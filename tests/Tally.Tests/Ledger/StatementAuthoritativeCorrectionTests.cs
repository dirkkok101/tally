using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Relationships;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Reconciliation;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class StatementAuthoritativeCorrectionTests : IAsyncLifetime
{
    private static readonly SafeActor Actor = new("human", "statement-correction-test", "run-1");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-statement-correction-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private TallyProcess process = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationProjectionStore projectionStore = null!;
    private ReconciliationDecisionStore decisionStore = null!;
    private RelationshipStore relationshipStore = null!;
    private ReconciliationApplyOperationModule module = null!;
    private int sequence;
    [Fact]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_owner_correction_normalizes_one_reviewed_target()
    {
        var input = ValidInput();

        var accepted = StatementAuthorityPolicy.TryNormalize(input, out var normalized, out var error);

        Assert.True(accepted, error);
        Assert.NotNull(normalized);
        Assert.Equal(input.TargetTransactionId, normalized.TargetTransactionId);
        Assert.Equal(input.StatementFact, normalized.StatementFact);
        Assert.Equal(ReconciliationAuthorityKind.Owner, normalized.AuthorityKind);
    }

    [Fact]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_correction_candidate_identity_is_canonical()
    {
        var input = ValidInput();
        var other = LedgerId.New().ToString();

        Assert.True(StatementAuthorityPolicy.TryNormalize(
            input with { ReviewedCandidateIds = [other, input.TargetTransactionId!] },
            out var normalized,
            out var error), error);

        Assert.Equal(new[] { other, input.TargetTransactionId! }.Order(StringComparer.Ordinal), normalized!.ReviewedCandidateIds);
        Assert.Equal(normalized.CanonicalInput(), normalized.CanonicalInput());
    }

    [Fact]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_automatic_correction_remains_review_required()
    {
        var accepted = StatementAuthorityPolicy.TryNormalize(
            ValidInput() with { AuthorityKind = ReconciliationAuthorityKind.DeterministicPolicy },
            out var normalized,
            out var error);

        Assert.False(accepted);
        Assert.Null(normalized);
        Assert.Equal(ReconciliationApplyErrors.ReviewRequired, error);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("fact")]
    [InlineData("candidates")]
    [InlineData("exception")]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_rejects_an_incomplete_correction_shape(string changed)
    {
        var input = ValidInput();
        input = changed switch
        {
            "target" => input with { TargetTransactionId = null },
            "fact" => input with { StatementFact = null },
            "candidates" => input with { ReviewedCandidateIds = [] },
            "exception" => input with { ExceptionCode = "NOT-ALLOWED" },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        Assert.False(StatementAuthorityPolicy.TryNormalize(input, out _, out var error));
        Assert.Equal(ReconciliationApplyErrors.InvalidInput, error);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_appends_one_statement_authoritative_replacement()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");

        var result = Success(await Apply(statement, source.TransactionId));
        var correction = Assert.IsType<StatementCorrectionApplyResult>(result.Correction);
        var prior = await GetTransaction(source.TransactionId);
        var replacement = await GetTransaction(correction.ReplacementTransactionId);

        Assert.Equal(correction.ReplacementTransactionId, result.ActiveTransactionId);
        Assert.Equal(TransactionLifecycleStatus.Superseded, prior.LifecycleStatus);
        Assert.Equal(correction.ReplacementTransactionId, prior.ActiveReplacementTransactionId);
        Assert.Equal(TransactionLifecycleStatus.Active, replacement.LifecycleStatus);
        Assert.Equal(TransactionReconciliationState.StatementReconciled, replacement.ReconciliationState);
        Assert.Equal("-12.34", replacement.SignedAmount);
        Assert.Equal("Statement-authoritative banking transaction", replacement.OriginalDescription);
        Assert.Contains(prior.Evidence, evidence => evidence.Kind == EvidenceKind.AgentCapture && evidence.Role == EvidenceLinkRole.Supporting);
        Assert.Contains(replacement.Evidence, evidence => evidence.EvidenceId == statement.EvidenceId && evidence.Role == EvidenceLinkRole.Confirming);
        Assert.Equal(1, await Scalar("""
            SELECT COUNT(*) FROM transaction_fact AS fact
            WHERE fact.transaction_id IN ($prior, $replacement)
              AND NOT EXISTS (SELECT 1 FROM transaction_lifecycle_event AS lifecycle WHERE lifecycle.transaction_id = fact.transaction_id);
            """, ("$prior", source.TransactionId), ("$replacement", correction.ReplacementTransactionId)));
        var decision = (await decisionStore.GetAsync(statement.EvidenceId, CancellationToken.None))!;
        Assert.Equal(ReconciliationDecisionCurrentState.CorrectedFromStatement, decision.CurrentState);
        Assert.Equal(correction.CorrectionId, decision.History.Single().CarryForward!.CorrectionId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_carries_category_and_pool_with_explicit_lineage()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var category = await CreateCategory("Groceries");
        var pool = await CreatePool("Company-paid personal");
        await AssignCategory(source.TransactionId, category.CategoryId);
        await AssignPool(source.TransactionId, source.Pool.PoolAssignmentEventId, pool.PoolId);
        var statement = await SeedStatement(account.AccountId, "-12.34");

        var correction = Success(await Apply(statement, source.TransactionId)).Correction!;
        var replacement = await GetTransaction(correction.ReplacementTransactionId);

        Assert.NotNull(correction.CategoryAllocationEventId);
        Assert.Equal(category.CategoryId, replacement.Category.CategoryId);
        Assert.Equal(pool.PoolId, replacement.Pool.PoolId);
        Assert.Equal(source.TransactionId, Assert.Single(replacement.History!.CategoryAssignments).SourceTransactionId);
        Assert.Equal(source.TransactionId, Assert.Single(replacement.History.PoolAssignments).SourceTransactionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_carries_compatible_payment_or_records_explicit_unknown(bool archiveInstrument)
    {
        var account = await CreateAccount("Primary", "1111");
        var instrument = await CreateInstrument(account.AccountId, "Primary card", "1111");
        var cardholder = await CreateCardholder("Owner");
        var source = await Record(account.AccountId, "-12.30", instrument.InstrumentId, cardholder.CardholderId);
        if (archiveInstrument) await ArchiveInstrument(instrument.InstrumentId);
        var statement = await SeedStatement(account.AccountId, "-12.34");

        var correction = Success(await Apply(statement, source.TransactionId)).Correction!;
        var replacement = await GetTransaction(correction.ReplacementTransactionId);

        if (archiveInstrument)
        {
            Assert.Equal(PaymentAttributionCarryForwardResolution.UnknownInitialization, correction.PaymentResolution);
            Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.InstrumentState);
            Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.CardholderState);
            Assert.Equal(1, await Scalar(
                "SELECT COUNT(*) FROM statement_unknown_attribution_authority WHERE attribution_event_id = $id;",
                ("$id", correction.AttributionEventId)));
        }
        else
        {
            Assert.Equal(PaymentAttributionCarryForwardResolution.CarryForward, correction.PaymentResolution);
            Assert.Equal(instrument.InstrumentId, replacement.PaymentAttribution.InstrumentId);
            Assert.Equal(cardholder.CardholderId, replacement.PaymentAttribution.CardholderId);
            Assert.Equal(source.TransactionId, Assert.Single(replacement.History!.PaymentAttribution).SourceTransactionId);
        }
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_replaces_an_invariant_preserving_relationship(string relationshipType)
    {
        var account = await CreateAccount("Primary", "1111");
        var counterpartAccount = relationshipType == "transfer"
            ? await CreateAccount("Savings", "2222")
            : account;
        var source = await Record(account.AccountId, "-12.34");
        var counterpart = await Record(counterpartAccount.AccountId, "12.34");
        var relationship = relationshipType == "transfer"
            ? await ConfirmTransfer(source.TransactionId, counterpart.TransactionId)
            : await ConfirmRefund(source.TransactionId, counterpart.TransactionId);
        var statement = await SeedStatement(account.AccountId, "-12.34");

        var correction = Success(await Apply(statement, source.TransactionId)).Correction!;
        var lifecycleId = Assert.Single(correction.RelationshipLifecycleEventIds);
        var retired = (await relationshipStore.GetAsync(relationship.RelationshipId, true, CancellationToken.None))!;
        var active = (await relationshipStore.GetAsync(retired.History.Single().ReplacementRelationshipId!, true, CancellationToken.None))!;

        Assert.Equal(lifecycleId, retired.History.Single().LifecycleEventId);
        Assert.Equal(FinancialRelationshipState.Retired, retired.State);
        Assert.Equal(FinancialRelationshipState.Active, active.State);
        Assert.Equal(correction.ReplacementTransactionId, active.SourceTransactionId);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_invalid_relationship_requires_review_and_rolls_back(string relationshipType)
    {
        var account = await CreateAccount("Primary", "1111");
        var counterpartAccount = relationshipType == "transfer"
            ? await CreateAccount("Savings", "2222")
            : account;
        var source = await Record(account.AccountId, "-12.34");
        var counterpart = await Record(counterpartAccount.AccountId, "12.34");
        var relationship = relationshipType == "transfer"
            ? await ConfirmTransfer(source.TransactionId, counterpart.TransactionId)
            : await ConfirmRefund(source.TransactionId, counterpart.TransactionId);
        var statement = await SeedStatement(account.AccountId, "-13.00");
        var before = await MutationCounts();

        var result = await Apply(statement, source.TransactionId);

        AssertError(result, RelationshipLifecycleErrors.ReviewRequired);
        Assert.Equal(before, await MutationCounts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await GetTransaction(source.TransactionId)).LifecycleStatus);
        Assert.Equal(FinancialRelationshipState.Active, (await relationshipStore.GetAsync(relationship.RelationshipId, true, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_automatic_correction_remains_review_required_without_mutation()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var before = await MutationCounts();

        var result = await Apply(statement, source.TransactionId, authority: ReconciliationAuthorityKind.DeterministicPolicy);

        AssertError(result, ReconciliationApplyErrors.ReviewRequired);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("date")]
    [InlineData("posting")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_authoritative_fact_must_equal_registered_evidence(string changed)
    {
        var account = await CreateAccount("Primary", "1111");
        var other = changed == "account" ? await CreateAccount("Other", "2222") : account;
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34", postingDate: "2026-07-02");
        var fact = statement.Fact with
        {
            AccountId = other.AccountId,
            SignedAmount = changed == "amount" ? "-99.00" : statement.Fact.SignedAmount,
            CurrencyCode = changed == "currency" ? "USD" : statement.Fact.CurrencyCode,
            TransactionDate = changed == "date" ? "2026-07-03" : statement.Fact.TransactionDate,
            PostingDate = changed == "posting" ? "2026-07-04" : statement.Fact.PostingDate
        };
        var before = await MutationCounts();

        var result = await Apply(statement, source.TransactionId, fact: fact);

        AssertError(result, changed == "currency" ? ReconciliationApplyErrors.InvalidInput : ReconciliationApplyErrors.StatementFactMismatch);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("fingerprint", ReconciliationApplyErrors.EvidenceFingerprintChanged)]
    [InlineData("token", ReconciliationApplyErrors.ProjectionChanged)]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_stale_review_material(
        string changed,
        string expectedError)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var before = await MutationCounts();

        var result = await Apply(
            statement,
            source.TransactionId,
            fingerprint: changed == "fingerprint" ? Digest() : null,
            token: changed == "token" ? Digest() : null);

        AssertError(result, expectedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_an_incomplete_reviewed_candidate_set()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");

        AssertError(
            await Apply(statement, source.TransactionId, candidates: [source.TransactionId]),
            ReconciliationApplyErrors.CandidateSetChanged);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_revalidates_the_account_inside_the_writer_transaction()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        await ArchiveAccount(account.AccountId);
        var before = await MutationCounts();

        var result = await Apply(statement, source.TransactionId);

        AssertError(result, AccountStore.ArchivedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_same_key_replay_returns_the_original_correction()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var first = Success(await Apply(statement, source.TransactionId, key: "same-key"));
        var before = await MutationCounts();

        var replay = Success(await Apply(statement, source.TransactionId, key: "same-key", captured: first));

        Assert.Equal(
            JsonSerializer.Serialize(first, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult),
            JsonSerializer.Serialize(replay, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult));
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_exact_replay_returns_the_original_correction()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var first = Success(await Apply(statement, source.TransactionId, key: "first-key"));
        var before = await MutationCounts();

        var replay = Success(await Apply(statement, source.TransactionId, key: "second-key", captured: first));

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(
            JsonSerializer.Serialize(first.Correction, ReconciliationApplyJsonContext.Default.StatementCorrectionApplyResult),
            JsonSerializer.Serialize(replay.Correction, ReconciliationApplyJsonContext.Default.StatementCorrectionApplyResult));
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_replay_conflicts_without_a_second_effect(bool differentKey)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var first = Success(await Apply(statement, source.TransactionId, key: "first-key"));
        var before = await MutationCounts();

        var result = await Apply(
            statement,
            source.TransactionId,
            key: differentKey ? "second-key" : "first-key",
            reason: "changed reason",
            captured: first);

        AssertError(result, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("rawPayload")]
    [InlineData("recipient")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_correction_contract_rejects_transport_and_payload_fields(string field)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var projection = await Projection(statement);
        var input = Input(statement, source.TransactionId, projection);
        var json = JsonSerializer.SerializeToElement(input, ReconciliationApplyJsonContext.Default.ReconciliationApplyInput).GetRawText();
        json = json.TrimEnd('}') + $",\"{field}\":\"forbidden\"}}";

        var result = await module.ApplyAsync(
            new(JsonDocument.Parse(json).RootElement.Clone(), Actor, "privacy-key"),
            CancellationToken.None);

        AssertError(result, ReconciliationApplyErrors.InvalidInput);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_requires_actor_and_idempotency_key(bool hasActor, bool hasKey)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.30");
        var statement = await SeedStatement(account.AccountId, "-12.34");
        var projection = await Projection(statement);
        var input = Input(statement, source.TransactionId, projection);

        var result = await module.ApplyAsync(
            new(
                JsonSerializer.SerializeToElement(input, ReconciliationApplyJsonContext.Default.ReconciliationApplyInput),
                hasActor ? Actor : null,
                hasKey ? "required-key" : null),
            CancellationToken.None);

        AssertError(result, ReconciliationApplyErrors.InvalidInput);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        projectionStore = new(database, factory, evidenceStore, transactionStore);
        decisionStore = new(database, factory, evidenceStore, transactionStore);
        relationshipStore = new(database, factory);
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var writeStore = new ReconciliationWriteStore(evidenceStore, transactionStore);
        var effectWriter = new StatementCorrectionEffectWriter(
            writeStore,
            decisionStore,
            transactionStore,
            new CategoryAllocationStore(database, factory),
            new PaymentAttributionStore(),
            new PaymentIdentityStore(database, factory),
            new PoolAssignmentStore(),
            relationshipStore);
        var coordinator = new StatementAuthoritativeCorrectionCoordinator(
            executor,
            accountStore,
            projectionStore,
            writeStore,
            transactionStore,
            effectWriter);
        module = new(
            new ReconciliationApplyHandler(executor, accountStore, projectionStore, writeStore),
            coordinator);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<CommandResult<JsonElement>> Apply(
        StatementFixture statement,
        string targetTransactionId,
        IReadOnlyList<string>? candidates = null,
        AuthoritativeStatementFact? fact = null,
        ReconciliationAuthorityKind authority = ReconciliationAuthorityKind.Owner,
        string? fingerprint = null,
        string? token = null,
        string key = "correction-key",
        string reason = "owner approved statement correction",
        ReconciliationApplyResult? captured = null)
    {
        var projection = captured is null ? await Projection(statement) : null;
        var input = new ReconciliationApplyInput(
            statement.EvidenceId,
            fingerprint ?? statement.Fingerprint,
            statement.ScopeId,
            token ?? captured?.ProjectionToken ?? projection!.AdvisoryToken,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            authority,
            candidates ?? captured?.ReviewedCandidateIds ?? CandidateIds(projection!),
            targetTransactionId,
            fact ?? statement.Fact,
            null,
            reason);
        return await module.ApplyAsync(
            new(
                JsonSerializer.SerializeToElement(input, ReconciliationApplyJsonContext.Default.ReconciliationApplyInput),
                Actor,
                key),
            CancellationToken.None);
    }

    private static ReconciliationApplyInput Input(
        StatementFixture statement,
        string targetTransactionId,
        ReconciliationProjectionResult projection) => new(
            statement.EvidenceId,
            statement.Fingerprint,
            statement.ScopeId,
            projection.AdvisoryToken,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            ReconciliationAuthorityKind.Owner,
            CandidateIds(projection),
            targetTransactionId,
            statement.Fact,
            null,
            "owner approved statement correction");

    private async Task<ReconciliationProjectionResult> Projection(StatementFixture statement)
    {
        var read = await projectionStore.ReadAsync(statement.EvidenceId, statement.ScopeId, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        return ManualReviewProjectionV1.Project(read.Source!);
    }

    private static string[] CandidateIds(ReconciliationProjectionResult projection) =>
        projection.ExactCandidates.Concat(projection.GuardCandidates)
            .Select(candidate => candidate.TransactionId)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private async Task<StatementFixture> SeedStatement(
        string accountId,
        string amount,
        string transactionDate = "2026-07-01",
        string? postingDate = null)
    {
        Assert.True(Money.TryParse(amount, out var money, out var moneyError), moneyError);
        var fingerprint = Digest();
        var observation = new EvidenceObservation(
            accountId,
            money.MinorUnits,
            "ZAR",
            transactionDate,
            postingDate,
            null,
            null,
            Digest());
        var input = new RegisterEvidenceInput(
            EvidenceKind.StatementRow,
            Digest(),
            "statement:correction:" + ++sequence,
            fingerprint,
            observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out var evidenceError), evidenceError);
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var evidence = await evidenceStore.RegisterInitialAsync(
            connection,
            transaction,
            identity!,
            input,
            "test:statement-correction",
            At(2),
            CancellationToken.None);
        var scopeId = LedgerId.New().ToString();
        await Execute(connection, transaction, """
            INSERT INTO statement_scope(
                scope_id, account_id, period_start, period_end, manifest_opaque_reference,
                status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', $manifest,
                    'open', 'test:statement-correction', $at);
            INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);
            """,
            ("$scopeId", scopeId),
            ("$accountId", accountId),
            ("$manifest", "statement:manifest:" + sequence),
            ("$at", At(2)),
            ("$evidenceId", evidence.EvidenceId));
        await transaction.CommitAsync();
        return new(
            evidence.EvidenceId,
            scopeId,
            accountId,
            fingerprint,
            new(
                accountId,
                money.ToString(),
                "ZAR",
                transactionDate,
                postingDate,
                "Statement-authoritative banking transaction"));
    }

    private async Task<AccountDetail> CreateAccount(string name, string suffix) => Success(
        await Run(
            "ledger.account.create",
            JsonSerializer.SerializeToElement(
                new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + suffix, "ZAR"),
                LedgerJsonContext.Default.CreateAccountInput),
            "account-" + ++sequence),
        LedgerJsonContext.Default.AccountDetail);

    private async Task<CategoryDetail> CreateCategory(string name) => Success(
        await Run(
            "ledger.category.create",
            JsonSerializer.SerializeToElement(new CreateCategoryInput(name), LedgerJsonContext.Default.CreateCategoryInput),
            "category-" + ++sequence),
        LedgerJsonContext.Default.CategoryDetail);

    private async Task<SpendPoolDetail> CreatePool(string name) => Success(
        await Run(
            "ledger.pool.create",
            JsonSerializer.SerializeToElement(new CreateSpendPoolInput(name), LedgerJsonContext.Default.CreateSpendPoolInput),
            "pool-" + ++sequence),
        LedgerJsonContext.Default.SpendPoolDetail);

    private async Task<PaymentInstrumentDetail> CreateInstrument(string accountId, string name, string suffix) => Success(
        await Run(
            "ledger.instrument.create",
            JsonSerializer.SerializeToElement(
                new CreatePaymentInstrumentInput(name, accountId, suffix),
                LedgerJsonContext.Default.CreatePaymentInstrumentInput),
            "instrument-" + ++sequence),
        LedgerJsonContext.Default.PaymentInstrumentDetail);

    private async Task<CardholderDetail> CreateCardholder(string name) => Success(
        await Run(
            "ledger.cardholder.create",
            JsonSerializer.SerializeToElement(new CreateCardholderInput(name), LedgerJsonContext.Default.CreateCardholderInput),
            "cardholder-" + ++sequence),
        LedgerJsonContext.Default.CardholderDetail);

    private async Task ArchiveInstrument(string instrumentId)
    {
        _ = Success(
            await Run(
                "ledger.instrument.archive",
                JsonSerializer.SerializeToElement(
                    new ArchivePaymentInstrumentInput(instrumentId, "archive for correction test"),
                    LedgerJsonContext.Default.ArchivePaymentInstrumentInput),
                "archive-instrument-" + ++sequence),
            LedgerJsonContext.Default.PaymentInstrumentLifecycleResult);
    }

    private async Task ArchiveAccount(string accountId)
    {
        _ = Success(
            await Run(
                "ledger.account.archive",
                JsonSerializer.SerializeToElement(
                    new ArchiveAccountInput(accountId, "archive for correction test"),
                    LedgerJsonContext.Default.ArchiveAccountInput),
                "archive-account-" + ++sequence),
            LedgerJsonContext.Default.AccountLifecycleResult);
    }

    private async Task AssignCategory(string transactionId, string categoryId)
    {
        _ = Success(
            await Run(
                "ledger.transaction.category.assign",
                JsonSerializer.SerializeToElement(
                    new AssignCategoryInput(transactionId, categoryId, "owner category"),
                    LedgerJsonContext.Default.AssignCategoryInput),
                "assign-category-" + ++sequence),
            LedgerJsonContext.Default.CategoryAllocationResult);
    }

    private async Task AssignPool(string transactionId, string expectedEventId, string poolId)
    {
        _ = Success(
            await Run(
                "ledger.transaction.pool.assign",
                JsonSerializer.SerializeToElement(
                    new AssignPoolInput(
                        transactionId,
                        expectedEventId,
                        new(TransactionPoolState.Assigned, poolId),
                        "owner pool"),
                    LedgerJsonContext.Default.AssignPoolInput),
                "assign-pool-" + ++sequence),
            LedgerJsonContext.Default.PoolAssignmentResult);
    }

    private async Task<TransactionDetail> Record(
        string accountId,
        string amount,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var seed = ++sequence;
        var input = new RecordTransactionInput(
            accountId,
            amount,
            "ZAR",
            "2026-07-01",
            null,
            "Agent-captured banking transaction",
            instrumentId,
            cardholderId,
            new(EvidenceKind.AgentCapture, Digest(), "capture:" + seed, null, null));
        return Success(
            await Run(
                "ledger.transaction.record",
                JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput),
                "record-" + seed),
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<FinancialRelationshipDetail> ConfirmTransfer(string outflowId, string inflowId) => Success(
        await Run(
            "ledger.transfer.confirm",
            JsonSerializer.SerializeToElement(
                new ConfirmTransferInput(outflowId, inflowId, "owner confirmed transfer"),
                LedgerJsonContext.Default.ConfirmTransferInput),
            "transfer-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<FinancialRelationshipDetail> ConfirmRefund(string originalId, string refundId) => Success(
        await Run(
            "ledger.refund.confirm",
            JsonSerializer.SerializeToElement(
                new ConfirmRefundInput(originalId, refundId, "owner confirmed full refund"),
                LedgerJsonContext.Default.ConfirmRefundInput),
            "refund-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<TransactionDetail> GetTransaction(string transactionId) =>
        (await transactionStore.GetAsync(transactionId, true, CancellationToken.None))!;

    private async Task<IReadOnlyDictionary<string, long>> MutationCounts()
    {
        var tables = new[]
        {
            "transaction_fact", "transaction_lifecycle_event", "reconciliation_decision",
            "reconciliation_decision_authority", "evidence_link_event", "category_allocation_event",
            "pool_assignment_event", "transaction_attribution_event", "statement_unknown_attribution_authority",
            "financial_relationship", "relationship_lifecycle_event", "statement_correction",
            "statement_correction_relationship_event", "idempotency_record", "logical_effect"
        };
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables) counts.Add(table, await Scalar($"SELECT COUNT(*) FROM {table};"));
        return counts;
    }

    private async Task<long> Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> Open() =>
        await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", Actor, input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static ReconciliationApplyResult Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult)!;
    }

    private static void AssertError(CommandResult<JsonElement> result, string expected)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorCode);
    }

    private static ReconciliationApplyInput ValidInput()
    {
        var accountId = LedgerId.New().ToString();
        var targetId = LedgerId.New().ToString();
        return new(
            LedgerId.New().ToString(),
            Digest(),
            LedgerId.New().ToString(),
            Digest(),
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            ReconciliationAuthorityKind.Owner,
            [targetId],
            targetId,
            new(accountId, "-12.34", "ZAR", "2026-07-01", null, "Statement transaction"),
            null,
            "owner approved statement correction");
    }

    private static string Digest() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();

    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";

    private sealed record StatementFixture(
        string EvidenceId,
        string ScopeId,
        string AccountId,
        string Fingerprint,
        AuthoritativeStatementFact Fact);
}
