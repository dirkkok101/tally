using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap.Features;
using Tally.Contracts.Budget.Plans;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Budget.Storage;

[SupportedOSPlatform("linux")]
public sealed class BudgetStateStoreTests : IAsyncLifetime
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-{Guid.NewGuid():N}");

    // DD-BUDGET-STATE-STORE
    [Fact]
    public async Task Opens_budget_db_under_owner_only_data_root_and_budget_directory()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "budget", "budget.db"), store.Paths.DatabasePath);
        Assert.Equal(store.Paths.DatabasePath, connection.DataSource);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.DataRoot));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.BudgetDirectory));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.DatabasePath));
    }

    // DD-BUDGET-STATE-STORE
    [Fact]
    public async Task Connection_enables_foreign_keys_wal_full_synchronous_and_bounded_busy_handling()
    {
        await using var connection = await OpenAsync();

        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2L, await ScalarLongAsync(connection, "PRAGMA synchronous;"));
        Assert.Equal(5000L, await ScalarLongAsync(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(5, connection.DefaultTimeout);
    }

    // DD-BUDGET-STATE-STORE / TC-BUDGET-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task Wal_shm_and_recognized_sidecars_are_owner_only_while_open()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS probe(id INTEGER PRIMARY KEY); ROLLBACK;");

        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.DatabasePath));
        Assert.True(File.Exists(store.Paths.WalPath));
        Assert.True(File.Exists(store.Paths.ShmPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.WalPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.ShmPath));
    }

    // TC-BUDGET-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task Recognized_lock_and_temporary_artifacts_are_protected_when_present()
    {
        var store = new BudgetStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        await File.WriteAllTextAsync(store.Paths.LockPath, "lock");
        await File.WriteAllTextAsync(store.Paths.AtomicPath, "atomic");
        File.SetUnixFileMode(store.Paths.LockPath, OwnerFile | UnixFileMode.GroupRead);
        File.SetUnixFileMode(store.Paths.AtomicPath, OwnerFile | UnixFileMode.OtherRead);

        await using var _ = await store.OpenAsync(CancellationToken.None);

        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.LockPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.AtomicPath));
    }

    // TC-BUDGET-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task Require_owner_only_rejects_unsafe_directory_modes()
    {
        var store = new BudgetStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        File.SetUnixFileMode(store.Paths.BudgetDirectory, OwnerDirectory | UnixFileMode.GroupRead);

        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
    }

    // TC-BUDGET-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task Require_owner_only_rejects_unsafe_database_modes()
    {
        var store = new BudgetStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile | UnixFileMode.GroupRead);

        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
    }

    // DM-BUDGET-STATE-STORE
    [Fact]
    public async Task V001_creates_exactly_the_five_designed_tables()
    {
        await using var connection = await MigratedAsync();

        Assert.Equal(
        [
            "budget_idempotency_record",
            "budget_lifecycle_event",
            "budget_plan",
            "budget_plan_entry",
            "budget_plan_revision"
        ], await TableNamesAsync(connection));
        Assert.Equal(BudgetSchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    // DM-BUDGET-STATE-STORE
    [Fact]
    public async Task V001_creates_expected_columns_for_each_table()
    {
        await using var connection = await MigratedAsync();

        Assert.Equal(
            ["plan_id", "period_start", "period_end_exclusive", "currency_code", "active_revision_id", "created_at_utc"],
            await ColumnNamesAsync(connection, "budget_plan"));
        Assert.Equal(
            [
                "revision_id", "plan_id", "revision_number", "status", "actor_kind", "actor_label", "actor_run_id",
                "reason", "created_at_utc", "category_contract_version", "payload_hash",
                "activated_at_utc", "superseded_at_utc", "superseded_by_revision_id"
            ],
            await ColumnNamesAsync(connection, "budget_plan_revision"));
        Assert.Equal(
            ["revision_id", "category_id", "planned_minor_units"],
            await ColumnNamesAsync(connection, "budget_plan_entry"));
        Assert.Equal(
            [
                "event_id", "plan_id", "revision_id", "event_type", "actor_kind", "actor_label", "actor_run_id",
                "reason", "occurred_at_utc", "prior_status", "resulting_status", "replacement_revision_id", "event_sequence"
            ],
            await ColumnNamesAsync(connection, "budget_lifecycle_event"));
        Assert.Equal(
            [
                "key_digest", "contract_version", "operation_id", "request_hash", "state",
                "plan_id", "result_revision_id", "prior_active_revision_id",
                "lifecycle_event_ids", "result_hash", "created_at_utc", "completed_at_utc"
            ],
            await ColumnNamesAsync(connection, "budget_idempotency_record"));
    }

    // DD-BUDGET-STATE-STORE
    [Fact]
    public async Task Reapplying_migrations_is_idempotent()
    {
        await using var connection = await OpenAsync();
        await BudgetSchema.ApplyAsync(connection, CancellationToken.None);
        await BudgetSchema.ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(BudgetSchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(5, (await TableNamesAsync(connection)).Length);
    }

    // DD-BUDGET-STATE-STORE
    [Fact]
    public async Task Newer_user_version_is_rejected()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, $"PRAGMA user_version = {BudgetSchema.CurrentVersion + 1};");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BudgetSchema.ApplyAsync(connection, CancellationToken.None));

        Assert.Equal("The budget database schema version is newer than this runtime supports.", exception.Message);
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Writer_interruption_leaves_no_partial_schema()
    {
        var store = new BudgetStateStore(root);
        await using var writer = await store.OpenAsync(CancellationToken.None);
        await using var blocked = await store.OpenAsync(CancellationToken.None);
        await using var transaction = writer.BeginTransaction();
        await ExecuteAsync(writer, "CREATE TABLE writer_lock (id INTEGER PRIMARY KEY);", transaction);
        await ExecuteAsync(blocked, "PRAGMA busy_timeout = 0;");

        await Assert.ThrowsAsync<SqliteException>(() => BudgetSchema.ApplyAsync(blocked, CancellationToken.None));
        await transaction.RollbackAsync();

        Assert.Empty(await TableNamesAsync(blocked));
        Assert.Equal(0L, await ScalarLongAsync(blocked, "PRAGMA user_version;"));
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Unique_zar_month_plan_identity_is_enforced()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1", "2026-07-01", "2026-08-01"), CancellationToken.None);
        await transaction.CommitAsync();

        await using var second = store.BeginImmediate(connection);
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.InsertPlanAsync(connection, second, Plan("plan-2", "2026-07-01", "2026-08-01"), CancellationToken.None));
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Non_zar_currency_is_rejected_by_schema()
    {
        await using var connection = await MigratedAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO budget_plan VALUES ('plan-1', '2026-07-01', '2026-08-01', 'USD', NULL, '2026-07-01T00:00:00Z');
            """));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Insert_draft_persists_revision_entries_and_lifecycle_event()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection,
            transaction,
            Draft("rev-1", "plan-1", 1),
            [new BudgetPlanEntryRow("rev-1", "cat-1", 100), new BudgetPlanEntryRow("rev-1", "cat-2", 0)],
            DraftEvent("evt-1", "plan-1", "rev-1", 1),
            CancellationToken.None);
        await transaction.CommitAsync();

        var revision = await store.GetRevisionAsync(connection, null, "rev-1", CancellationToken.None);
        var entries = await store.GetEntriesAsync(connection, null, "rev-1", CancellationToken.None);
        var events = await store.GetLifecycleEventsAsync(connection, null, "plan-1", CancellationToken.None);

        Assert.NotNull(revision);
        Assert.Equal(BudgetRevisionStatus.Draft, revision.Status);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries.Single(e => e.CategoryId == "cat-2").PlannedMinorUnits);
        Assert.Equal("DraftCreated", events.Single().EventType);
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Unique_revision_number_per_plan_is_enforced()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var transaction = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1), [], DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var second = store.BeginImmediate(connection);
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.InsertDraftRevisionAsync(
                connection, second, Draft("rev-2", "plan-1", 1), [], DraftEvent("evt-2", "plan-1", "rev-2", 2), CancellationToken.None));
    }

    // DD-BUDGET-PLAN-REVISION-LIFECYCLE
    [Fact]
    public async Task Activation_transitions_draft_to_active_and_sets_pointer()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var seed = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, seed, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, seed, Draft("rev-1", "plan-1", 1),
                [new BudgetPlanEntryRow("rev-1", "cat-1", 250)],
                DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await seed.CommitAsync();
        }

        await using (var activate = store.BeginImmediate(connection))
        {
            await store.ActivateRevisionAsync(
                connection, activate, "plan-1", "rev-1", "2026-07-02T00:00:00Z", "activate",
                "user", "owner", null, "evt-activate", null, CancellationToken.None);
            await activate.CommitAsync();
        }

        var plan = await store.GetPlanAsync(connection, null, "plan-1", CancellationToken.None);
        var revision = await store.GetRevisionAsync(connection, null, "rev-1", CancellationToken.None);
        Assert.Equal("rev-1", plan!.ActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, revision!.Status);
        Assert.Equal("2026-07-02T00:00:00Z", revision.ActivatedAtUtc);
    }

    // DD-BUDGET-PLAN-REVISION-LIFECYCLE
    [Fact]
    public async Task Activation_supersedes_prior_active_and_keeps_one_active()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedActivatedAsync(store, connection, "plan-1", "rev-1");

        await using (var draft = store.BeginImmediate(connection))
        {
            await store.InsertDraftRevisionAsync(
                connection, draft, Draft("rev-2", "plan-1", 2),
                [new BudgetPlanEntryRow("rev-2", "cat-1", 500)],
                DraftEvent("evt-draft-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await draft.CommitAsync();
        }

        await using (var activate = store.BeginImmediate(connection))
        {
            await store.ActivateRevisionAsync(
                connection, activate, "plan-1", "rev-2", "2026-07-03T00:00:00Z", "replace",
                "user", "owner", "run-1", "evt-activate-2", "evt-supersede-1", CancellationToken.None);
            await activate.CommitAsync();
        }

        var plan = await store.GetPlanAsync(connection, null, "plan-1", CancellationToken.None);
        var prior = await store.GetRevisionAsync(connection, null, "rev-1", CancellationToken.None);
        var next = await store.GetRevisionAsync(connection, null, "rev-2", CancellationToken.None);
        Assert.Equal("rev-2", plan!.ActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Superseded, prior!.Status);
        Assert.Equal("rev-2", prior.SupersededByRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, next!.Status);
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active' AND plan_id = 'plan-1';"));
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Partial_unique_index_rejects_two_active_revisions_for_one_plan()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedActivatedAsync(store, connection, "plan-1", "rev-1");
        await using (var draft = store.BeginImmediate(connection))
        {
            await store.InsertDraftRevisionAsync(
                connection, draft, Draft("rev-2", "plan-1", 2), [], DraftEvent("evt-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await draft.CommitAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            UPDATE budget_plan_revision SET status = 'Active', activated_at_utc = '2026-07-03T00:00:00Z' WHERE revision_id = 'rev-2';
            """));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Execute_write_rolls_back_all_changes_on_failure()
    {
        var store = new BudgetStateStore(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteWriteAsync<object?>(async (connection, transaction, ct) =>
        {
            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), ct);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1),
                [new BudgetPlanEntryRow("rev-1", "cat-1", 10)],
                DraftEvent("evt-1", "plan-1", "rev-1", 1), ct);
            throw new InvalidOperationException("Injected failure.");
        }, CancellationToken.None));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_lifecycle_event;"));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Begin_immediate_is_exclusive_against_concurrent_writer()
    {
        var store = new BudgetStateStore(root);
        await using var writer = await store.OpenMigratedAsync(CancellationToken.None);
        await using var blocked = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(writer);
        await ExecuteAsync(blocked, "PRAGMA busy_timeout = 1;");

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(blocked, "BEGIN IMMEDIATE; INSERT INTO budget_plan VALUES ('plan-x', '2026-01-01', '2026-02-01', 'ZAR', NULL, '2026-01-01T00:00:00Z');"));
        await transaction.RollbackAsync();
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Idempotency_commit_and_replay_return_stable_outcome_references()
    {
        var store = new BudgetStateStore(root);
        var idempotency = new BudgetIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var transaction = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1),
                [new BudgetPlanEntryRow("rev-1", "cat-1", 10)],
                DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await idempotency.CommitAsync(connection, transaction, Idempotency("digest-1", "op.draft", "plan-1", "rev-1", "evt-1"), CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var lookup = store.BeginImmediate(connection);
        var existing = await idempotency.FindAsync(connection, lookup, Hash("digest-1"), CancellationToken.None);
        var disposition = idempotency.Resolve(existing, "1.0", "op.draft", Hash("request-1"));
        await lookup.RollbackAsync();

        Assert.Equal(BudgetIdempotencyDisposition.Replay, disposition.Disposition);
        Assert.Equal("rev-1", disposition.Record!.ResultRevisionId);
        Assert.Equal(["evt-1"], BudgetRowMapper.ParseLifecycleEventIds(disposition.Record.LifecycleEventIds));
        Assert.DoesNotContain("planned", disposition.Record.LifecycleEventIds, StringComparison.OrdinalIgnoreCase);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Idempotency_key_reuse_with_different_request_is_conflict()
    {
        var store = new BudgetStateStore(root);
        var idempotency = new BudgetIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var transaction = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, transaction, Draft("rev-1", "plan-1", 1), [], DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await idempotency.CommitAsync(connection, transaction, Idempotency("digest-1", "op.draft", "plan-1", "rev-1", "evt-1"), CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var lookup = store.BeginImmediate(connection);
        var existing = await idempotency.FindAsync(connection, lookup, Hash("digest-1"), CancellationToken.None);
        var disposition = idempotency.Resolve(existing, "1.0", "op.draft", Hash("different-request"));
        await lookup.RollbackAsync();

        Assert.Equal(BudgetIdempotencyDisposition.Conflict, disposition.Disposition);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Idempotency_records_do_not_store_serialized_financial_bodies()
    {
        await using var connection = await MigratedAsync();
        var columns = string.Join(',', await ColumnNamesAsync(connection, "budget_idempotency_record"));
        string[] prohibited = ["response", "request_body", "payload_json", "stable_result", "amount", "entries_json", "position", "actual"];

        Assert.DoesNotContain(prohibited, term => columns.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // DM-BUDGET-STATE-STORE
    [Fact]
    public async Task Schema_never_persists_prohibited_projection_tables()
    {
        await using var connection = await MigratedAsync();
        var tables = await TableNamesAsync(connection);
        string[] prohibited = ["actual", "snapshot", "cursor", "position", "report", "consumer", "category_catalogue", "ledger"];

        Assert.DoesNotContain(tables, table => prohibited.Any(term => table.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    // DD-BUDGET-STATE-STORE
    [Fact]
    public async Task Bootstrap_extension_initializes_store_under_data_root()
    {
        var services = await BudgetStateExtensions.CreateStateAsync(root, CancellationToken.None);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "budget", "budget.db"), services.Store.Paths.DatabasePath);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(services.Store.Paths.BudgetDirectory));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(services.Store.Paths.DatabasePath));
        Assert.NotNull(services.Idempotency);
        Assert.NotNull(services.Protection);
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Plan_row_mapper_round_trips_nullable_active_pointer()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
        await transaction.CommitAsync();

        var plan = await store.GetPlanByPeriodAsync(connection, null, "ZAR", "2026-07-01", CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Null(plan.ActiveRevisionId);
        Assert.Equal("2026-08-01", plan.PeriodEndExclusive);
    }

    // DM-BUDGET-REVISION-ENTRY
    [Fact]
    public async Task Next_revision_number_and_event_sequence_allocate_monotonically()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, transaction, Plan("plan-1"), CancellationToken.None);
        Assert.Equal(1, await store.NextRevisionNumberAsync(connection, transaction, "plan-1", CancellationToken.None));
        Assert.Equal(1, await store.NextEventSequenceAsync(connection, transaction, "plan-1", CancellationToken.None));
        await store.InsertDraftRevisionAsync(
            connection, transaction, Draft("rev-1", "plan-1", 1), [], DraftEvent("evt-1", "plan-1", "rev-1", 1), CancellationToken.None);
        Assert.Equal(2, await store.NextRevisionNumberAsync(connection, transaction, "plan-1", CancellationToken.None));
        Assert.Equal(2, await store.NextEventSequenceAsync(connection, transaction, "plan-1", CancellationToken.None));
        await transaction.CommitAsync();
    }

    // DM-BUDGET-PERIOD-PLAN
    [Fact]
    public async Task Foreign_keys_reject_orphaned_revision()
    {
        await using var connection = await MigratedAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO budget_plan_revision VALUES (
                'rev-1', 'missing', 1, 'Draft', 'user', 'owner', NULL, 'reason',
                '2026-07-01T00:00:00Z', '1.0', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                NULL, NULL, NULL);
            """));
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

    private async Task<SqliteConnection> OpenAsync() => await new BudgetStateStore(root).OpenAsync(CancellationToken.None);

    private async Task<SqliteConnection> MigratedAsync() => await new BudgetStateStore(root).OpenMigratedAsync(CancellationToken.None);

    private static async Task SeedActivatedAsync(BudgetStateStore store, SqliteConnection connection, string planId, string revisionId)
    {
        await using var seed = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, seed, Plan(planId), CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection, seed, Draft(revisionId, planId, 1),
            [new BudgetPlanEntryRow(revisionId, "cat-1", 100)],
            DraftEvent("evt-draft-" + revisionId, planId, revisionId, 1), CancellationToken.None);
        await store.ActivateRevisionAsync(
            connection, seed, planId, revisionId, "2026-07-02T00:00:00Z", "activate",
            "user", "owner", null, "evt-activate-" + revisionId, null, CancellationToken.None);
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

    private static BudgetIdempotencyRow Idempotency(
        string digestSeed, string operationId, string planId, string revisionId, string eventId) => new(
        Hash(digestSeed), "1.0", operationId, Hash("request-1"), BudgetIdempotencyStore.CompletedState,
        planId, revisionId, null, BudgetRowMapper.FormatLifecycleEventIds([eventId]), Hash("result-" + revisionId),
        "2026-07-01T00:00:00Z", "2026-07-01T00:00:01Z");

    private static string Hash(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
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

    private static async Task<string[]> TableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names.ToArray();
    }
}
