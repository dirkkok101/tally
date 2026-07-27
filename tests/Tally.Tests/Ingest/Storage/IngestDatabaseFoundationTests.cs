using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Ingest.Storage.Migrations;
using Xunit;

namespace Tally.Tests.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class IngestDatabaseFoundationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-{Guid.NewGuid():N}");

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task Opens_only_the_owner_ingest_database_under_the_data_root()
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());

        await using var connection = await database.OpenAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "ingest", "ingest.db"), database.DatabasePath);
        Assert.Equal(database.DatabasePath, connection.DataSource);
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task Connection_enables_foreign_keys_wal_full_synchronous_and_bounded_busy_handling()
    {
        await using var connection = await OpenAsync();

        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;"), System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2L, await ScalarLongAsync(connection, "PRAGMA synchronous;"));
        Assert.Equal(5000L, await ScalarLongAsync(connection, "PRAGMA busy_timeout;"));
    }

    // DM-INGEST-STATE-STORE
    [Fact]
    public async Task V002_creates_the_exact_state_store_tables_with_snake_case_identifiers()
    {
        await using var connection = await OpenAsync();
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(
        [
            "batch_error_event", "candidate_receipt", "import_candidate", "import_receipt", "ingest_batch",
            "ingest_store_metadata", "manifest_approval", "manifest_revision", "reconciliation_control",
            "source_record_outcome", "status_snapshot", "status_snapshot_item"
        ], await TableNamesAsync(connection));
        Assert.Equal(["manifest_revision_id", "batch_id", "revision_number", "canonical_digest", "committable", "created_at"], await ColumnNamesAsync(connection, "manifest_revision"));
        Assert.Equal(["error_event_id", "batch_id", "sequence", "code", "category", "safe_message", "candidate_id", "mutation_possibility", "durable_state", "retry_action", "field", "recorded_at"], await ColumnNamesAsync(connection, "batch_error_event"));
        Assert.Equal(["snapshot_id", "contract_version", "store_generation", "created_at", "expires_at", "total_count"], await ColumnNamesAsync(connection, "status_snapshot"));
        Assert.Equal(["snapshot_id", "ordinal", "batch_status_summary_json"], await ColumnNamesAsync(connection, "status_snapshot_item"));
    }

    // DM-INGEST-STATE-STORE
    [Fact]
    public async Task V003_advances_user_version()
    {
        await using var connection = await OpenAsync();
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task Reapplying_v003_is_idempotent()
    {
        await using var connection = await OpenAsync();
        var migrator = new IngestSchemaMigrator();
        await migrator.ApplyAsync(connection, CancellationToken.None);
        await migrator.ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(12, (await TableNamesAsync(connection)).Length);
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task A_newer_user_version_returns_a_stable_compatibility_failure()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "PRAGMA user_version = 4;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None));

        Assert.Equal("The ingest database schema version is newer than this runtime supports.", exception.Message);
    }

    // bd-2vft / import_receipt provenance columns
    [Fact]
    public async Task V003_adds_receipt_created_at_and_updated_at_columns()
    {
        await using var connection = await OpenAsync();
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await new IngestMigrationV001().ApplyAsync(connection, transaction, CancellationToken.None);
            await new IngestMigrationV002().ApplyAsync(connection, transaction, CancellationToken.None);
            await ExecuteAsync(connection, "PRAGMA user_version = 2;", transaction);
            await transaction.CommitAsync();
        }

        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        var columns = await ColumnNamesAsync(connection, "import_receipt");
        Assert.Contains("created_at", columns);
        Assert.Contains("updated_at", columns);
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task A_writer_interruption_leaves_no_partial_v001_schema()
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        await using var writer = await database.OpenAsync(CancellationToken.None);
        await using var blocked = await database.OpenAsync(CancellationToken.None);
        await using var transaction = writer.BeginTransaction();
        await ExecuteAsync(writer, "CREATE TABLE writer_lock (id INTEGER PRIMARY KEY);", transaction);
        await ExecuteAsync(blocked, "PRAGMA busy_timeout = 0;");

        await Assert.ThrowsAsync<SqliteException>(() => new IngestSchemaMigrator().ApplyAsync(blocked, CancellationToken.None));
        await transaction.RollbackAsync();

        Assert.Empty(await TableNamesAsync(blocked));
        Assert.Equal(0L, await ScalarLongAsync(blocked, "PRAGMA user_version;"));
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task V003_upgrades_an_existing_v001_database_without_rebuilding_v001_state()
    {
        await using var connection = await OpenAsync();
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await new IngestMigrationV001().ApplyAsync(connection, transaction, CancellationToken.None);
            await ExecuteAsync(connection, "PRAGMA user_version = 1;", transaction);
            await transaction.CommitAsync();
        }
        await InsertBatchAsync(connection);

        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM ingest_batch WHERE batch_id = 'batch-1';"));
    }

    // DM-INGEST-STATE-STORE
    [Fact]
    public async Task V002_creates_exactly_one_store_generation_row()
    {
        await using var connection = await MigratedAsync();

        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM ingest_store_metadata;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO ingest_store_metadata VALUES (2, 'another-generation');"));
    }

    // DM-INGEST-ERROR-STATUS-CONTRACTS
    [Fact]
    public async Task Error_events_round_trip_complete_metadata_and_latest_uses_highest_sequence()
    {
        await using var connection = await MigratedAsync();
        await InsertBatchAsync(connection);
        var store = new BatchErrorEventStore();
        var first = new IngestError("INGEST-001", IngestErrorCategory.Validation, "Correct the selected field.", "batch-1", null, MutationPossibility.None, "preview_blocked", IngestRetryAction.CorrectSource, "account");
        var latest = new IngestError("INGEST-002", IngestErrorCategory.Ledger, "Resume the interrupted commit.", "batch-1", "candidate-1", MutationPossibility.Possible, "commit_interrupted", IngestRetryAction.Resume, null);
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await store.AppendAsync(connection, transaction, "event-1", first, "2026-07-25T00:00:00Z", CancellationToken.None);
            await store.AppendAsync(connection, transaction, "event-2", latest, "2026-07-25T00:00:01Z", CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal(latest, await store.LatestAsync(connection, "batch-1", CancellationToken.None));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT MAX(sequence) FROM batch_error_event WHERE batch_id = 'batch-1';"));
    }

    // DM-INGEST-ERROR-STATUS-CONTRACTS
    [Fact]
    public async Task Error_append_participates_in_the_caller_owned_transaction()
    {
        await using var connection = await MigratedAsync();
        await InsertBatchAsync(connection);
        var store = new BatchErrorEventStore();
        var error = new IngestError("INGEST-001", IngestErrorCategory.Interrupted, "Resume the operation.", "batch-1", null, MutationPossibility.Possible, "commit_interrupted", IngestRetryAction.Resume, null);
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await store.AppendAsync(connection, transaction, "event-1", error, "2026-07-25T00:00:00Z", CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Null(await store.LatestAsync(connection, "batch-1", CancellationToken.None));
    }

    // DM-INGEST-STATE-STORE
    [Fact]
    public async Task Error_events_are_append_only_and_sequences_are_unique_per_batch()
    {
        await using var connection = await MigratedAsync();
        await InsertBatchAsync(connection);
        const string insert = "INSERT INTO batch_error_event VALUES ('event-1', 'batch-1', 1, 'INGEST-001', 1, 'Safe.', NULL, 0, NULL, 0, NULL, '2026-07-25T00:00:00Z');";
        await ExecuteAsync(connection, insert);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE batch_error_event SET safe_message = 'Changed.' WHERE error_event_id = 'event-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM batch_error_event WHERE error_event_id = 'event-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO batch_error_event VALUES ('event-2', 'batch-1', 1, 'INGEST-002', 1, 'Safe.', NULL, 0, NULL, 0, NULL, '2026-07-25T00:00:01Z');"));
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task Snapshot_membership_is_immutable_and_parent_expiry_cascades_items()
    {
        await using var connection = await MigratedAsync();
        var generation = Convert.ToString(await ScalarAsync(connection, "SELECT generation_id FROM ingest_store_metadata WHERE singleton_id = 1;"), System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteAsync(connection, $"INSERT INTO status_snapshot VALUES ('snapshot-1', '1', '{generation}', '2026-07-25T00:00:00Z', '2026-07-25T00:15:00Z', 1);");
        await ExecuteAsync(connection, "INSERT INTO status_snapshot_item VALUES ('snapshot-1', 0, '{}');");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE status_snapshot SET total_count = 2 WHERE snapshot_id = 'snapshot-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE status_snapshot_item SET ordinal = 1 WHERE snapshot_id = 'snapshot-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM status_snapshot_item WHERE snapshot_id = 'snapshot-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO status_snapshot_item VALUES ('snapshot-1', 1, '{}');"));
        await ExecuteAsync(connection, "DELETE FROM status_snapshot WHERE snapshot_id = 'snapshot-1';");

        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM status_snapshot_item;"));
    }

    // NFR-INGEST-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task V002_tables_contain_no_prohibited_financial_payload_columns()
    {
        await using var connection = await MigratedAsync();
        var columns = (await ColumnNamesAsync(connection, "batch_error_event"))
            .Concat(await ColumnNamesAsync(connection, "status_snapshot"))
            .Concat(await ColumnNamesAsync(connection, "status_snapshot_item"));
        string[] prohibited = ["source_path", "statement", "description", "amount", "balance", "bank_identifier", "manifest", "request", "exception", "stack"];

        Assert.DoesNotContain(columns, column => prohibited.Any(term => column.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    // TC-INGEST-STATE-STORE-CONFORMANCE
    [Fact]
    public async Task Revision_numbers_are_immutable_per_batch()
    {
        await using var connection = await MigratedAsync();
        await InsertBatchAsync(connection);
        await ExecuteAsync(connection, "INSERT INTO manifest_revision VALUES ('revision-1', 'batch-1', 1, 'digest', 1, '2026-07-25T00:00:00Z');");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE manifest_revision SET revision_number = 2 WHERE manifest_revision_id = 'revision-1';"));
    }

    // TC-INGEST-STATE-STORE-CONFORMANCE
    [Fact]
    public async Task Idempotency_keys_are_unique()
    {
        await using var connection = await MigratedAsync();
        await InsertBatchAsync(connection);
        await ExecuteAsync(connection, "INSERT INTO manifest_revision VALUES ('revision-1', 'batch-1', 1, 'digest', 1, '2026-07-25T00:00:00Z');");
        await ExecuteAsync(connection, "INSERT INTO import_candidate VALUES ('candidate-1', 'revision-1', 'record-1', '{}', '{}', 'idempotency-1', 0);");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO import_candidate VALUES ('candidate-2', 'revision-1', 'record-2', '{}', '{}', 'idempotency-1', 0);"));
    }

    // TC-INGEST-STATE-STORE-CONFORMANCE
    [Fact]
    public async Task Foreign_keys_reject_orphaned_state()
    {
        await using var connection = await MigratedAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO manifest_revision VALUES ('revision-1', 'missing', 1, 'digest', 1, '2026-07-25T00:00:00Z');"));
    }

    // TC-INGEST-STATE-STORE-CONFORMANCE
    [Fact]
    public async Task Concurrent_writer_respects_the_configured_busy_bound()
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        await using var writer = await database.OpenAsync(CancellationToken.None);
        await using var blocked = await database.OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(writer, CancellationToken.None);
        await using var transaction = writer.BeginTransaction();
        await ExecuteAsync(writer, "INSERT INTO ingest_batch VALUES ('batch-1', 'fingerprint', 'account', 'adapter', '1', '1', NULL, NULL, 0, '2026-07-25T00:00:00Z', '2026-07-25T00:00:00Z');", transaction);
        await ExecuteAsync(blocked, "PRAGMA busy_timeout = 1;");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(blocked, "INSERT INTO ingest_batch VALUES ('batch-2', 'fingerprint', 'account', 'adapter', '1', '1', NULL, NULL, 0, '2026-07-25T00:00:00Z', '2026-07-25T00:00:00Z');"));
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public void Ingest_storage_exposes_no_ledger_database_access_path()
    {
        var storageTypes = typeof(IngestDatabase).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("Tally.Infrastructure.Ingest.Storage", StringComparison.Ordinal) == true)
            .ToArray();
        var referencedTypes = storageTypes
            .SelectMany(type => type.GetMembers())
            .SelectMany(member => member switch
            {
                System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
                System.Reflection.ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
                System.Reflection.PropertyInfo property => [property.PropertyType],
                System.Reflection.FieldInfo field => [field.FieldType],
                _ => []
            });

        Assert.DoesNotContain(referencedTypes, type => type.FullName?.StartsWith("Tally.Infrastructure.Storage", StringComparison.Ordinal) == true);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(root)) { Directory.Delete(root, true); } return Task.CompletedTask; }

    private async Task<SqliteConnection> OpenAsync() => await new IngestDatabase(root, new IngestArtifactProtection()).OpenAsync(CancellationToken.None);
    private async Task<SqliteConnection> MigratedAsync() { var connection = await OpenAsync(); await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None); return connection; }
    private static Task InsertBatchAsync(SqliteConnection connection) => ExecuteAsync(connection, "INSERT INTO ingest_batch VALUES ('batch-1', 'fingerprint', 'account', 'adapter', '1', '1', NULL, NULL, 0, '2026-07-25T00:00:00Z', '2026-07-25T00:00:00Z');");
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) => Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<string[]> TableNamesAsync(SqliteConnection connection) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) { names.Add(reader.GetString(0)); } return names.ToArray(); }
    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string table) { await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({table});"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) { names.Add(reader.GetString(1)); } return names.ToArray(); }
}
