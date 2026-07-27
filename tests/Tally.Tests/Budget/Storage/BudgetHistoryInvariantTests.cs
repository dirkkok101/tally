using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget.Plans;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Xunit;

namespace Tally.Tests.Budget.Storage;

[SupportedOSPlatform("linux")]
public sealed class BudgetHistoryInvariantTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-history-{Guid.NewGuid():N}");

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Revision_content_provenance_and_hash_are_immutable()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET reason = 'changed' WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET payload_hash = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET actor_label = 'other' WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET revision_number = 9 WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET category_contract_version = '9.9' WHERE revision_id = 'rev-1';"));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Plan_entries_are_immutable()
    {
        await using var connection = await SeedDraftAsync(withEntries: true);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_entry SET planned_minor_units = 999 WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_plan_entry WHERE revision_id = 'rev-1';"));
    }

    // DM-BUDGET-LIFECYCLE-IDEMPOTENCY
    [Fact]
    public async Task Lifecycle_events_are_append_only()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_lifecycle_event SET reason = 'changed' WHERE event_id = 'evt-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_lifecycle_event WHERE event_id = 'evt-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO budget_lifecycle_event VALUES (
                'evt-2', 'plan-1', 'rev-1', 'DraftCreated', 'user', 'owner', NULL, 'reason',
                '2026-07-01T00:00:01Z', NULL, 'Draft', NULL, 1);
            """));
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Plan_identity_is_immutable_and_plans_cannot_be_deleted()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan SET period_start = '2026-08-01' WHERE plan_id = 'plan-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan SET currency_code = 'ZAR' , created_at_utc = '2099-01-01T00:00:00Z' WHERE plan_id = 'plan-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_plan WHERE plan_id = 'plan-1';"));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Revisions_cannot_be_deleted()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_plan_revision WHERE revision_id = 'rev-1';"));
    }

    // DD-BUDGET-PLAN-REVISION-LIFECYCLE
    [Fact]
    public async Task Status_transition_rejects_draft_to_superseded_and_active_to_draft()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedActivatedAsync(store, connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET status = 'Draft' WHERE revision_id = 'rev-1';"));
        await using (var draft = store.BeginImmediate(connection))
        {
            await store.InsertDraftRevisionAsync(
                connection, draft, Draft("rev-2", "plan-1", 2), [], DraftEvent("evt-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await draft.CommitAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan_revision SET status = 'Superseded' WHERE revision_id = 'rev-2';"));
    }

    // DD-BUDGET-PLAN-REVISION-LIFECYCLE
    [Fact]
    public async Task Draft_to_active_to_superseded_is_the_only_allowed_path()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedActivatedAsync(store, connection);
        await using (var draft = store.BeginImmediate(connection))
        {
            await store.InsertDraftRevisionAsync(
                connection, draft, Draft("rev-2", "plan-1", 2),
                [new BudgetPlanEntryRow("rev-2", "cat-1", 1)],
                DraftEvent("evt-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await draft.CommitAsync();
        }

        await using (var activate = store.BeginImmediate(connection))
        {
            await store.ActivateRevisionAsync(
                connection, activate, "plan-1", "rev-2", "2026-07-03T00:00:00Z", "replace",
                "user", "owner", null, "evt-activate-2", "evt-supersede-1", CancellationToken.None);
            await activate.CommitAsync();
        }

        var events = await store.GetLifecycleEventsAsync(connection, null, "plan-1", CancellationToken.None);
        Assert.Equal(["DraftCreated", "RevisionActivated", "DraftCreated", "RevisionSuperseded", "RevisionActivated"], events.Select(e => e.EventType).ToArray());
        Assert.Equal(BudgetRevisionStatus.Superseded, (await store.GetRevisionAsync(connection, null, "rev-1", CancellationToken.None))!.Status);
        Assert.Equal(BudgetRevisionStatus.Active, (await store.GetRevisionAsync(connection, null, "rev-2", CancellationToken.None))!.Status);
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Active_pointer_must_reference_same_plan_revision()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var seed = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, seed, Plan("plan-1"), CancellationToken.None);
            await store.InsertPlanAsync(connection, seed, Plan("plan-2", "2026-08-01", "2026-09-01"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, seed, Draft("rev-2", "plan-2", 1), [], DraftEvent("evt-2", "plan-2", "rev-2", 1), CancellationToken.None);
            await seed.CommitAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_plan SET active_revision_id = 'rev-2' WHERE plan_id = 'plan-1';"));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Negative_planned_minor_units_are_rejected()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO budget_plan_entry VALUES ('rev-1', 'cat-neg', -1);
            """));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Explicit_zero_entry_is_preserved()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection, transaction, Draft("rev-1", "plan-1", 1),
            [new BudgetPlanEntryRow("rev-1", "cat-zero", 0)],
            DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
        await transaction.CommitAsync();

        var entries = await store.GetEntriesAsync(connection, null, "rev-1", CancellationToken.None);
        Assert.Equal(0, Assert.Single(entries).PlannedMinorUnits);
    }

    // DM-BUDGET-LIFECYCLE-IDEMPOTENCY
    [Fact]
    public async Task Idempotency_records_are_immutable_once_committed()
    {
        var store = new BudgetStateStore(root);
        var idempotency = new BudgetIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var transaction = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1), [], DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await idempotency.CommitAsync(connection, transaction, new BudgetIdempotencyRow(
                Hash("k1"), "1.0", "op.draft", Hash("r1"), BudgetIdempotencyStore.CompletedState,
                "plan-1", "rev-1", null, "evt-1", Hash("result"), "2026-07-01T00:00:00Z", "2026-07-01T00:00:01Z"), CancellationToken.None);
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE budget_idempotency_record SET result_hash = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_idempotency_record;"));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Activation_failure_rolls_back_pointer_and_status()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedActivatedAsync(store, connection);
        await using (var draft = store.BeginImmediate(connection))
        {
            await store.InsertDraftRevisionAsync(
                connection, draft, Draft("rev-2", "plan-1", 2), [], DraftEvent("evt-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await draft.CommitAsync();
        }

        await using (var broken = store.BeginImmediate(connection))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateRevisionAsync(
                connection, broken, "plan-1", "rev-2", "2026-07-03T00:00:00Z", "replace",
                "user", "owner", null, "evt-activate-2", null, CancellationToken.None));
            await broken.RollbackAsync();
        }

        var plan = await store.GetPlanAsync(connection, null, "plan-1", CancellationToken.None);
        var prior = await store.GetRevisionAsync(connection, null, "rev-1", CancellationToken.None);
        var draftRevision = await store.GetRevisionAsync(connection, null, "rev-2", CancellationToken.None);
        Assert.Equal("rev-1", plan!.ActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, prior!.Status);
        Assert.Equal(BudgetRevisionStatus.Draft, draftRevision!.Status);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Replay_lookup_and_outcome_commit_share_one_begin_immediate()
    {
        var store = new BudgetStateStore(root);
        var idempotency = new BudgetIdempotencyStore();
        var wrote = await store.ExecuteWriteAsync(async (connection, transaction, ct) =>
        {
            var existing = await idempotency.FindAsync(connection, transaction, Hash("key"), ct);
            Assert.Equal(BudgetIdempotencyDisposition.Miss, idempotency.Resolve(existing, "1.0", "op.draft", Hash("req")).Disposition);

            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), ct);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1),
                [new BudgetPlanEntryRow("rev-1", "cat-1", 42)],
                DraftEvent("evt-1", "plan-1", "rev-1", 1), ct);
            await idempotency.CommitAsync(connection, transaction, new BudgetIdempotencyRow(
                Hash("key"), "1.0", "op.draft", Hash("req"), BudgetIdempotencyStore.CompletedState,
                "plan-1", "rev-1", null, "evt-1", Hash("result"), "2026-07-01T00:00:00Z", "2026-07-01T00:00:01Z"), ct);
            return true;
        }, CancellationToken.None);

        Assert.True(wrote);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        var replay = await idempotency.FindAsync(connection, transaction, Hash("key"), CancellationToken.None);
        Assert.Equal(BudgetIdempotencyDisposition.Replay, idempotency.Resolve(replay, "1.0", "op.draft", Hash("req")).Disposition);
        Assert.Equal("rev-1", replay!.ResultRevisionId);
        await transaction.RollbackAsync();
    }

    // DM-BUDGET-LIFECYCLE-IDEMPOTENCY
    [Fact]
    public async Task Lifecycle_event_sequence_is_unique_per_plan()
    {
        await using var connection = await SeedDraftAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO budget_lifecycle_event VALUES (
                'evt-dup', 'plan-1', 'rev-1', 'RevisionActivated', 'user', 'owner', NULL, 'reason',
                '2026-07-01T00:00:01Z', 'Draft', 'Active', NULL, 1);
            """));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Restrict_foreign_keys_block_cascade_style_history_erasure()
    {
        await using var connection = await SeedDraftAsync(withEntries: true);

        // No ON DELETE CASCADE paths: parent delete is blocked by immutability triggers first.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_plan_revision WHERE revision_id = 'rev-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM budget_plan WHERE plan_id = 'plan-1';"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_entry;"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private async Task<SqliteConnection> SeedDraftAsync(bool withEntries = false)
    {
        var store = new BudgetStateStore(root);
        var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
        IReadOnlyList<BudgetPlanEntryRow> entries = withEntries
            ? [new BudgetPlanEntryRow("rev-1", "cat-1", 100)]
            : [];
        await store.InsertDraftRevisionAsync(
            connection, transaction, Draft("rev-1", "plan-1", 1), entries, DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
        await transaction.CommitAsync();
        return connection;
    }

    private static async Task SeedActivatedAsync(BudgetStateStore store, SqliteConnection connection)
    {
        await using var seed = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, seed, Plan("plan-1"), CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection, seed, Draft("rev-1", "plan-1", 1),
            [new BudgetPlanEntryRow("rev-1", "cat-1", 100)],
            DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
        await store.ActivateRevisionAsync(
            connection, seed, "plan-1", "rev-1", "2026-07-02T00:00:00Z", "activate",
            "user", "owner", null, "evt-activate", null, CancellationToken.None);
        await seed.CommitAsync();
    }

    private static BudgetPlanRow Plan(string planId, string start = "2026-07-01", string end = "2026-08-01") =>
        new(planId, start, end, "ZAR", null, "2026-07-01T00:00:00Z");

    private static BudgetPlanRevisionRow Draft(string revisionId, string planId, int number) => new(
        revisionId, planId, number, BudgetRevisionStatus.Draft, "user", "owner", null, "draft reason",
        "2026-07-01T00:00:00Z", "1.0", Hash("payload-" + revisionId), null, null, null);

    private static BudgetLifecycleEventRow DraftEvent(string eventId, string planId, string revisionId, int sequence) => new(
        eventId, planId, revisionId, "DraftCreated", "user", "owner", null, "draft reason",
        "2026-07-01T00:00:00Z", null, "Draft", null, sequence);

    private static string Hash(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), CultureInfo.InvariantCulture);
}
