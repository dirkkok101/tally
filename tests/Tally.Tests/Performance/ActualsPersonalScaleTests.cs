using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Ledger;
using Tally.Features.Ledger.Actuals;
using Tally.Features.Ledger.Relationships;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;
using Xunit;

namespace Tally.Tests.Performance;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}

[SupportedOSPlatform("linux")]
[Collection(PerformanceCollection.Name)]
public sealed class ActualsPersonalScaleTests : IAsyncLifetime
{
    private const int TransactionCount = 100_000;
    private const int MeasuredRuns = 30;
    private const int WarmupRuns = 3;
    private static readonly TimeSpan P95Budget = TimeSpan.FromSeconds(2);

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-actuals-scale-" + Guid.NewGuid().ToString("N"));
    private readonly LedgerConnectionFactory factory = new(new HostArtifactProtection());
    private LedgerDb database = null!;
    private OperationDescriptor query = null!;

    [Fact]
    public async Task Published_pool_category_actuals_path_meets_personal_scale_budget()
    {
        var warmup = await InvokePublishedQuery();
        AssertExactResult(warmup.Result);
        Assert.NotNull(warmup.Result.Cursor);
        var secondPage = await ContinuePublishedQuery(warmup.Result.Cursor);
        Assert.Equal(500, secondPage.Items.Count);
        Assert.Equal(500, secondPage.Items[0].Ordinal);
        Assert.Equal(999, secondPage.Items[^1].Ordinal);
        Assert.Equal(warmup.Result.Totals, secondPage.Totals);
        Assert.Equal(warmup.Result.Groups, secondPage.Groups);
        await DeleteSnapshot(warmup.Result.SnapshotId);
        for (var run = 1; run < WarmupRuns; run++)
        {
            var additionalWarmup = await InvokePublishedQuery();
            AssertExactResult(additionalWarmup.Result);
            await DeleteSnapshot(additionalWarmup.Result.SnapshotId);
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var samples = new List<TimeSpan>(MeasuredRuns);
        for (var run = 0; run < MeasuredRuns; run++)
        {
            var measured = await InvokePublishedQuery();

            AssertExactResult(measured.Result);
            samples.Add(measured.Elapsed);
            await DeleteSnapshot(measured.Result.SnapshotId);
        }

        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(MeasuredRuns * 0.95) - 1];
        Assert.True(
            p95 < P95Budget,
            $"Published 100,000-transaction pool/category actuals p95 was {p95.TotalMilliseconds:0.0} ms; budget is < {P95Budget.TotalMilliseconds:0} ms.");
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        await SeedPersonalScaleLedger();

        var actuals = new ActualsOperationModule(new(new QuerySnapshotStore(database, factory)));
        var bundle = new RelationshipActualsOperationBundle(
            new TransferOperationModule(null!, null!),
            new RefundOperationModule(null!),
            new RelationshipLifecycleOperationModule(null!, null!),
            actuals);
        query = Assert.Single(bundle.Descriptors, descriptor => descriptor.OperationId == ActualsOperationModule.OperationId);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<(ActualsQueryResult Result, TimeSpan Elapsed)> InvokePublishedQuery()
    {
        var handler = query.HandlerFactory(LedgerServices.Create(), OperationRegistry.Create());
        var input = JsonSerializer.SerializeToElement(
            new QueryActualsInput(new(GroupBy: ActualsGrouping.PoolCategory), 500),
            ActualsJsonContext.Default.QueryActualsInput);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await handler.HandleAsync(new(input, null, null), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        return (JsonSerializer.Deserialize(outcome.Value, ActualsJsonContext.Default.ActualsQueryResult)!, stopwatch.Elapsed);
    }

    private async Task<ActualsQueryResult> ContinuePublishedQuery(string cursor)
    {
        var handler = query.HandlerFactory(LedgerServices.Create(), OperationRegistry.Create());
        var input = JsonSerializer.SerializeToElement(
            new QueryActualsInput(Cursor: cursor),
            ActualsJsonContext.Default.QueryActualsInput);
        var outcome = await handler.HandleAsync(new(input, null, null), CancellationToken.None);

        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        return JsonSerializer.Deserialize(outcome.Value, ActualsJsonContext.Default.ActualsQueryResult)!;
    }

    private static void AssertExactResult(ActualsQueryResult result)
    {
        Assert.Equal(TransactionCount, result.TotalCount);
        Assert.Equal("-50000.50", result.Totals.NetAccountMovement);
        Assert.Equal("66667", result.Totals.ExternalSpend);
        Assert.Equal("66667", result.Totals.BudgetActual);
        Assert.Equal(6, result.Groups.Count);
        Assert.Equal(DecimalMinor(result.Totals.NetAccountMovement), result.Groups.Sum(group => DecimalMinor(group.Totals.NetAccountMovement)));
        Assert.Equal(DecimalMinor(result.Totals.ExternalSpend), result.Groups.Sum(group => DecimalMinor(group.Totals.ExternalSpend)));
        Assert.Equal(DecimalMinor(result.Totals.BudgetActual), result.Groups.Sum(group => DecimalMinor(group.Totals.BudgetActual)));
    }

    private async Task DeleteSnapshot(string snapshotId)
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM query_snapshot WHERE snapshot_id = $snapshotId;";
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedPersonalScaleLedger()
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        var triggers = await TriggerDefinitions(connection);
        await using var transaction = connection.BeginTransaction();
        foreach (var trigger in triggers)
        {
            await Execute(connection, transaction, $"DROP TRIGGER \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";");
        }
        await Execute(connection, transaction, """
            INSERT INTO account VALUES ('00000000000000000000000001', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '2026-07-22T00:00:00Z');
            INSERT INTO account VALUES ('00000000000000000000000002', 'Bank', 'savings', 'asset', '1002', 'ZAR', '2026-07-22T00:00:00Z');
            INSERT INTO account VALUES ('00000000000000000000000003', 'Bank', 'credit_card', 'liability', '1003', 'ZAR', '2026-07-22T00:00:00Z');
            INSERT INTO catalogue_lifecycle_event VALUES ('account-event-1', 'account', '00000000000000000000000001', 'create', NULL, 'Account 1', 'account 1', NULL, 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('account-event-2', 'account', '00000000000000000000000002', 'create', NULL, 'Account 2', 'account 2', NULL, 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('account-event-3', 'account', '00000000000000000000000003', 'create', NULL, 'Account 3', 'account 3', NULL, 'test', '2026-07-22T00:00:00Z', NULL);

            INSERT INTO spend_pool VALUES ('00000000000000000000000011', '2026-07-22T00:00:00Z');
            INSERT INTO spend_pool VALUES ('00000000000000000000000012', '2026-07-22T00:00:00Z');
            INSERT INTO catalogue_lifecycle_event VALUES ('pool-event-1', 'spend_pool', '00000000000000000000000011', 'create', NULL, 'Pool 1', 'pool 1', NULL, 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('pool-event-2', 'spend_pool', '00000000000000000000000012', 'create', NULL, 'Pool 2', 'pool 2', NULL, 'test', '2026-07-22T00:00:00Z', NULL);

            INSERT INTO spend_category VALUES ('00000000000000000000000021', '2026-07-22T00:00:00Z');
            INSERT INTO spend_category VALUES ('00000000000000000000000022', '2026-07-22T00:00:00Z');
            INSERT INTO spend_category VALUES ('00000000000000000000000023', '2026-07-22T00:00:00Z');
            INSERT INTO category_parent_event VALUES ('category-parent-1', '00000000000000000000000021', NULL, 'initialize', 'initial', 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO category_parent_event VALUES ('category-parent-2', '00000000000000000000000022', NULL, 'initialize', 'initial', 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO category_parent_event VALUES ('category-parent-3', '00000000000000000000000023', NULL, 'initialize', 'initial', 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('category-event-1', 'category', '00000000000000000000000021', 'create', NULL, 'Category 1', 'category 1', NULL, 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('category-event-2', 'category', '00000000000000000000000022', 'create', NULL, 'Category 2', 'category 2', NULL, 'test', '2026-07-22T00:00:00Z', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('category-event-3', 'category', '00000000000000000000000023', 'create', NULL, 'Category 3', 'category 3', NULL, 'test', '2026-07-22T00:00:00Z', NULL);

            CREATE TEMP TABLE personal_scale_number (n INTEGER PRIMARY KEY);
            WITH digits(d) AS (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))
            INSERT INTO personal_scale_number(n)
            SELECT 1 + a.d + (10 * b.d) + (100 * c.d) + (1000 * d.d) + (10000 * e.d)
            FROM digits AS a
            CROSS JOIN digits AS b
            CROSS JOIN digits AS c
            CROSS JOIN digits AS d
            CROSS JOIN digits AS e;

            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            SELECT printf('%026d', n),
                   CASE n % 3
                       WHEN 0 THEN '00000000000000000000000001'
                       WHEN 1 THEN '00000000000000000000000002'
                       ELSE '00000000000000000000000003' END,
                   CASE WHEN n % 3 = 0 THEN 50 ELSE -100 END,
                   'ZAR',
                   printf('2026-%02d-%02d', 1 + (n % 6), 1 + (n % 27)),
                   NULL,
                   'Personal scale transaction',
                   '2026-07-22T00:00:00Z',
                   'test'
            FROM personal_scale_number;

            INSERT INTO transaction_attribution_event
            SELECT printf('1%025d', n), printf('%026d', n), 'unknown', NULL, 'unknown', NULL,
                   'initialize', NULL, NULL, NULL, 'initial', 'test', '2026-07-22T00:00:00Z'
            FROM personal_scale_number;

            INSERT INTO pool_assignment_event
            SELECT printf('2%025d', n), printf('%026d', n), 'unassigned', NULL,
                   'initialize', NULL, NULL, NULL, 'initial', 'test', '2026-07-22T00:00:00Z'
            FROM personal_scale_number;
            INSERT INTO pool_assignment_event
            SELECT printf('3%025d', n), printf('%026d', n), 'assigned',
                   CASE n % 2 WHEN 0 THEN '00000000000000000000000011' ELSE '00000000000000000000000012' END,
                   'assign', printf('2%025d', n), NULL, NULL, 'owner assignment', 'test', '2026-07-22T00:00:00Z'
            FROM personal_scale_number;

            INSERT INTO category_allocation_event
            SELECT printf('4%025d', n), printf('%026d', n),
                   CASE n % 3
                       WHEN 0 THEN '00000000000000000000000021'
                       WHEN 1 THEN '00000000000000000000000022'
                       ELSE '00000000000000000000000023' END,
                   'assign', NULL, NULL, NULL, 'owner assignment', 'test', '2026-07-22T00:00:00Z'
            FROM personal_scale_number;

            """);
        foreach (var trigger in triggers) await Execute(connection, transaction, trigger.Sql);
        await transaction.CommitAsync();
    }

    private static async Task<IReadOnlyList<(string Name, string Sql)>> TriggerDefinitions(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var triggers = new List<(string Name, string Sql)>();
        while (await reader.ReadAsync()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        return triggers;
    }

    private static async Task Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static long DecimalMinor(string value) =>
        checked((long)(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * 100m));
}
