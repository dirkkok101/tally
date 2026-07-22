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
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Features.Ledger.Transactions;

[SupportedOSPlatform("linux")]
public sealed class TransactionCorrectionTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private static readonly SafeActor Actor = new("human", "transaction-correction-test", "run-1");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-transaction-correction-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private TallyProcess process = null!;
    private TransactionStore transactionStore = null!;
    private TransactionCorrectionHandler correction = null!;
    private ReconciliationDecisionStore decisionStore = null!;
    private int sequence;

    // FR-LEDGER-TRANSACTION-CORRECTION
    [Theory]
    [InlineData(TransactionLifecycleAction.Void, false)]
    [InlineData(TransactionLifecycleAction.Superseded, true)]
    [InlineData(TransactionLifecycleAction.StatementAuthoritativeReplacement, true)]
    public void FR_LEDGER_TRANSACTION_CORRECTION_defines_closed_replacement_semantics(
        TransactionLifecycleAction action,
        bool requiresReplacement) =>
        Assert.Equal(requiresReplacement, TransactionLifecycle.RequiresReplacement(action));

    // FR-LEDGER-TRANSACTION-CORRECTION
    [Fact]
    public void FR_LEDGER_TRANSACTION_CORRECTION_exposes_closed_public_inputs()
    {
        Assert.NotNull(typeof(VoidTransactionInput));
        Assert.NotNull(typeof(SupersedeTransactionInput));
        Assert.NotNull(typeof(TransactionCorrectionResult));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_void_preserves_facts_evidence_and_attributable_history()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");

        var result = Success(await Void(original.TransactionId, "duplicate notification", "void"));

        Assert.Equal(TransactionLifecycleAction.Void, result.Action);
        Assert.Null(result.Replacement);
        Assert.Empty(result.RetiredRelationshipIds);
        Assert.Equal(TransactionLifecycleStatus.Voided, result.Original.LifecycleStatus);
        Assert.Null(result.Original.ActiveReplacementTransactionId);
        Assert.Equal(original.SignedAmount, result.Original.SignedAmount);
        Assert.Equal(original.OriginalDescription, result.Original.OriginalDescription);
        Assert.Equal(original.Evidence.Single().EvidenceId, result.Original.Evidence.Single().EvidenceId);
        var lifecycle = Assert.Single(result.Original.History!.Lifecycle);
        Assert.Equal(TransactionLifecycleAction.Void, lifecycle.Action);
        Assert.Equal("duplicate notification", lifecycle.Reason);
        Assert.Equal("human:transaction-correction-test:run-1", lifecycle.Actor);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_supersede_creates_an_independent_active_replacement()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var replacementInput = Replacement(account.AccountId, "-13.57");

        var result = Success(await Supersede(original.TransactionId, replacementInput, "correct amount", "supersede"));

        Assert.Equal(TransactionLifecycleAction.Superseded, result.Action);
        Assert.Equal(TransactionLifecycleStatus.Superseded, result.Original.LifecycleStatus);
        Assert.Equal(result.Replacement!.TransactionId, result.Original.ActiveReplacementTransactionId);
        Assert.Equal(TransactionLifecycleStatus.Active, result.Replacement.LifecycleStatus);
        Assert.Equal("-13.57", result.Replacement.SignedAmount);
        Assert.NotEqual(original.TransactionId, result.Replacement.TransactionId);
        var lifecycle = Assert.Single(result.Original.History!.Lifecycle);
        Assert.Equal(result.Replacement.TransactionId, lifecycle.ReplacementTransactionId);
        Assert.Null(lifecycle.ReconciliationDecisionId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_ordinary_supersession_does_not_copy_dimensions_or_evidence()
    {
        var account = await CreateAccount("Primary", "1111");
        var instrument = await CreateInstrument(account.AccountId, "original instrument", "1111");
        var cardholder = await CreateCardholder("Original owner");
        var original = await Record(account.AccountId, "-12.34", instrument.InstrumentId, cardholder.CardholderId);
        var category = await CreateCategory("Original category");
        var pool = await CreatePool("Company-paid personal");
        await AssignCategory(original.TransactionId, category.CategoryId);
        await AssignPool(original.TransactionId, original.Pool.PoolAssignmentEventId, pool.PoolId);
        var originalBefore = await Get(original.TransactionId);

        var result = Success(await Supersede(original.TransactionId, Replacement(account.AccountId, "-12.35"), "correct facts", "supersede"));
        var replacement = result.Replacement!;

        Assert.Equal(category.CategoryId, result.Original.Category.CategoryId);
        Assert.Equal(pool.PoolId, result.Original.Pool.PoolId);
        Assert.Equal(instrument.InstrumentId, result.Original.PaymentAttribution.InstrumentId);
        Assert.Equal(cardholder.CardholderId, result.Original.PaymentAttribution.CardholderId);
        Assert.Equal(originalBefore.Evidence.Single().EvidenceId, result.Original.Evidence.Single().EvidenceId);
        Assert.Equal(TransactionCategoryState.Uncategorized, replacement.Category.State);
        Assert.Equal(TransactionPoolState.Unassigned, replacement.Pool.State);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.CardholderState);
        Assert.NotEqual(result.Original.Evidence.Single().EvidenceId, replacement.Evidence.Single().EvidenceId);
        Assert.Single(replacement.History!.PaymentAttribution);
        Assert.Single(replacement.History.PoolAssignments);
        Assert.Empty(replacement.History.CategoryAssignments);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_replacement_accepts_only_explicit_new_attribution()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var instrument = await CreateInstrument(account.AccountId, "replacement instrument", "2222");
        var cardholder = await CreateCardholder("Replacement owner");
        var replacementInput = Replacement(account.AccountId, "-12.34", instrument.InstrumentId, cardholder.CardholderId);

        var result = Success(await Supersede(original.TransactionId, replacementInput, "add explicit attribution", "supersede"));

        Assert.Equal(instrument.InstrumentId, result.Replacement!.PaymentAttribution.InstrumentId);
        Assert.Equal(cardholder.CardholderId, result.Replacement.PaymentAttribution.CardholderId);
        Assert.Equal(2, result.Replacement.History!.PaymentAttribution.Count);
        Assert.Null(result.Original.PaymentAttribution.InstrumentId);
        Assert.Null(result.Original.PaymentAttribution.CardholderId);
    }

    [Theory]
    [InlineData("transfer", "void")]
    [InlineData("transfer", "supersede")]
    [InlineData("refund", "void")]
    [InlineData("refund", "supersede")]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_retires_active_financial_relationships_atomically(
        string relationshipType,
        string correctionAction)
    {
        var primary = await CreateAccount("Primary", "1111");
        var relatedAccount = relationshipType == "transfer"
            ? await CreateAccount("Savings", "2222")
            : primary;
        var original = await Record(primary.AccountId, "-12.34");
        var related = await Record(relatedAccount.AccountId, "12.34");
        var relationship = relationshipType == "transfer"
            ? await ConfirmTransfer(original.TransactionId, related.TransactionId)
            : await ConfirmRefund(original.TransactionId, related.TransactionId);

        var result = correctionAction == "void"
            ? Success(await Void(original.TransactionId, "retire relationship", $"{relationshipType}-void"))
            : Success(await Supersede(original.TransactionId, Replacement(primary.AccountId, "-12.34"), "retire relationship", $"{relationshipType}-supersede"));
        var current = await GetRelationship(relationship.RelationshipId);

        Assert.Equal([relationship.RelationshipId], result.RetiredRelationshipIds);
        Assert.Equal(FinancialRelationshipState.Retired, current.State);
        var lifecycle = Assert.Single(current.History);
        Assert.Equal(RelationshipLifecycleAction.Revoked, lifecycle.Action);
        Assert.Null(lifecycle.ReplacementRelationshipId);
        Assert.Equal("retire relationship", lifecycle.Reason);
    }

    [Theory]
    [InlineData("void")]
    [InlineData("supersede")]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_inactive_confirmed_target_is_reported_as_reconciliation_exception(string action)
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var statementEvidenceId = await SeedConfirmedDecision(original.TransactionId);

        if (action == "void")
            Success(await Void(original.TransactionId, "invalidate confirmation", "void-confirmed"));
        else
            Success(await Supersede(original.TransactionId, Replacement(account.AccountId, "-12.34"), "invalidate confirmation", "supersede-confirmed"));

        var decision = await decisionStore.GetAsync(statementEvidenceId, CancellationToken.None);
        Assert.NotNull(decision);
        Assert.Equal(ReconciliationDecisionCurrentState.Exception, decision.CurrentState);
        Assert.True(decision.RequiresOwnerReview);
        Assert.Equal(original.TransactionId, decision.ActiveTransactionId);
        Assert.NotNull(decision.ActiveConfirmingLinkEventId);
        Assert.Single(decision.History);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_same_key_replay_returns_the_original_void()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");

        var first = Success(await Void(original.TransactionId, "duplicate", "same-key"));
        var replay = Success(await Void(original.TransactionId, "duplicate", "same-key"));

        Assert.Equal(Serialize(first), Serialize(replay));
        Assert.Equal(1, await Count("transaction_lifecycle_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_exact_replay_returns_the_original_supersession()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var replacement = Replacement(account.AccountId, "-12.35");

        var first = Success(await Supersede(original.TransactionId, replacement, "correct amount", "first-key"));
        var replay = Success(await Supersede(original.TransactionId, replacement, "correct amount", "second-key"));

        Assert.Equal(first.Replacement!.TransactionId, replay.Replacement!.TransactionId);
        Assert.Equal(2, await Count("transaction_fact"));
        Assert.Equal(1, await Count("transaction_lifecycle_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_replay_returns_a_stable_conflict()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        Success(await Void(original.TransactionId, "duplicate", "same-key"));

        AssertError(await Void(original.TransactionId, "different reason", "same-key"), LedgerMutationExecutor.ConflictCode);
        AssertError(await Supersede(original.TransactionId, Replacement(account.AccountId, "-12.34"), "different action", "different-key"), LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(1, await Count("transaction_lifecycle_event"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_missing_and_inactive_transactions_are_stable()
    {
        AssertError(await Void(LedgerId.New().ToString(), "missing", "missing"), TransactionLifecycle.NotFoundError);
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        await Terminate(original.TransactionId);

        AssertError(await Void(original.TransactionId, "again", "inactive"), TransactionLifecycle.InactiveError);
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(1, await Count("transaction_lifecycle_event"));
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("reason")]
    [InlineData("actor")]
    [InlineData("key")]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_invalid_void_contract_is_atomic(string invalidField)
    {
        var transactionId = invalidField == "transaction" ? "invalid" : LedgerId.New().ToString();
        var reason = invalidField == "reason" ? "" : "owner correction";
        var actor = invalidField == "actor" ? null : Actor;
        var key = invalidField == "key" ? null : "invalid";

        AssertError(await correction.VoidAsync(new(transactionId, reason), actor, key, CancellationToken.None), TransactionLifecycle.InvalidError);
        Assert.Equal(0, await Count("transaction_lifecycle_event"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_invalid_replacement_account_rolls_back_every_effect()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var archived = await CreateAccount("Archived", "2222");
        await ArchiveAccount(archived.AccountId);
        var before = await MutationCounts();

        AssertError(await Supersede(original.TransactionId, Replacement(archived.AccountId, "-12.35"), "invalid account", "supersede"), AccountStore.ArchivedError);

        Assert.Equal(before, await MutationCounts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_duplicate_replacement_evidence_rolls_back_every_effect()
    {
        var account = await CreateAccount("Primary", "1111");
        var originalInput = Input(account.AccountId, "-12.34");
        var original = await Record(originalInput);
        var before = await MutationCounts();
        var replacement = originalInput with { SignedAmount = "-12.35" };

        AssertError(await Supersede(original.TransactionId, replacement, "duplicate evidence", "supersede"), TransactionErrors.EvidenceConflict);

        Assert.Equal(before, await MutationCounts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_incompatible_replacement_attribution_rolls_back_every_effect()
    {
        var account = await CreateAccount("Primary", "1111");
        var other = await CreateAccount("Other", "2222");
        var original = await Record(account.AccountId, "-12.34");
        var instrument = await CreateInstrument(other.AccountId, "other instrument", "2222");
        var before = await MutationCounts();

        AssertError(
            await Supersede(original.TransactionId, Replacement(account.AccountId, "-12.35", instrument.InstrumentId), "invalid attribution", "supersede"),
            TransactionErrors.AttributionIncompatible);

        Assert.Equal(before, await MutationCounts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
    }

    [Theory]
    [InlineData("void")]
    [InlineData("supersede")]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_relationship_retirement_failure_rolls_back_the_complete_correction(string action)
    {
        var primary = await CreateAccount("Primary", "1111");
        var savings = await CreateAccount("Savings", "2222");
        var original = await Record(primary.AccountId, "-12.34");
        var related = await Record(savings.AccountId, "12.34");
        var relationship = await ConfirmTransfer(original.TransactionId, related.TransactionId);
        var before = await MutationCounts();
        await using (var connection = await Open())
        {
            await Execute(connection, """
                CREATE TRIGGER fail_correction_relationship_retirement
                BEFORE INSERT ON relationship_lifecycle_event
                BEGIN SELECT RAISE(ABORT, 'injected relationship retirement failure'); END;
                """);
        }

        if (action == "void")
        {
            await Assert.ThrowsAsync<SqliteException>(() => Void(original.TransactionId, "retryable correction", "correction"));
        }
        else
        {
            var replacement = Replacement(primary.AccountId, "-12.35");
            await Assert.ThrowsAsync<SqliteException>(() => Supersede(original.TransactionId, replacement, "retryable correction", "correction"));
        }

        Assert.Equal(before, await MutationCounts());
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
        Assert.Equal(FinancialRelationshipState.Active, (await GetRelationship(relationship.RelationshipId)).State);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_invalid_replacement_facts_use_existing_transaction_errors()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");

        AssertError(
            await Supersede(original.TransactionId, Replacement(account.AccountId, "1.2"), "invalid amount", "supersede"),
            "amount.invalid");
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
    }

    [Fact]
    public void DD_LEDGER_RECONCILIATION_CONTRACT_statement_supersession_primitive_is_assembly_internal_and_transaction_scoped()
    {
        var method = typeof(TransactionStore).GetMethod("AppendStatementSupersessionAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.False(method.IsPublic);
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(SqliteTransaction));
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType.Name == "StatementSupersessionDecisionWriter");
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_unauthorized_statement_supersession_cannot_leave_an_orphan_replacement()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        Assert.True(TransactionFact.TryCreate(Replacement(account.AccountId, "-12.35"), out var statementFact, out _));
        var method = typeof(TransactionStore).GetMethod("AppendStatementSupersessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writerType = method.GetParameters().Single(parameter => parameter.ParameterType.Name == "StatementSupersessionDecisionWriter").ParameterType;
        var writerMethod = typeof(TransactionCorrectionTests).GetMethod(nameof(ReturnUnbackedDecision), BindingFlags.Static | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(writerType, writerMethod);
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);

        var invocation = (Task)method.Invoke(
            transactionStore,
            [
                connection,
                transaction,
                LedgerId.New().ToString(),
                original.TransactionId,
                LedgerId.New().ToString(),
                statementFact!,
                "statement correction",
                "system:reconciliation",
                At,
                writer,
                CancellationToken.None
            ])!;
        await invocation;
        var result = invocation.GetType().GetProperty("Result")!.GetValue(invocation)!;
        await transaction.CommitAsync();

        Assert.Equal(TransactionLifecycle.ReplacementConflictError, result.GetType().GetProperty("ErrorCode")!.GetValue(result));
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(TransactionLifecycleStatus.Active, (await Get(original.TransactionId)).LifecycleStatus);
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_authorized_statement_supersession_leaves_dimensions_for_explicit_carry_forward()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34");
        var evidenceId = await SeedStatementEvidence();
        Assert.True(TransactionFact.TryCreate(Replacement(account.AccountId, "-12.35"), out var statementFact, out _));
        var method = typeof(TransactionStore).GetMethod("AppendStatementSupersessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writerType = method.GetParameters().Single(parameter => parameter.ParameterType.Name == "StatementSupersessionDecisionWriter").ParameterType;
        var replacementId = LedgerId.New().ToString();
        var lifecycleId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var decisionWriter = new AuthorizedStatementDecisionWriter(connection, transaction, original.TransactionId, evidenceId);
        var writer = Delegate.CreateDelegate(
            writerType,
            decisionWriter,
            typeof(AuthorizedStatementDecisionWriter).GetMethod(nameof(AuthorizedStatementDecisionWriter.WriteAsync))!);

        var invocation = (Task)method.Invoke(
            transactionStore,
            [
                connection,
                transaction,
                lifecycleId,
                original.TransactionId,
                replacementId,
                statementFact!,
                "statement authority",
                "system:reconciliation",
                At,
                writer,
                CancellationToken.None
            ])!;
        await invocation;
        var result = invocation.GetType().GetProperty("Result")!.GetValue(invocation)!;
        await transaction.CommitAsync();

        Assert.Null(result.GetType().GetProperty("ErrorCode")!.GetValue(result));
        Assert.Equal(replacementId, result.GetType().GetProperty("ReplacementTransactionId")!.GetValue(result));
        Assert.Equal(decisionWriter.DecisionId, result.GetType().GetProperty("DecisionId")!.GetValue(result));
        var corrected = await Get(original.TransactionId);
        Assert.Equal(TransactionLifecycleStatus.Superseded, corrected.LifecycleStatus);
        Assert.Equal(replacementId, corrected.ActiveReplacementTransactionId);
        var lifecycle = Assert.Single(corrected.History!.Lifecycle);
        Assert.Equal(TransactionLifecycleAction.StatementAuthoritativeReplacement, lifecycle.Action);
        Assert.Equal(decisionWriter.DecisionId, lifecycle.ReconciliationDecisionId);
        Assert.Equal(1, await CountWhere("transaction_fact", "transaction_id", replacementId));
        Assert.Equal(0, await CountWhere("pool_assignment_event", "transaction_id", replacementId));
        Assert.Equal(0, await CountWhere("transaction_attribution_event", "transaction_id", replacementId));
        Assert.Null(await transactionStore.GetAsync(replacementId, true, CancellationToken.None));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new LedgerConnectionFactory(new HostArtifactProtection());
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var accountStore = new AccountStore(database, factory);
        var paymentIdentityStore = new PaymentIdentityStore(database, factory);
        var evidenceStore = new EvidenceStore(database, factory);
        transactionStore = new TransactionStore(database, factory);
        correction = new(
            executor,
            accountStore,
            paymentIdentityStore,
            evidenceStore,
            transactionStore,
            new RelationshipStore(database, factory));
        decisionStore = new(database, factory, evidenceStore, transactionStore);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<AccountDetail> CreateAccount(string name, string suffix)
    {
        var input = new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + suffix, "ZAR");
        return ProcessSuccess(
            await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), "account-" + suffix),
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId) => Run(
        "ledger.account.archive",
        JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, "archive for correction test"), LedgerJsonContext.Default.ArchiveAccountInput),
        "archive-account-" + ++sequence);

    private async Task<CategoryDetail> CreateCategory(string name) => ProcessSuccess(
        await Run(
            "ledger.category.create",
            JsonSerializer.SerializeToElement(new CreateCategoryInput(name), LedgerJsonContext.Default.CreateCategoryInput),
            "category-" + ++sequence),
        LedgerJsonContext.Default.CategoryDetail);

    private async Task<SpendPoolDetail> CreatePool(string name) => ProcessSuccess(
        await Run(
            "ledger.pool.create",
            JsonSerializer.SerializeToElement(new CreateSpendPoolInput(name), LedgerJsonContext.Default.CreateSpendPoolInput),
            "pool-" + ++sequence),
        LedgerJsonContext.Default.SpendPoolDetail);

    private async Task<PaymentInstrumentDetail> CreateInstrument(string accountId, string name, string suffix) => ProcessSuccess(
        await Run(
            "ledger.instrument.create",
            JsonSerializer.SerializeToElement(new CreatePaymentInstrumentInput(name, accountId, suffix), LedgerJsonContext.Default.CreatePaymentInstrumentInput),
            "instrument-" + ++sequence),
        LedgerJsonContext.Default.PaymentInstrumentDetail);

    private async Task<CardholderDetail> CreateCardholder(string name) => ProcessSuccess(
        await Run(
            "ledger.cardholder.create",
            JsonSerializer.SerializeToElement(new CreateCardholderInput(name), LedgerJsonContext.Default.CreateCardholderInput),
            "cardholder-" + ++sequence),
        LedgerJsonContext.Default.CardholderDetail);

    private async Task AssignCategory(string transactionId, string categoryId) => ProcessSuccess(
        await Run(
            "ledger.transaction.category.assign",
            JsonSerializer.SerializeToElement(new AssignCategoryInput(transactionId, categoryId, "owner category"), LedgerJsonContext.Default.AssignCategoryInput),
            "assign-category-" + ++sequence),
        LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task AssignPool(string transactionId, string expectedEventId, string poolId) => ProcessSuccess(
        await Run(
            "ledger.transaction.pool.assign",
            JsonSerializer.SerializeToElement(
                new AssignPoolInput(transactionId, expectedEventId, new(TransactionPoolState.Assigned, poolId), "owner pool"),
                LedgerJsonContext.Default.AssignPoolInput),
            "assign-pool-" + ++sequence),
        LedgerJsonContext.Default.PoolAssignmentResult);

    private Task<TransactionDetail> Record(
        string accountId,
        string amount,
        string? instrumentId = null,
        string? cardholderId = null) => Record(Input(accountId, amount, instrumentId, cardholderId));

    private async Task<TransactionDetail> Record(RecordTransactionInput input) => ProcessSuccess(
        await Run(
            "ledger.transaction.record",
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput),
            "record-" + ++sequence),
        LedgerJsonContext.Default.TransactionDetail);

    private RecordTransactionInput Input(
        string accountId,
        string amount,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var seed = ++sequence;
        return new(
            accountId,
            amount,
            "ZAR",
            "2026-07-01",
            null,
            "Owner-safe banking transaction",
            instrumentId,
            cardholderId,
            new(EvidenceKind.AgentCapture, Digest(seed), "capture:" + seed, null, null));
    }

    private RecordTransactionInput Replacement(
        string accountId,
        string amount,
        string? instrumentId = null,
        string? cardholderId = null) => Input(accountId, amount, instrumentId, cardholderId) with
        {
            OriginalDescription = "Corrected owner-safe banking transaction"
        };

    private Task<CommandResult<JsonElement>> Void(string transactionId, string reason, string key) =>
        correction.VoidAsync(new(transactionId, reason), Actor, key, CancellationToken.None);

    private Task<CommandResult<JsonElement>> Supersede(
        string transactionId,
        RecordTransactionInput replacement,
        string reason,
        string key) => correction.SupersedeAsync(new(transactionId, replacement, reason), Actor, key, CancellationToken.None);

    private async Task<FinancialRelationshipDetail> ConfirmTransfer(string outflowId, string inflowId) => ProcessSuccess(
        await Run(
            "ledger.transfer.confirm",
            JsonSerializer.SerializeToElement(new ConfirmTransferInput(outflowId, inflowId, "owner confirmed transfer"), LedgerJsonContext.Default.ConfirmTransferInput),
            "transfer-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<FinancialRelationshipDetail> ConfirmRefund(string originalId, string refundId) => ProcessSuccess(
        await Run(
            "ledger.refund.confirm",
            JsonSerializer.SerializeToElement(new ConfirmRefundInput(originalId, refundId, "owner confirmed refund"), LedgerJsonContext.Default.ConfirmRefundInput),
            "refund-" + ++sequence),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<FinancialRelationshipDetail> GetRelationship(string relationshipId) => ProcessSuccess(
        await Run(
            "ledger.relationship.get",
            JsonSerializer.SerializeToElement(new GetRelationshipInput(relationshipId, true), LedgerJsonContext.Default.GetRelationshipInput),
            null),
        LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<TransactionDetail> Get(string transactionId) =>
        (await transactionStore.GetAsync(transactionId, true, CancellationToken.None))!;

    private async Task<string> SeedConfirmedDecision(string transactionId)
    {
        var evidenceId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        var linkId = LedgerId.New().ToString();
        await using var connection = await Open();
        await Execute(connection, """
            INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, 'statement:test', NULL, 'system:test', $at);
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $transactionId, 'owner_confirmed', NULL, NULL,
                    'owner reviewed match', 0, 'owner confirmed statement row', 'owner:dirk', $at, NULL);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'owner_confirmed_match', NULL, $transactionId,
                    'owner', 'statement:test', 'v2', $at);
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $transactionId, 'confirming', 'link', $decisionId,
                    'owner confirmed statement row', 'owner:dirk', $at, NULL);
            """,
            ("$evidenceId", evidenceId),
            ("$digest", Digest(++sequence)),
            ("$decisionId", decisionId),
            ("$transactionId", transactionId),
            ("$linkId", linkId),
            ("$at", At));
        return evidenceId;
    }

    private async Task<string> SeedStatementEvidence()
    {
        var evidenceId = LedgerId.New().ToString();
        await using var connection = await Open();
        await Execute(
            connection,
            "INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, 'statement:test', NULL, 'system:test', $at);",
            ("$evidenceId", evidenceId),
            ("$digest", Digest(++sequence)),
            ("$at", At));
        return evidenceId;
    }

    private async Task Terminate(string transactionId)
    {
        await using var connection = await Open();
        await Execute(
            connection,
            "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, 'void', NULL, NULL, 'test', 'system:test', $at);",
            ("$eventId", LedgerId.New().ToString()),
            ("$transactionId", transactionId),
            ("$at", At));
    }

    private async Task<(long Facts, long Lifecycle, long Evidence, long Links, long Relationships)> MutationCounts() =>
        (await Count("transaction_fact"), await Count("transaction_lifecycle_event"), await Count("evidence_record"), await Count("evidence_link_event"), await Count("relationship_lifecycle_event"));

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountWhere(string table, string column, string value)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $value;";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> Open() =>
        await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", Actor, input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static TransactionCorrectionResult Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value, TransactionCorrectionJsonContext.Default.TransactionCorrectionResult)!;
    }

    private static T ProcessSuccess<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(CommandResult<JsonElement> result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
    }

    private static string Serialize(TransactionCorrectionResult result) =>
        JsonSerializer.Serialize(result, TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private static Task<string?> ReturnUnbackedDecision(string replacementTransactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(LedgerId.New().ToString());
    }

    private sealed class AuthorizedStatementDecisionWriter(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originalTransactionId,
        string evidenceId)
    {
        public string? DecisionId { get; private set; }

        public async Task<string?> WriteAsync(string replacementTransactionId, CancellationToken cancellationToken)
        {
            DecisionId = LedgerId.New().ToString();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reconciliation_decision(
                    decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                    match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
                VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', NULL, NULL,
                        'statement authority', 0, 'corrected from statement', 'system:reconciliation', $at, NULL);
                INSERT INTO reconciliation_decision_authority(
                    decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                    authority_kind, statement_authority_basis, schema_origin, recorded_at)
                VALUES ($decisionId, 'corrected_from_statement', $originalId, $replacementId,
                        'owner', 'statement:test', 'v2', $at);
                """;
            command.Parameters.AddWithValue("$decisionId", DecisionId);
            command.Parameters.AddWithValue("$evidenceId", evidenceId);
            command.Parameters.AddWithValue("$originalId", originalTransactionId);
            command.Parameters.AddWithValue("$replacementId", replacementTransactionId);
            command.Parameters.AddWithValue("$at", At);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return DecisionId;
        }
    }

    private static string Digest(int seed) =>
        string.Concat(Enumerable.Repeat((seed % 256).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
}
