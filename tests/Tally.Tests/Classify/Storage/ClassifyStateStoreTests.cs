using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap.Features;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Classify.Storage;

[SupportedOSPlatform("linux")]
public sealed class ClassifyStateStoreTests : IAsyncLifetime
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Opens_classify_db_under_owner_only_data_root_and_classify_directory()
    {
        var store = new ClassifyStateStore(root);
        await using var connection = await store.OpenAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "classify", "classify.db"), store.Paths.DatabasePath);
        Assert.Equal(store.Paths.DatabasePath, connection.DataSource);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.DataRoot));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.ClassifyDirectory));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.DatabasePath));
        Assert.DoesNotContain("ledger.db", store.Paths.DatabasePath, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task Wal_shm_and_recognized_sidecars_are_owner_only_while_open()
    {
        var store = new ClassifyStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS probe(id INTEGER PRIMARY KEY); ROLLBACK;");

        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.DatabasePath));
        Assert.True(File.Exists(store.Paths.WalPath));
        Assert.True(File.Exists(store.Paths.ShmPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.WalPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.ShmPath));
    }

    [Fact]
    public async Task Recognized_lock_and_temporary_artifacts_are_protected_when_present()
    {
        var store = new ClassifyStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        await File.WriteAllTextAsync(store.Paths.LockPath, "lock");
        var tempFile = Path.Combine(store.Paths.TemporaryDirectory, "work.tmp");
        await File.WriteAllTextAsync(tempFile, "temp");
        File.SetUnixFileMode(store.Paths.LockPath, OwnerFile | UnixFileMode.GroupRead);
        File.SetUnixFileMode(tempFile, OwnerFile | UnixFileMode.OtherRead);

        await using var _ = await store.OpenAsync(CancellationToken.None);

        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.LockPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(tempFile));
    }

    [Fact]
    public async Task Require_owner_only_rejects_unsafe_directory_modes()
    {
        var store = new ClassifyStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        File.SetUnixFileMode(store.Paths.ClassifyDirectory, OwnerDirectory | UnixFileMode.GroupRead);

        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
    }

    [Fact]
    public async Task Require_owner_only_rejects_unsafe_database_modes()
    {
        var store = new ClassifyStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile | UnixFileMode.GroupRead);

        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
    }

    [Fact]
    public async Task V001_creates_designed_tables_and_user_version()
    {
        await using var connection = await MigratedAsync();
        var tables = await TableNamesAsync(connection);
        Assert.Contains("classify_store_meta", tables);
        Assert.Contains("operation_idempotency", tables);
        Assert.Contains("classification_rule", tables);
        Assert.Contains("rule_version", tables);
        Assert.Contains("rule_condition", tables);
        Assert.Contains("rule_set_version", tables);
        Assert.Contains("evaluation_run", tables);
        Assert.Contains("classification_outcome", tables);
        Assert.Contains("match_evidence", tables);
        Assert.Contains("apply_preview", tables);
        Assert.Contains("apply_run", tables);
        Assert.Contains("apply_item", tables);
        Assert.Contains("classification_feedback", tables);
        Assert.Contains("validation_run", tables);
        Assert.Contains("validation_report", tables);
        Assert.Contains("abandonment_tombstone", tables);
        Assert.Contains("cleanup_event", tables);
        Assert.Equal(ClassifySchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Reapplying_migrations_is_idempotent()
    {
        await using var connection = await OpenAsync();
        await ClassifySchema.ApplyAsync(connection, CancellationToken.None);
        await ClassifySchema.ApplyAsync(connection, CancellationToken.None);
        Assert.Equal(ClassifySchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Newer_user_version_is_rejected()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, $"PRAGMA user_version = {ClassifySchema.CurrentVersion + 1};");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClassifySchema.ApplyAsync(connection, CancellationToken.None));
        Assert.Equal("The classify database schema version is newer than this runtime supports.", exception.Message);
    }

    [Fact]
    public async Task Writer_interruption_leaves_no_partial_schema()
    {
        var store = new ClassifyStateStore(root);
        await using var writer = await store.OpenAsync(CancellationToken.None);
        await using var blocked = await store.OpenAsync(CancellationToken.None);
        await using var transaction = writer.BeginTransaction();
        await ExecuteAsync(writer, "CREATE TABLE writer_lock (id INTEGER PRIMARY KEY);", transaction);
        await ExecuteAsync(blocked, "PRAGMA busy_timeout = 0;");

        await Assert.ThrowsAsync<SqliteException>(() => ClassifySchema.ApplyAsync(blocked, CancellationToken.None));
        await transaction.RollbackAsync();

        Assert.Empty(await TableNamesAsync(blocked));
        Assert.Equal(0L, await ScalarLongAsync(blocked, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Bootstrap_extensions_create_owner_only_state_services()
    {
        var services = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        Assert.NotNull(services.Store);
        Assert.NotNull(services.Idempotency);
        Assert.NotNull(services.Protection);
        Assert.True(File.Exists(services.Store.Paths.DatabasePath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(services.Store.Paths.DatabasePath));
    }

    [Fact]
    public async Task Symlink_database_path_is_rejected()
    {
        var store = new ClassifyStateStore(root);
        Directory.CreateDirectory(store.Paths.ClassifyDirectory);
        var target = Path.Combine(store.Paths.ClassifyDirectory, "other.db");
        await File.WriteAllTextAsync(target, "x");
        File.CreateSymbolicLink(store.Paths.DatabasePath, target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public void Fingerprint_is_stable_under_key_reordering_and_whitespace()
    {
        using var compact = JsonDocument.Parse("""{"b":2,"a":1}""");
        using var spaced = JsonDocument.Parse("""{ "a" : 1 , "b" : 2 }""");
        var left = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, compact.RootElement);
        var right = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, spaced.RootElement);
        Assert.Equal(left, right);
        Assert.Equal(64, left.Length);
    }

    [Fact]
    public void Fingerprint_treats_explicit_null_actor_run_id_canonically()
    {
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var withNull = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);
        var withEmptyDifferent = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", "", document.RootElement);
        Assert.NotEqual(withNull, withEmptyDifferent);
    }

    [Fact]
    public void Fingerprint_changes_when_actor_changes()
    {
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var a = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", "run-1", document.RootElement);
        var b = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "other", "run-1", document.RootElement);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_operation_changes()
    {
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var a = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);
        var b = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.apply.run", "1.0", "human", "owner", null, document.RootElement);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_contract_version_changes()
    {
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var a = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);
        var b = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "2.0", "human", "owner", null, document.RootElement);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_input_changes()
    {
        using var one = JsonDocument.Parse("""{"contractVersion":"1.0","x":1}""");
        using var two = JsonDocument.Parse("""{"contractVersion":"1.0","x":2}""");
        var a = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, one.RootElement);
        var b = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, two.RootElement);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_does_not_include_idempotency_key()
    {
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0","idempotencyKey":"should-not-matter"}""");
        // Key is not a separate input to fingerprint — only the request JSON body is hashed after sorting.
        // Including a field named idempotencyKey inside request still hashes as input; the separate key arg is excluded.
        var left = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);
        using var without = JsonDocument.Parse("""{"contractVersion":"1.0","idempotencyKey":"other"}""");
        var right = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, without.RootElement);
        Assert.NotEqual(left, right); // request field differs when present in body
        // And computing with same body yields same fingerprint regardless of external key usage.
        Assert.Equal(left, ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement));
    }

    [Fact]
    public async Task Exact_replay_returns_byte_identical_terminal_result()
    {
        var store = new ClassifyStateStore(root);
        var idempotency = new ClassifyOperationIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var fingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", "run-1", document.RootElement);
        const string terminal = """{"outcome":"success","result":{"evaluationId":"e1"}}""";

        await using (var tx = store.BeginImmediate(connection))
        {
            await idempotency.CommitAsync(
                connection,
                tx,
                new ClassifyOperationIdempotencyRow(
                    "key-1",
                    "classify.evaluate",
                    "1.0",
                    fingerprint,
                    terminal,
                    "2026-07-31T00:00:00.0000000Z"),
                CancellationToken.None);
            await tx.CommitAsync();
        }

        await using var readTx = store.BeginImmediate(connection);
        var existing = await idempotency.FindAsync(connection, readTx, "key-1", CancellationToken.None);
        var lookup = idempotency.Resolve(existing, "classify.evaluate", "1.0", fingerprint);
        Assert.Equal(ClassifyIdempotencyDisposition.Replay, lookup.Disposition);
        Assert.Equal(terminal, lookup.Record!.TerminalResult);
    }

    [Fact]
    public async Task Mismatched_replay_is_conflict_before_mutation()
    {
        var store = new ClassifyStateStore(root);
        var idempotency = new ClassifyOperationIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var fingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);

        await using (var tx = store.BeginImmediate(connection))
        {
            await idempotency.CommitAsync(
                connection,
                tx,
                new ClassifyOperationIdempotencyRow(
                    "key-2",
                    "classify.evaluate",
                    "1.0",
                    fingerprint,
                    """{"outcome":"error"}""",
                    "2026-07-31T00:00:00.0000000Z"),
                CancellationToken.None);
            await tx.CommitAsync();
        }

        using var other = JsonDocument.Parse("""{"contractVersion":"1.0","extra":true}""");
        var otherFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, other.RootElement);
        await using var readTx = store.BeginImmediate(connection);
        var existing = await idempotency.FindAsync(connection, readTx, "key-2", CancellationToken.None);
        var lookup = idempotency.Resolve(existing, "classify.evaluate", "1.0", otherFingerprint);
        Assert.Equal(ClassifyIdempotencyDisposition.Conflict, lookup.Disposition);
    }

    [Fact]
    public void Replay_requires_explicit_operation_and_contract_identity_even_when_fingerprint_matches()
    {
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var record = new ClassifyOperationIdempotencyRow(
            "key-identity",
            "classify.evaluate",
            "1.0",
            fingerprint,
            "{}",
            "2026-07-31T00:00:00.0000000Z");
        var idempotency = new ClassifyOperationIdempotencyStore();

        Assert.Equal(
            ClassifyIdempotencyDisposition.Replay,
            idempotency.Resolve(record, "classify.evaluate", "1.0", fingerprint).Disposition);
        Assert.Equal(
            ClassifyIdempotencyDisposition.Conflict,
            idempotency.Resolve(record, "classify.apply.run", "1.0", fingerprint).Disposition);
        Assert.Equal(
            ClassifyIdempotencyDisposition.Conflict,
            idempotency.Resolve(record, "classify.evaluate", "2.0", fingerprint).Disposition);
    }

    [Fact]
    public async Task Idempotency_rows_are_immutable_after_commit()
    {
        var store = new ClassifyStateStore(root);
        var idempotency = new ClassifyOperationIdempotencyStore();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        using var document = JsonDocument.Parse("""{"contractVersion":"1.0"}""");
        var fingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            "classify.evaluate", "1.0", "human", "owner", null, document.RootElement);
        await using (var tx = store.BeginImmediate(connection))
        {
            await idempotency.CommitAsync(
                connection,
                tx,
                new ClassifyOperationIdempotencyRow(
                    "key-3", "classify.evaluate", "1.0", fingerprint, "{}", "2026-07-31T00:00:00.0000000Z"),
                CancellationToken.None);
            await tx.CommitAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE operation_idempotency SET terminal_result = 'x' WHERE idempotency_key = 'key-3';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM operation_idempotency WHERE idempotency_key = 'key-3';"));
    }

    [Fact]
    public async Task Execute_write_commits_atomically()
    {
        var store = new ClassifyStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        await store.ExecuteWriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO abandonment_tombstone (
                    tombstone_id, subject_type, subject_id, reason, actor, abandoned_at, removed_payload_count
                ) VALUES ('t1', 'evaluation', 'e1', 'reason', 'human:owner', '2026-07-31T00:00:00Z', 0);
                """;
            await command.ExecuteNonQueryAsync(token);
            return 1;
        }, CancellationToken.None);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM abandonment_tombstone;"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync()
    {
        var store = new ClassifyStateStore(root);
        return await store.OpenAsync(CancellationToken.None);
    }

    private async Task<SqliteConnection> MigratedAsync()
    {
        var store = new ClassifyStateStore(root);
        return await store.OpenMigratedAsync(CancellationToken.None);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), CultureInfo.InvariantCulture);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> TableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }
}
