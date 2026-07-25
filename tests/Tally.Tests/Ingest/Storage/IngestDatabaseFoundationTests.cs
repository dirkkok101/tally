using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using Tally.Infrastructure.Ingest.Storage;
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
    public async Task V001_creates_the_exact_state_store_tables_with_snake_case_identifiers()
    {
        await using var connection = await OpenAsync();
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(
        [
            "candidate_receipt", "import_candidate", "import_receipt", "ingest_batch", "manifest_approval",
            "manifest_revision", "reconciliation_control", "source_record_outcome"
        ], await TableNamesAsync(connection));
        Assert.Equal(["manifest_revision_id", "batch_id", "revision_number", "canonical_digest", "committable", "created_at"], await ColumnNamesAsync(connection, "manifest_revision"));
    }

    // DM-INGEST-STATE-STORE
    [Fact]
    public async Task V001_advances_user_version_atomically()
    {
        await using var connection = await OpenAsync();
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task Reapplying_v001_is_idempotent()
    {
        await using var connection = await OpenAsync();
        var migrator = new IngestSchemaMigrator();
        await migrator.ApplyAsync(connection, CancellationToken.None);
        await migrator.ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(8, (await TableNamesAsync(connection)).Length);
    }

    // DD-INGEST-STATE-STORE
    [Fact]
    public async Task A_newer_user_version_returns_a_stable_compatibility_failure()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "PRAGMA user_version = 2;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None));

        Assert.Equal("The ingest database schema version is newer than this runtime supports.", exception.Message);
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
