using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.Ledger.Dimensions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Dimensions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class PoolAssignmentOperationTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-pool-assignment-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private PoolAssignmentStore store = null!;

    [Fact]
    public void DM_LEDGER_ATTRIBUTION_POOL_CONTRACTS_registry_exposes_pool_assignment_operations()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(typeof(AssignPoolInput), registry.Find("ledger.transaction.pool.assign")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(CorrectPoolInput), registry.Find("ledger.transaction.pool.correct")!.RequestTypeInfo.Type);
        Assert.All(
            new[] { "ledger.transaction.pool.assign", "ledger.transaction.pool.correct" },
            operation => Assert.Equal(typeof(PoolAssignmentResult), registry.Find(operation)!.ResultTypeInfo.Type));
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_transaction_initializes_atomic_unassigned_projection()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 1);

        Assert.True(LedgerId.TryParse(transaction.Pool.PoolAssignmentEventId, out _, out _));
        Assert.Equal(TransactionPoolState.Unassigned, transaction.Pool.State);
        Assert.Null(transaction.Pool.PoolId);
        var history = Assert.Single(transaction.History!.PoolAssignments);
        Assert.Equal(TransactionAssignmentAction.Initialize, history.Action);
        Assert.Equal(transaction.Pool.PoolAssignmentEventId, history.PoolAssignmentEventId);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_assigns_one_active_pool_without_changing_other_dimensions()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 2);
        var pool = await CreatePool("Personal after-tax", "pool");

        var result = Assignment(await Assign(transaction, TransactionPoolState.Assigned, pool.PoolId, "owner selected pool", "assign"));

        Assert.Equal(TransactionPoolState.Assigned, result.Transaction.Pool.State);
        Assert.Equal(pool.PoolId, result.Transaction.Pool.PoolId);
        Assert.Equal(result.PoolAssignmentEventId, result.Transaction.Pool.PoolAssignmentEventId);
        Assert.Equal(TransactionCategoryState.Uncategorized, result.Transaction.Category.State);
        Assert.Equal(TransactionKnowledgeState.Unknown, result.Transaction.PaymentAttribution.InstrumentState);
        Assert.Single(result.Transaction.Evidence);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, result.Transaction.ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_correction_replaces_pool_and_preserves_history_and_counts()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 3);
        var first = await CreatePool("Company discretionary", "first-pool");
        var second = await CreatePool("Personal after-tax", "second-pool");
        var assigned = Assignment(await Assign(transaction, TransactionPoolState.Assigned, first.PoolId, "initial", "assign"));

        var corrected = Assignment(await Correct(assigned.Transaction, TransactionPoolState.Assigned, second.PoolId, "owner correction", "correct"));

        Assert.Equal(second.PoolId, corrected.Transaction.Pool.PoolId);
        Assert.Collection(
            corrected.Transaction.History!.PoolAssignments,
            item => Assert.Equal(TransactionAssignmentAction.Initialize, item.Action),
            item => Assert.Equal(TransactionAssignmentAction.Assign, item.Action),
            item =>
            {
                Assert.Equal(TransactionAssignmentAction.Correct, item.Action);
                Assert.Equal(assigned.PoolAssignmentEventId, item.PreviousEventId);
                Assert.Equal("owner correction", item.Reason);
                Assert.Equal("human:pool-assignment-test", item.Actor);
            });
        Assert.Equal(0, (await GetPool(first.PoolId)).CurrentAssignmentCount);
        Assert.Equal(1, (await GetPool(first.PoolId)).HistoricalAssignmentCount);
        Assert.Equal(1, (await GetPool(second.PoolId)).CurrentAssignmentCount);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_correction_can_make_pool_explicitly_unassigned()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 4);
        var pool = await CreatePool("Personal", "pool");
        var assigned = Assignment(await Assign(transaction, TransactionPoolState.Assigned, pool.PoolId, "initial", "assign"));

        var corrected = Assignment(await Correct(assigned.Transaction, TransactionPoolState.Unassigned, null, "pool withdrawn", "correct"));

        Assert.Equal(TransactionPoolState.Unassigned, corrected.Transaction.Pool.State);
        Assert.Null(corrected.Transaction.Pool.PoolId);
    }

    [Theory]
    [InlineData("assigned-without-id")]
    [InlineData("unassigned-with-id")]
    [InlineData("blank-reason")]
    [InlineData("invalid-expected-event")]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_invalid_selection_is_atomic(string scenario)
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 5);
        var pool = await CreatePool("Personal", "pool");
        var assignment = scenario switch
        {
            "assigned-without-id" => new PoolAssignmentInput(TransactionPoolState.Assigned),
            "unassigned-with-id" => new PoolAssignmentInput(TransactionPoolState.Unassigned, pool.PoolId),
            _ => new PoolAssignmentInput(TransactionPoolState.Assigned, pool.PoolId)
        };
        var input = new AssignPoolInput(
            transaction.TransactionId,
            scenario == "invalid-expected-event" ? "not-an-id" : transaction.Pool.PoolAssignmentEventId,
            assignment,
            scenario == "blank-reason" ? "" : "reason");

        AssertError(await Assign(input, "invalid-" + scenario), 3, PoolAssignmentPolicy.InvalidError);
        Assert.Single((await Get(transaction.TransactionId)).History!.PoolAssignments);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_closed_schema_rejects_missing_state_and_multiple_pool_fields()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 6);
        var pool = await CreatePool("Personal", "pool");
        var missingState = JsonDocument.Parse($$"""
            {"transactionId":"{{transaction.TransactionId}}","expectedPoolAssignmentEventId":"{{transaction.Pool.PoolAssignmentEventId}}","assignment":{"poolId":"{{pool.PoolId}}"},"reason":"state required"}
            """).RootElement.Clone();
        var multiplePools = JsonDocument.Parse($$"""
            {"transactionId":"{{transaction.TransactionId}}","expectedPoolAssignmentEventId":"{{transaction.Pool.PoolAssignmentEventId}}","assignment":{"state":"assigned","poolId":"{{pool.PoolId}}","poolIds":["{{pool.PoolId}}"]},"reason":"one pool only"}
            """).RootElement.Clone();

        AssertError(await Run("ledger.transaction.pool.assign", missingState, "missing-state"), 3, "validation.invalid_input");
        AssertError(await Run("ledger.transaction.pool.assign", multiplePools, "multiple-pools"), 3, "validation.invalid_input");
        Assert.Single((await Get(transaction.TransactionId)).History!.PoolAssignments);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_missing_and_archived_pools_are_rejected()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 7);
        var archived = await CreatePool("Archived", "pool");
        await ArchivePool(archived.PoolId);

        AssertError(await Assign(transaction, TransactionPoolState.Assigned, LedgerId.New().ToString(), "missing", "missing"), 4, SpendPoolErrors.NotFound);
        AssertError(await Assign(transaction, TransactionPoolState.Assigned, archived.PoolId, "archived", "archived"), 6, SpendPoolErrors.Archived);
        Assert.Single((await Get(transaction.TransactionId)).History!.PoolAssignments);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_missing_transaction_is_rejected()
    {
        var pool = await CreatePool("Personal", "pool");
        var input = new AssignPoolInput(
            LedgerId.New().ToString(), LedgerId.New().ToString(),
            new(TransactionPoolState.Assigned, pool.PoolId), "missing transaction");

        AssertError(await Assign(input, "missing-transaction"), 4, TransactionErrors.NotFound);
        Assert.Equal(0, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_inactive_transaction_changes_nothing()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 8);
        var pool = await CreatePool("Personal", "pool");
        await Terminate(transaction.TransactionId, "void", null, null);

        AssertError(await Assign(transaction, TransactionPoolState.Assigned, pool.PoolId, "late", "late"), 6, PoolAssignmentErrors.TransactionInactive);
        Assert.Equal(1, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_stale_repeated_and_unchanged_requests_are_rejected()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 9);
        var pool = await CreatePool("Personal", "pool");
        var assigned = Assignment(await Assign(transaction, TransactionPoolState.Assigned, pool.PoolId, "assign", "assign"));

        AssertError(await Correct(transaction, TransactionPoolState.Unassigned, null, "stale", "stale"), 5, PoolAssignmentErrors.Stale);
        AssertError(await Correct(assigned.Transaction, TransactionPoolState.Assigned, pool.PoolId, "same", "same"), 5, PoolAssignmentErrors.Unchanged);
        AssertError(await Assign(assigned.Transaction, TransactionPoolState.Unassigned, null, "assign again", "again"), 5, PoolAssignmentErrors.AlreadyAssigned);
        Assert.Equal(2, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_replay_returns_original_and_changed_input_conflicts()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 10);
        var pool = await CreatePool("Personal", "pool");
        var input = new AssignPoolInput(
            transaction.TransactionId, transaction.Pool.PoolAssignmentEventId,
            new(TransactionPoolState.Assigned, pool.PoolId), "owner choice");
        var original = Assignment(await Assign(input, "same"));

        Assert.Equal(original.PoolAssignmentEventId, Assignment(await Assign(input, "same")).PoolAssignmentEventId);
        AssertError(await Assign(input with { Reason = "changed" }, "same"), 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(2, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_statement_correction_carries_assigned_pool()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 11);
        var pool = await CreatePool("Personal", "pool");
        source = Assignment(await Assign(source, TransactionPoolState.Assigned, pool.PoolId, "known", "assign")).Transaction;
        var replacementId = await CreateReplacementFact(account.AccountId);
        var decisionId = await AuthorizeStatementCorrection(source.TransactionId, replacementId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var result = await store.CarryForwardAsync(
            connection, transaction, source.TransactionId, replacementId, decisionId,
            "statement correction", "system:reconciliation", At, CancellationToken.None);
        await transaction.CommitAsync();

        var replacement = await Get(replacementId);
        Assert.Equal(pool.PoolId, replacement.Pool.PoolId);
        var history = Assert.Single(replacement.History!.PoolAssignments);
        Assert.Equal(result.PoolAssignmentEventId, history.PoolAssignmentEventId);
        Assert.Equal(source.TransactionId, history.SourceTransactionId);
        Assert.Equal(decisionId, history.ReconciliationDecisionId);
        Assert.Equal(TransactionAssignmentAction.CarryForward, history.Action);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_statement_correction_carries_explicit_unassigned_state()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 12);
        var replacementId = await CreateReplacementFact(account.AccountId);
        var decisionId = await AuthorizeStatementCorrection(source.TransactionId, replacementId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await store.CarryForwardAsync(
            connection, transaction, source.TransactionId, replacementId, decisionId,
            "statement correction", "system:reconciliation", At, CancellationToken.None);
        await transaction.CommitAsync();

        var replacement = await Get(replacementId);
        Assert.Equal(TransactionPoolState.Unassigned, replacement.Pool.State);
        Assert.Null(replacement.Pool.PoolId);
        Assert.Equal(TransactionAssignmentAction.CarryForward, Assert.Single(replacement.History!.PoolAssignments).Action);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_pool_carry_requires_statement_correction_authority()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 13);
        var replacementId = await CreateReplacementFact(account.AccountId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CarryForwardAsync(
            connection, transaction, source.TransactionId, replacementId, LedgerId.New().ToString(),
            "unauthorized", "system:test", At, CancellationToken.None));
        await transaction.RollbackAsync();

        Assert.Equal(1, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_ordinary_supersession_does_not_inherit_pool()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 14);
        var replacement = await Record(account.AccountId, 15);
        var pool = await CreatePool("Personal", "pool");
        source = Assignment(await Assign(source, TransactionPoolState.Assigned, pool.PoolId, "known", "assign")).Transaction;

        await Terminate(source.TransactionId, "superseded", replacement.TransactionId, null);

        replacement = await Get(replacement.TransactionId);
        Assert.Equal(TransactionPoolState.Unassigned, replacement.Pool.State);
        Assert.Null(replacement.Pool.PoolId);
        Assert.Single(replacement.History!.PoolAssignments);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_pool_assignment_events_reject_update_and_delete()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 16);
        var pool = await CreatePool("Personal", "pool");
        await Assign(transaction, TransactionPoolState.Assigned, pool.PoolId, "owner", "assign");

        await using var connection = await Open();
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE pool_assignment_event SET reason = 'changed';"))).SqliteErrorCode > 0);
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM pool_assignment_event;"))).SqliteErrorCode > 0);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        store = new PoolAssignmentStore();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<AccountDetail> CreateAccount()
    {
        var input = new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), "account"), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<SpendPoolDetail> CreatePool(string name, string key)
    {
        var input = new CreateSpendPoolInput(name);
        return Success(await Run("ledger.pool.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateSpendPoolInput), key), LedgerJsonContext.Default.SpendPoolDetail);
    }

    private Task<ProcessResult> ArchivePool(string poolId) => Run(
        "ledger.pool.archive",
        JsonSerializer.SerializeToElement(new ArchiveSpendPoolInput(poolId, "archive for test"), LedgerJsonContext.Default.ArchiveSpendPoolInput),
        "archive-pool");

    private async Task<SpendPoolDetail> GetPool(string poolId) => Success(
        await Run("ledger.pool.get", JsonSerializer.SerializeToElement(new GetSpendPoolInput(poolId), LedgerJsonContext.Default.GetSpendPoolInput), null),
        LedgerJsonContext.Default.SpendPoolDetail);

    private async Task<TransactionDetail> Record(string accountId, int digestSeed)
    {
        var digest = digestSeed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        var input = new RecordTransactionInput(
            accountId, "-12.34", "ZAR", "2026-07-01", null, "Owner-safe purchase", null, null,
            new(EvidenceKind.AgentCapture, string.Concat(Enumerable.Repeat(digest, 32)), "capture:" + digestSeed, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + digestSeed), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Assign(TransactionDetail transaction, TransactionPoolState state, string? poolId, string reason, string key) =>
        Assign(new(transaction.TransactionId, transaction.Pool.PoolAssignmentEventId, new(state, poolId), reason), key);

    private Task<ProcessResult> Assign(AssignPoolInput input, string key) => Run(
        "ledger.transaction.pool.assign", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.AssignPoolInput), key);

    private Task<ProcessResult> Correct(TransactionDetail transaction, TransactionPoolState state, string? poolId, string reason, string key) => Run(
        "ledger.transaction.pool.correct",
        JsonSerializer.SerializeToElement(new CorrectPoolInput(transaction.TransactionId, transaction.Pool.PoolAssignmentEventId, new(state, poolId), reason), LedgerJsonContext.Default.CorrectPoolInput),
        key);

    private async Task<TransactionDetail> Get(string transactionId) => Success(
        await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, true), LedgerJsonContext.Default.GetTransactionInput), null),
        LedgerJsonContext.Default.TransactionDetail);

    private async Task<string> CreateReplacementFact(string accountId)
    {
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transactionId, $accountId, -1234, 'ZAR', '2026-07-01', NULL, 'Statement replacement', $at, 'test');
            INSERT INTO transaction_attribution_event (
                attribution_event_id, transaction_id, instrument_state, instrument_id, cardholder_state, cardholder_id,
                action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($attributionEventId, $transactionId, 'unknown', NULL, 'unknown', NULL,
                    'initialize', NULL, NULL, NULL, 'initialize', 'system:test', $at);
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$attributionEventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
        return transactionId;
    }

    private async Task<string> AuthorizeStatementCorrection(string sourceId, string replacementId)
    {
        var evidenceId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, NULL, NULL, 'system:test', $at);
            INSERT INTO reconciliation_decision (
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', NULL, NULL,
                    'statement authority', 0, 'corrected from statement', 'system:reconciliation', $at, NULL);
            INSERT INTO reconciliation_decision_authority (
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'corrected_from_statement', $sourceId, $replacementId,
                    'owner', 'statement row authority', 'v2', $at);
            INSERT INTO transaction_lifecycle_event (
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($lifecycleId, $sourceId, 'statement_authoritative_replacement', $replacementId,
                    $decisionId, 'statement correction', 'system:reconciliation', $at);
            """;
        command.Parameters.AddWithValue("$evidenceId", evidenceId);
        command.Parameters.AddWithValue("$digest", new string('f', 64));
        command.Parameters.AddWithValue("$decisionId", decisionId);
        command.Parameters.AddWithValue("$replacementId", replacementId);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$lifecycleId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
        return decisionId;
    }

    private async Task Terminate(string transactionId, string action, string? replacementId, string? decisionId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, $action, $replacementId, $decisionId, 'test', 'system:test', $at);";
        command.Parameters.AddWithValue("$eventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$replacementId", replacementId is null ? DBNull.Value : replacementId);
        command.Parameters.AddWithValue("$decisionId", decisionId is null ? DBNull.Value : decisionId);
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "pool-assignment-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static PoolAssignmentResult Assignment(ProcessResult result) => Success(result, LedgerJsonContext.Default.PoolAssignmentResult);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code);
    }
}
