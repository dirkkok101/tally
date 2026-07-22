using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.Ledger.Dimensions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Dimensions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class SpendPoolOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-spend-pool-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;

    [Fact]
    public void DM_LEDGER_ATTRIBUTION_POOL_CONTRACTS_registry_exposes_six_typed_operations()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(73, registry.Descriptors.Count);
        Assert.Equal(6, registry.Descriptors.Count(descriptor => descriptor.OperationId.StartsWith("ledger.pool.", StringComparison.Ordinal)));
        Assert.Equal(typeof(CreateSpendPoolInput), registry.Find("ledger.pool.create")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(SpendPoolDetail), registry.Find("ledger.pool.create")!.ResultTypeInfo.Type);
        Assert.Equal(typeof(SpendPoolLifecycleResult), registry.Find("ledger.pool.reactivate")!.ResultTypeInfo.Type);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_create_returns_a_stable_pool()
    {
        var pool = Pool(await Create("Personal after-tax", "create"));

        Assert.True(LedgerId.TryParse(pool.PoolId, out _, out _));
        Assert.Equal("Personal after-tax", pool.Name);
        Assert.Equal(SpendPoolStatus.Active, pool.Status);
        Assert.Equal(0, pool.CurrentAssignmentCount);
        Assert.Equal(0, pool.HistoricalAssignmentCount);
        Assert.Single(pool.LifecycleHistory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_blank_names_are_rejected(string name)
    {
        AssertError(await Create(name, "invalid"), 3, SpendPool.InvalidError);
    }

    [Theory]
    [InlineData("accountId")]
    [InlineData("categoryId")]
    [InlineData("budgetAmount")]
    public async Task DD_LEDGER_DIMENSIONAL_ATTRIBUTION_cross_dimension_and_budget_fields_are_rejected(string field)
    {
        var result = await Run("ledger.pool.create", Json($$"""{"name":"Personal","{{field}}":"forbidden"}"""), "forbidden-field");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Empty(Pools(await List()).Items);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_duplicate_active_name_is_a_stable_conflict()
    {
        await Create("Personal after-tax", "first");

        AssertError(await Create(" personal AFTER-tax ", "second"), 5, SpendPoolErrors.Duplicate);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_create_replay_is_stable_and_changed_replay_conflicts()
    {
        var first = Pool(await Create("Personal", "same"));
        var replay = Pool(await Create("Personal", "same"));

        Assert.Equal(first.PoolId, replay.PoolId);
        AssertError(await Create("Company", "same"), 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_rename_preserves_identity_and_appends_history()
    {
        var created = Pool(await Create("Personal", "create"));

        var renamed = Lifecycle(await Rename(created.PoolId, "Personal after-tax", "clearer", "rename"));

        Assert.Equal(created.PoolId, renamed.Pool.PoolId);
        Assert.Equal("Personal after-tax", renamed.Pool.Name);
        Assert.Equal(2, renamed.Pool.LifecycleHistory.Count);
        Assert.Equal(created.LifecycleHistory[0].LifecycleEventId, renamed.Pool.LifecycleHistory[1].PreviousLifecycleEventId);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_lifecycle_replays_return_original_events()
    {
        var pool = Pool(await Create("Personal", "create"));
        var rename = Lifecycle(await Rename(pool.PoolId, "Personal after-tax", "clearer", "rename"));
        var archive = Lifecycle(await Archive(pool.PoolId, "unused", "archive"));
        var active = Lifecycle(await Reactivate(pool.PoolId, "needed", "reactivate"));

        Assert.Equal(rename.LifecycleEventId, Lifecycle(await Rename(pool.PoolId, "Personal after-tax", "clearer", "rename")).LifecycleEventId);
        Assert.Equal(archive.LifecycleEventId, Lifecycle(await Archive(pool.PoolId, "unused", "archive")).LifecycleEventId);
        Assert.Equal(active.LifecycleEventId, Lifecycle(await Reactivate(pool.PoolId, "needed", "reactivate")).LifecycleEventId);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_get_history_is_explicit_and_missing_identity_is_stable()
    {
        var pool = Pool(await Create("Personal", "create"));
        await Rename(pool.PoolId, "Personal after-tax", "clearer", "rename");

        Assert.Empty(Pool(await Get(pool.PoolId, false)).LifecycleHistory);
        Assert.Equal(2, Pool(await Get(pool.PoolId, true)).LifecycleHistory.Count);
        AssertError(await Get(LedgerId.New().ToString(), false), 4, SpendPoolErrors.NotFound);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_archival_blocks_assignment_until_reactivation()
    {
        var pool = Pool(await Create("Personal", "create"));
        await Archive(pool.PoolId, "unused", "archive");
        var factory = Factory();
        var store = new SpendPoolStore(database, factory);
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();

        Assert.Equal(SpendPoolErrors.Archived, await store.ActiveAssignmentErrorAsync(connection, transaction, pool.PoolId, CancellationToken.None));
        await transaction.RollbackAsync();
        await Reactivate(pool.PoolId, "needed", "reactivate");
        await using var activeTransaction = connection.BeginTransaction();
        Assert.Null(await store.ActiveAssignmentErrorAsync(connection, activeTransaction, pool.PoolId, CancellationToken.None));
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_archived_pool_retains_referenced_assignments_and_counts()
    {
        var pool = Pool(await Create("Personal", "create"));
        var account = await CreateAccount();
        await SeedAssignedTransaction(account.AccountId, pool.PoolId);

        var archived = Lifecycle(await Archive(pool.PoolId, "unused", "archive")).Pool;

        Assert.Equal(1, archived.CurrentAssignmentCount);
        Assert.Equal(1, archived.HistoricalAssignmentCount);
        Assert.Equal("Personal", archived.Name);
        Assert.Equal(1L, await PoolAssignmentCountAsync(pool.PoolId));
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_archived_name_may_be_reused_but_conflicting_reactivation_is_rejected()
    {
        var original = Pool(await Create("Personal", "first"));
        await Archive(original.PoolId, "unused", "archive-first");
        var replacement = Pool(await Create("Personal", "second"));

        AssertError(await Reactivate(original.PoolId, "needed", "reactivate-first"), 5, SpendPoolErrors.Duplicate);
        await Archive(replacement.PoolId, "unused", "archive-second");
        Assert.Equal(SpendPoolStatus.Active, Lifecycle(await Reactivate(original.PoolId, "needed", "reactivate-original")).Pool.Status);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_archived_and_active_lifecycle_conflicts_are_stable()
    {
        var pool = Pool(await Create("Personal", "create"));
        await Archive(pool.PoolId, "unused", "archive");

        AssertError(await Rename(pool.PoolId, "Changed", "why", "rename"), 6, SpendPoolErrors.Archived);
        AssertError(await Archive(pool.PoolId, "again", "archive-again"), 6, SpendPoolErrors.AlreadyArchived);
        await Reactivate(pool.PoolId, "needed", "reactivate");
        AssertError(await Reactivate(pool.PoolId, "again", "reactivate-again"), 6, SpendPoolErrors.AlreadyActive);
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_list_filters_and_order_are_deterministic()
    {
        var archived = Pool(await Create("Archived", "archived"));
        await Create("Zulu", "zulu");
        await Create("Alpha", "alpha");
        await Archive(archived.PoolId, "unused", "archive");

        Assert.Equal(["Alpha", "Zulu"], Pools(await List(new(SpendPoolStatus.Active))).Items.Select(item => item.Name));
        Assert.Equal(["Archived"], Pools(await List(new(SpendPoolStatus.Archived))).Items.Select(item => item.Name));
        Assert.Equal(Pools(await List()).Items.Select(item => item.PoolId), Pools(await List()).Items.Select(item => item.PoolId));
    }

    [Fact]
    public async Task DD_LEDGER_DIMENSIONAL_ATTRIBUTION_pool_lifecycle_does_not_mutate_other_dimensions()
    {
        var before = await DimensionCounts();
        var pool = Pool(await Create("Personal", "create"));
        await Rename(pool.PoolId, "Personal after-tax", "clearer", "rename");
        await Archive(pool.PoolId, "unused", "archive");
        await Reactivate(pool.PoolId, "needed", "reactivate");

        Assert.Equal(before, await DimensionCounts());
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_invalid_status_and_id_fail_without_mutation()
    {
        AssertError(await Run("ledger.pool.list", Json("""{"status":99}"""), null), 3, SpendPool.InvalidError);
        AssertError(await Get("not-a-ulid", false), 3, SpendPool.InvalidError);
        Assert.Empty(Pools(await List()).Items);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private Task<ProcessResult> Create(string name, string key) => Run("ledger.pool.create", JsonSerializer.SerializeToElement(new CreateSpendPoolInput(name), LedgerJsonContext.Default.CreateSpendPoolInput), key);
    private Task<ProcessResult> Get(string poolId, bool history) => Run("ledger.pool.get", JsonSerializer.SerializeToElement(new GetSpendPoolInput(poolId, history), LedgerJsonContext.Default.GetSpendPoolInput), null);
    private Task<ProcessResult> List(ListSpendPoolsInput? input = null) => Run("ledger.pool.list", JsonSerializer.SerializeToElement(input ?? new(), LedgerJsonContext.Default.ListSpendPoolsInput), null);
    private Task<ProcessResult> Rename(string poolId, string name, string reason, string key) => Run("ledger.pool.rename", JsonSerializer.SerializeToElement(new RenameSpendPoolInput(poolId, name, reason), LedgerJsonContext.Default.RenameSpendPoolInput), key);
    private Task<ProcessResult> Archive(string poolId, string reason, string key) => Run("ledger.pool.archive", JsonSerializer.SerializeToElement(new ArchiveSpendPoolInput(poolId, reason), LedgerJsonContext.Default.ArchiveSpendPoolInput), key);
    private Task<ProcessResult> Reactivate(string poolId, string reason, string key) => Run("ledger.pool.reactivate", JsonSerializer.SerializeToElement(new ReactivateSpendPoolInput(poolId, reason), LedgerJsonContext.Default.ReactivateSpendPoolInput), key);

    private async Task<AccountDetail> CreateAccount()
    {
        var input = JsonSerializer.SerializeToElement(new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR"), LedgerJsonContext.Default.CreateAccountInput);
        return Success(await Run("ledger.account.create", input, "account"), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task SeedAssignedTransaction(string accountId, string poolId)
    {
        var transactionId = LedgerId.New().ToString();
        var initialEventId = LedgerId.New().ToString();
        var assignmentEventId = LedgerId.New().ToString();
        await using var connection = await Factory().OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transactionId, $accountId, -100, 'ZAR', '2026-07-01', NULL, 'Test', $occurredAt, 'test');
            INSERT INTO pool_assignment_event (
                pool_assignment_event_id, transaction_id, assignment_state, pool_id, action,
                previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($initialEventId, $transactionId, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initialize', 'test', $occurredAt);
            INSERT INTO pool_assignment_event (
                pool_assignment_event_id, transaction_id, assignment_state, pool_id, action,
                previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($assignmentEventId, $transactionId, 'assigned', $poolId, 'assign', $initialEventId, NULL, NULL, 'assign', 'test', $occurredAt);
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$occurredAt", "2026-07-01T00:00:00Z");
        command.Parameters.AddWithValue("$initialEventId", initialEventId);
        command.Parameters.AddWithValue("$assignmentEventId", assignmentEventId);
        command.Parameters.AddWithValue("$poolId", poolId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<long> PoolAssignmentCountAsync(string poolId)
    {
        await using var connection = await Factory().OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pool_assignment_event WHERE pool_id = $poolId;";
        command.Parameters.AddWithValue("$poolId", poolId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(long Accounts, long Categories, long Instruments, long Cardholders)> DimensionCounts()
    {
        await using var connection = await Factory().OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        var counts = new long[4];
        var tables = new[] { "account", "spend_category", "payment_instrument", "cardholder" };
        for (var index = 0; index < tables.Length; index++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tables[index]};";
            counts[index] = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return (counts[0], counts[1], counts[2], counts[3]);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "pool-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private LedgerConnectionFactory Factory() => new(new HostArtifactProtection());
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static SpendPoolDetail Pool(ProcessResult result) => Success(result, LedgerJsonContext.Default.SpendPoolDetail);
    private static SpendPoolLifecycleResult Lifecycle(ProcessResult result) => Success(result, LedgerJsonContext.Default.SpendPoolLifecycleResult);
    private static SpendPoolListResult Pools(ProcessResult result) => Success(result, LedgerJsonContext.Default.SpendPoolListResult);

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
