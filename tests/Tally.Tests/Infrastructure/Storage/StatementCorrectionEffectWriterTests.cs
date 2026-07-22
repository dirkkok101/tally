using System.Reflection;
using System.Runtime.Versioning;
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
using Tally.Domain.Ledger.Relationships;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class StatementCorrectionEffectWriterTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private static readonly SafeActor Actor = new("human", "statement-effect-test", "run-1");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-statement-effect-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private TallyProcess process = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationDecisionStore decisionStore = null!;
    private RelationshipStore relationshipStore = null!;
    private StatementCorrectionEffectWriter writer = null!;
    private int sequence;

    [Fact]
    public void DM_LEDGER_RECONCILIATION_HISTORY_exposes_one_caller_transaction_scoped_append_boundary()
    {
        var methods = typeof(StatementCorrectionEffectWriter).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(StatementCorrectionEffectWriter.AppendAsync))
            .ToArray();

        var append = Assert.Single(methods);
        Assert.Contains(append.GetParameters(), parameter => parameter.ParameterType == typeof(SqliteConnection));
        Assert.Contains(append.GetParameters(), parameter => parameter.ParameterType == typeof(SqliteTransaction));
        Assert.DoesNotContain(append.GetParameters(), parameter => parameter.ParameterType == typeof(LedgerDb));
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_appends_one_complete_uncategorized_correction_effect()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var write = await CreateWrite(source, "-12.35");

        var result = await AppendAndCommit(write);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.ReviewRequired);
        Assert.Equal(write.CorrectionId, result.CorrectionId);
        Assert.Equal(write.DecisionId, result.DecisionId);
        Assert.Equal(write.ReplacementTransactionId, result.ReplacementTransactionId);
        Assert.Null(result.CategoryAllocationEventId);
        Assert.Equal(PaymentAttributionCarryForwardResolution.CarryForward, result.PaymentResolution);
        Assert.Empty(result.RelationshipLifecycleEventIds);
        var prior = await GetTransaction(source.TransactionId);
        var replacement = await GetTransaction(write.ReplacementTransactionId);
        Assert.Equal(TransactionLifecycleStatus.Superseded, prior.LifecycleStatus);
        Assert.Equal(write.ReplacementTransactionId, prior.ActiveReplacementTransactionId);
        Assert.Equal(TransactionLifecycleStatus.Active, replacement.LifecycleStatus);
        Assert.Equal("-12.35", replacement.SignedAmount);
        Assert.Equal(TransactionCategoryState.Uncategorized, replacement.Category.State);
        Assert.Equal(TransactionPoolState.Unassigned, replacement.Pool.State);
        Assert.Equal(TransactionAssignmentAction.CarryForward, Assert.Single(replacement.History!.PoolAssignments).Action);
        Assert.Equal(TransactionAssignmentAction.CarryForward, Assert.Single(replacement.History.PaymentAttribution).Action);
        var evidence = Assert.Single(replacement.Evidence);
        Assert.Equal(EvidenceKind.StatementRow, evidence.Kind);
        Assert.Equal(EvidenceLinkRole.Confirming, evidence.Role);
        var decision = (await decisionStore.GetAsync(write.EvidenceId, CancellationToken.None))!;
        Assert.Equal(ReconciliationDecisionCurrentState.CorrectedFromStatement, decision.CurrentState);
        Assert.Equal(write.ReplacementTransactionId, decision.ActiveTransactionId);
        Assert.Equal(write.ConfirmingLinkEventId, decision.ActiveConfirmingLinkEventId);
        Assert.Equal(result.PoolAssignmentEventId, decision.History.Single().CarryForward!.PoolAssignmentEventId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_explicit_category_and_pool_carry_forward_preserve_lineage()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var category = await CreateCategory("Groceries");
        var pool = await CreatePool("Company-paid personal");
        await AssignCategory(source.TransactionId, category.CategoryId);
        await AssignPool(source.TransactionId, source.Pool.PoolAssignmentEventId, pool.PoolId);
        var write = await CreateWrite(source, "-12.35");

        var result = await AppendAndCommit(write);
        var replacement = await GetTransaction(write.ReplacementTransactionId);

        Assert.NotNull(result.CategoryAllocationEventId);
        Assert.Equal(category.CategoryId, replacement.Category.CategoryId);
        Assert.Equal(pool.PoolId, replacement.Pool.PoolId);
        var categoryHistory = Assert.Single(replacement.History!.CategoryAssignments);
        Assert.Equal(TransactionCategoryAction.CarryForward, categoryHistory.Action);
        Assert.Equal(source.TransactionId, categoryHistory.SourceTransactionId);
        Assert.Equal(write.DecisionId, categoryHistory.ReconciliationDecisionId);
        var poolHistory = Assert.Single(replacement.History.PoolAssignments);
        Assert.Equal(TransactionAssignmentAction.CarryForward, poolHistory.Action);
        Assert.Equal(source.TransactionId, poolHistory.SourceTransactionId);
        Assert.Equal(write.DecisionId, poolHistory.ReconciliationDecisionId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_compatible_payment_attribution_is_carried_forward_explicitly()
    {
        var account = await CreateAccount("Primary", "1111");
        var instrument = await CreateInstrument(account.AccountId, "Primary card", "1111");
        var cardholder = await CreateCardholder("Owner");
        var source = await Record(account.AccountId, "-12.34", instrument.InstrumentId, cardholder.CardholderId);
        var write = await CreateWrite(source, "-12.35");

        var result = await AppendAndCommit(write);
        var replacement = await GetTransaction(write.ReplacementTransactionId);

        Assert.Equal(PaymentAttributionCarryForwardResolution.CarryForward, result.PaymentResolution);
        Assert.Equal(instrument.InstrumentId, replacement.PaymentAttribution.InstrumentId);
        Assert.Equal(cardholder.CardholderId, replacement.PaymentAttribution.CardholderId);
        var history = Assert.Single(replacement.History!.PaymentAttribution);
        Assert.Equal(TransactionAssignmentAction.CarryForward, history.Action);
        Assert.Equal(source.TransactionId, history.SourceTransactionId);
        Assert.Equal(write.DecisionId, history.ReconciliationDecisionId);
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_incompatible_payment_attribution_becomes_explicit_unknown_with_review_metadata()
    {
        var account = await CreateAccount("Primary", "1111");
        var instrument = await CreateInstrument(account.AccountId, "Archived card", "1111");
        var source = await Record(account.AccountId, "-12.34", instrument.InstrumentId);
        await ArchiveInstrument(instrument.InstrumentId);
        var write = await CreateWrite(source, "-12.35");

        var result = await AppendAndCommit(write);
        var replacement = await GetTransaction(write.ReplacementTransactionId);

        Assert.Equal(PaymentAttributionCarryForwardResolution.UnknownInitialization, result.PaymentResolution);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.CardholderState);
        var history = Assert.Single(replacement.History!.PaymentAttribution);
        Assert.Equal(TransactionAssignmentAction.Initialize, history.Action);
        Assert.Null(history.SourceTransactionId);
        Assert.Null(history.ReconciliationDecisionId);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM statement_unknown_attribution_authority WHERE attribution_event_id = $id;", ("$id", result.AttributionEventId!)));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_replaces_an_invariant_preserving_relationship(string relationshipType)
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
        var write = await CreateWrite(source, "-12.34", [relationship.RelationshipId]);

        var result = await AppendAndCommit(write);
        var lifecycleId = Assert.Single(result.RelationshipLifecycleEventIds);
        var retired = (await relationshipStore.GetAsync(relationship.RelationshipId, true, CancellationToken.None))!;
        var activeId = Assert.Single(retired.History).ReplacementRelationshipId!;
        var active = (await relationshipStore.GetAsync(activeId, true, CancellationToken.None))!;

        Assert.Equal(lifecycleId, retired.History.Single().LifecycleEventId);
        Assert.Equal(FinancialRelationshipState.Retired, retired.State);
        Assert.Equal(FinancialRelationshipState.Active, active.State);
        Assert.Equal(write.DecisionId, active.ReconciliationDecisionId);
        Assert.Contains(write.ReplacementTransactionId, new[] { active.SourceTransactionId, active.TargetTransactionId });
        Assert.DoesNotContain(source.TransactionId, new[] { active.SourceTransactionId, active.TargetTransactionId });
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM statement_correction_relationship_event WHERE correction_id = $id;", ("$id", write.CorrectionId)));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    [InlineData("missing")]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_relationship_review_block_rolls_back_every_effect(string failure)
    {
        var account = await CreateAccount("Primary", "1111");
        var counterpartAccount = failure == "transfer"
            ? await CreateAccount("Savings", "2222")
            : account;
        var source = await Record(account.AccountId, "-12.34");
        var counterpart = await Record(counterpartAccount.AccountId, "12.34");
        var relationship = failure switch
        {
            "transfer" => await ConfirmTransfer(source.TransactionId, counterpart.TransactionId),
            "refund" => await ConfirmRefund(source.TransactionId, counterpart.TransactionId),
            _ => null
        };
        var relationshipId = relationship?.RelationshipId ?? LedgerId.New().ToString();
        var write = await CreateWrite(source, failure == "missing" ? "-12.34" : "-13.00", [relationshipId]);
        var before = await Counts();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);

        var result = await writer.AppendAsync(connection, transaction, write, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.ReviewRequired);
        Assert.Equal(failure == "missing" ? RelationshipLifecycleErrors.NotFound : failure == "transfer" ? TransferErrors.Amount : RefundErrors.Amount, result.ErrorCode);
        Assert.Equal(before, await Counts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await GetTransaction(source.TransactionId)).LifecycleStatus);
        if (relationship is not null)
            Assert.Equal(FinancialRelationshipState.Active, (await relationshipStore.GetAsync(relationship.RelationshipId, true, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData("confirming_link")]
    [InlineData("category")]
    [InlineData("pool")]
    [InlineData("payment")]
    [InlineData("correction")]
    public async Task DD_LEDGER_ATOMIC_DURABLE_MUTATIONS_injected_collaborator_failure_rolls_back_every_effect(string boundary)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var category = await CreateCategory("Rollback category");
        await AssignCategory(source.TransactionId, category.CategoryId);
        var write = await CreateWrite(source, "-12.35");
        var before = await Counts();
        await CreateFailureTrigger(boundary);
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<SqliteException>(() => writer.AppendAsync(connection, transaction, write, CancellationToken.None));
        await transaction.CommitAsync();

        Assert.Equal(before, await Counts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await GetTransaction(source.TransactionId)).LifecycleStatus);
        Assert.Null(await transactionStore.GetAsync(write.ReplacementTransactionId, true, CancellationToken.None));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_exact_effect_replay_returns_the_persisted_result()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var write = await CreateWrite(source, "-12.35");
        var first = await AppendAndCommit(write);
        var before = await Counts();

        var replay = await AppendAndCommit(write);

        Assert.Equal(first with { RelationshipLifecycleEventIds = replay.RelationshipLifecycleEventIds }, replay);
        Assert.Equal(first.RelationshipLifecycleEventIds, replay.RelationshipLifecycleEventIds);
        Assert.Equal(before, await Counts());
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("relationship")]
    [InlineData("previous_link")]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_effect_replay_returns_a_stable_conflict(string changed)
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var write = await CreateWrite(source, "-12.35");
        await AppendAndCommit(write);
        var before = await Counts();
        var conflicting = changed switch
        {
            "reason" => write with { Reason = "changed reason" },
            "relationship" => write with { RelationshipIds = [LedgerId.New().ToString()] },
            "previous_link" => write with { PreviousConfirmingLinkEventId = LedgerId.New().ToString() },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        var result = await AppendAndCommit(conflicting);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatementCorrectionEffectErrors.Conflict, result.ErrorCode);
        Assert.Equal(before, await Counts());
    }

    [Fact]
    public async Task DD_LEDGER_ATOMIC_DURABLE_MUTATIONS_caller_rollback_discards_the_complete_effect()
    {
        var account = await CreateAccount("Primary", "1111");
        var source = await Record(account.AccountId, "-12.34");
        var write = await CreateWrite(source, "-12.35");
        var before = await Counts();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);

        var result = await writer.AppendAsync(connection, transaction, write, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await transaction.RollbackAsync();

        Assert.Equal(before, await Counts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await GetTransaction(source.TransactionId)).LifecycleStatus);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new LedgerConnectionFactory(new HostArtifactProtection());
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        var evidenceStore = new EvidenceStore(database, factory);
        transactionStore = new TransactionStore(database, factory);
        decisionStore = new(database, factory, evidenceStore, transactionStore);
        relationshipStore = new(database, factory);
        writer = new(
            new ReconciliationWriteStore(evidenceStore, transactionStore),
            decisionStore,
            transactionStore,
            new CategoryAllocationStore(database, factory),
            new PaymentAttributionStore(),
            new PaymentIdentityStore(database, factory),
            new PoolAssignmentStore(),
            relationshipStore);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<StatementCorrectionEffectResult> AppendAndCommit(StatementCorrectionEffectWrite write)
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await writer.AppendAsync(connection, transaction, write, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private async Task<StatementCorrectionEffectWrite> CreateWrite(
        TransactionDetail source,
        string amount,
        IReadOnlyList<string>? relationshipIds = null)
    {
        var evidenceInput = new RegisterEvidenceInput(
            EvidenceKind.StatementRow,
            Digest(++sequence),
            "statement:" + sequence,
            Digest(++sequence),
            new(source.AccountId, Minor(amount), "ZAR", "2026-07-01", null, null, null, Digest(++sequence)));
        var evidence = await RegisterEvidence(evidenceInput);
        var factInput = new RecordTransactionInput(
            source.AccountId,
            amount,
            "ZAR",
            "2026-07-01",
            null,
            "Statement-authoritative banking transaction",
            null,
            null,
            evidenceInput);
        Assert.True(TransactionFact.TryCreate(factInput, out var fact, out var error), error);
        return new(
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            evidence.EvidenceId,
            source.TransactionId,
            LedgerId.New().ToString(),
            fact!,
            null,
            null,
            "manual-review-v1",
            "1.0",
            "owner reviewed authoritative statement correction",
            "scope:test|evidence:" + evidence.ContentFingerprint,
            "owner approved statement correction",
            "human:statement-effect-test:run-1",
            At,
            relationshipIds ?? []);
    }

    private async Task<AccountDetail> CreateAccount(string name, string suffix)
    {
        var input = new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + suffix, "ZAR");
        return Success(
            await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), "account-" + suffix),
            LedgerJsonContext.Default.AccountDetail);
    }

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
            JsonSerializer.SerializeToElement(new CreatePaymentInstrumentInput(name, accountId, suffix), LedgerJsonContext.Default.CreatePaymentInstrumentInput),
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
                JsonSerializer.SerializeToElement(new ArchivePaymentInstrumentInput(instrumentId, "archive for compatibility test"), LedgerJsonContext.Default.ArchivePaymentInstrumentInput),
                "archive-instrument-" + ++sequence),
            LedgerJsonContext.Default.PaymentInstrumentLifecycleResult);
    }

    private async Task AssignCategory(string transactionId, string categoryId)
    {
        _ = Success(
            await Run(
                "ledger.transaction.category.assign",
                JsonSerializer.SerializeToElement(new AssignCategoryInput(transactionId, categoryId, "owner category"), LedgerJsonContext.Default.AssignCategoryInput),
                "assign-category-" + ++sequence),
            LedgerJsonContext.Default.CategoryAllocationResult);
    }

    private async Task AssignPool(string transactionId, string expectedEventId, string poolId)
    {
        _ = Success(
            await Run(
                "ledger.transaction.pool.assign",
                JsonSerializer.SerializeToElement(
                    new AssignPoolInput(transactionId, expectedEventId, new(TransactionPoolState.Assigned, poolId), "owner pool"),
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
            "Owner-safe banking transaction",
            instrumentId,
            cardholderId,
            new(EvidenceKind.AgentCapture, Digest(seed), "capture:" + seed, null, null));
        return Success(
            await Run(
                "ledger.transaction.record",
                JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput),
                "record-" + seed),
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<EvidenceRecordDetail> RegisterEvidence(RegisterEvidenceInput input) => Success(
        await Run(
            "ledger.evidence.register",
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RegisterEvidenceInput),
            "evidence-" + ++sequence),
        LedgerJsonContext.Default.EvidenceRecordDetail);

    private async Task<FinancialRelationshipDetail> ConfirmTransfer(string outflowId, string inflowId) => Success(
        await Run(
            "ledger.transfer.confirm",
            JsonSerializer.SerializeToElement(new ConfirmTransferInput(outflowId, inflowId, "owner confirmed transfer"), LedgerJsonContext.Default.ConfirmTransferInput),
            "transfer-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<FinancialRelationshipDetail> ConfirmRefund(string originalId, string refundId) => Success(
        await Run(
            "ledger.refund.confirm",
            JsonSerializer.SerializeToElement(new ConfirmRefundInput(originalId, refundId, "owner confirmed refund"), LedgerJsonContext.Default.ConfirmRefundInput),
            "refund-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<TransactionDetail> GetTransaction(string transactionId) =>
        (await transactionStore.GetAsync(transactionId, true, CancellationToken.None))!;

    private async Task CreateFailureTrigger(string boundary)
    {
        var (table, condition) = boundary switch
        {
            "confirming_link" => ("evidence_link_event", "WHEN NEW.role = 'confirming'"),
            "category" => ("category_allocation_event", "WHEN NEW.action = 'carry_forward'"),
            "pool" => ("pool_assignment_event", "WHEN NEW.action = 'carry_forward'"),
            "payment" => ("transaction_attribution_event", "WHEN NEW.action IN ('carry_forward', 'initialize')"),
            "correction" => ("statement_correction", string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        await using var connection = await Open();
        await Execute(connection, $"""
            CREATE TRIGGER fail_statement_effect_{boundary}
            BEFORE INSERT ON {table} {condition}
            BEGIN SELECT RAISE(ABORT, 'injected statement effect failure'); END;
            """);
    }

    private async Task<EffectCounts> Counts()
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM transaction_fact),
                (SELECT COUNT(*) FROM transaction_lifecycle_event),
                (SELECT COUNT(*) FROM reconciliation_decision),
                (SELECT COUNT(*) FROM reconciliation_decision_authority),
                (SELECT COUNT(*) FROM evidence_link_event),
                (SELECT COUNT(*) FROM category_allocation_event),
                (SELECT COUNT(*) FROM pool_assignment_event),
                (SELECT COUNT(*) FROM transaction_attribution_event),
                (SELECT COUNT(*) FROM statement_unknown_attribution_authority),
                (SELECT COUNT(*) FROM financial_relationship),
                (SELECT COUNT(*) FROM relationship_lifecycle_event),
                (SELECT COUNT(*) FROM statement_correction),
                (SELECT COUNT(*) FROM statement_correction_relationship_event);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12));
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

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", Actor, input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static long Minor(string amount)
    {
        Assert.True(Money.TryParse(amount, out var money, out var error), error);
        return money.MinorUnits;
    }

    private static string Digest(int seed) =>
        string.Concat(Enumerable.Repeat((seed % 256).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));

    private sealed record EffectCounts(
        long Facts,
        long TransactionLifecycle,
        long Decisions,
        long Authorities,
        long EvidenceLinks,
        long Categories,
        long Pools,
        long Attributions,
        long UnknownAttributions,
        long Relationships,
        long RelationshipLifecycle,
        long Corrections,
        long CorrectionRelationships);
}
