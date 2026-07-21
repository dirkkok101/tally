using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class LedgerStorageFoundationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-storage-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public void Data_root_is_owner_only() { protection.EnsureDataRoot(root); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root)); }
    // TC-LEDGER-LOCAL-DATA-PROTECTION
    [Fact]
    public async Task Generation_directories_and_sqlite_sidecars_are_owner_only()
    {
        var database = new LedgerDb(root, Guid.NewGuid().ToString("N"));
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(database, 1, CancellationToken.None);
        await ExecuteAsync(connection, "CREATE TABLE sidecar_probe (id INTEGER PRIMARY KEY);");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(database.GenerationDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath + "-wal"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath + "-shm"));
    }
    // TC-LEDGER-LOCAL-DATA-PROTECTION
    [Fact] public async Task Database_artifact_is_owner_only() { var db = await OpenAsync(); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(db.DatabasePath)); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Connection_enables_foreign_keys() { await using var connection = await ConnectionAsync(); Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA foreign_keys;")); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Connection_sets_a_bounded_busy_timeout() { await using var connection = await ConnectionAsync(); Assert.Equal(5, connection.DefaultTimeout); Assert.Equal(5000L, await ScalarLongAsync(connection, "PRAGMA busy_timeout;")); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Connection_enables_wal_and_full_synchronous_writes() { await using var connection = await ConnectionAsync(); Assert.Equal("wal", Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;"), System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase); Assert.Equal(2L, await ScalarLongAsync(connection, "PRAGMA synchronous;")); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Newer_user_version_is_rejected_before_schema_mutation() { var db = await OpenAsync(); await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath}")) { await connection.OpenAsync(); await ExecuteAsync(connection, "PRAGMA user_version = 2;"); } await Assert.ThrowsAsync<InvalidOperationException>(() => new LedgerConnectionFactory(protection).OpenAsync(db, 1, CancellationToken.None)); }
    // DM-LEDGER-STORE-GENERATION
    [Fact] public async Task V001_creates_only_foundation_tables() { await using var connection = await ConnectionAsync(); await Registry().ApplyAsync(connection, CancellationToken.None); var names = await TableNamesAsync(connection); Assert.Equal(["artifact_manifest", "idempotency_record", "logical_effect", "migration_metadata", "store_generation"], names); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public void Duplicate_fragment_name_is_rejected() => Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry([new V001StorageSchema(), new V001StorageSchema()], [V001StorageSchema.FragmentName]));
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public void Duplicate_fragment_name_across_versions_is_rejected() => Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry([new V001StorageSchema(), new NamedFragment(2, V001StorageSchema.FragmentName)], [V001StorageSchema.FragmentName]));
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public void Missing_required_fragment_is_rejected() => Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry([new V001StorageSchema()], ["missing"]));
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Fragment_failure_rolls_back_all_schema_changes() { await using var connection = await ConnectionAsync(); var registry = new LedgerSchemaFragmentRegistry([new FailingFragment()], ["failing"]); await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyAsync(connection, CancellationToken.None)); Assert.Empty(await TableNamesAsync(connection)); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Writer_lock_failure_exposes_no_partial_schema()
    {
        var database = new LedgerDb(root, Guid.NewGuid().ToString("N"));
        var factory = new LedgerConnectionFactory(protection);
        await using var lockConnection = await factory.OpenAsync(database, 1, CancellationToken.None);
        await using var blockedConnection = await factory.OpenAsync(database, 1, CancellationToken.None);
        blockedConnection.DefaultTimeout = 1;
        await ExecuteAsync(blockedConnection, "PRAGMA busy_timeout = 0;");
        await using var lockTransaction = lockConnection.BeginTransaction();
        await ExecuteAsync(lockConnection, "CREATE TABLE write_lock (id INTEGER PRIMARY KEY);", lockTransaction);

        await Assert.ThrowsAsync<SqliteException>(() => Registry().ApplyAsync(blockedConnection, CancellationToken.None));
        await lockTransaction.RollbackAsync();

        Assert.Empty(await TableNamesAsync(blockedConnection));
    }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Applied_migration_is_immutable_and_not_reapplied() { await using var connection = await ConnectionAsync(); await Registry().ApplyAsync(connection, CancellationToken.None); await Registry().ApplyAsync(connection, CancellationToken.None); Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM migration_metadata;")); Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;")); }
    // DD-LEDGER-CANDIDATE-ACTIVATION
    [Fact] public async Task Activation_rejects_generation_without_owner_only_manifest() { var manager = new StoreGenerationManager(protection); manager.ConfigureDataRoot(root); var generation = Guid.NewGuid().ToString("N"); var db = await CreateGenerationAsync(generation); await File.WriteAllTextAsync(db.ManifestPath, "fingerprint"); await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ActivateAsync(generation, "fingerprint", CancellationToken.None)); }
    // DD-LEDGER-CANDIDATE-ACTIVATION
    [Fact] public async Task Activation_rejects_manifest_with_wrong_fingerprint() { var manager = new StoreGenerationManager(protection); manager.ConfigureDataRoot(root); var generation = Guid.NewGuid().ToString("N"); await CreateProtectedGenerationAsync(generation, "actual"); await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ActivateAsync(generation, "expected", CancellationToken.None)); }
    // DD-LEDGER-CANDIDATE-ACTIVATION
    [Fact]
    public async Task Activation_rejects_an_unsafe_existing_pointer_without_replacing_it()
    {
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(root);
        var prior = Guid.NewGuid().ToString("N");
        var next = Guid.NewGuid().ToString("N");
        await CreateProtectedGenerationAsync(prior, "prior");
        await CreateProtectedGenerationAsync(next, "next");
        var currentPath = Path.Combine(root, "CURRENT");
        await File.WriteAllTextAsync(currentPath, prior);
        File.SetUnixFileMode(currentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ActivateAsync(next, "next", CancellationToken.None));

        Assert.Equal(prior, await File.ReadAllTextAsync(currentPath));
    }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Pointer_replacement_failure_leaves_no_temporary_or_unverified_pointer()
    {
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(root);
        var generation = Guid.NewGuid().ToString("N");
        await CreateProtectedGenerationAsync(generation, "verified");
        Directory.CreateDirectory(Path.Combine(root, "CURRENT"));

        await Assert.ThrowsAsync<IOException>(() => manager.ActivateAsync(generation, "verified", CancellationToken.None));

        Assert.True(Directory.Exists(Path.Combine(root, "CURRENT")));
        Assert.Empty(Directory.EnumerateFiles(root, ".CURRENT.*"));
    }
    // DD-LEDGER-CANDIDATE-ACTIVATION
    [Fact] public async Task Activation_atomically_replaces_current_and_retains_prior_generation() { var manager = new StoreGenerationManager(protection); manager.ConfigureDataRoot(root); var prior = Guid.NewGuid().ToString("N"); var next = Guid.NewGuid().ToString("N"); await CreateProtectedGenerationAsync(prior, "prior"); await CreateProtectedGenerationAsync(next, "next"); await manager.ActivateAsync(prior, "prior", CancellationToken.None); await manager.ActivateAsync(next, "next", CancellationToken.None); Assert.Equal(next, (await File.ReadAllTextAsync(Path.Combine(root, "CURRENT"))).Trim()); Assert.True(Directory.Exists(new LedgerDb(root, prior).GenerationDirectory)); protection.RequireOwnerOnlyArtifact(Path.Combine(root, "CURRENT")); }
    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact] public async Task Activation_rejects_non_sqlite_candidate_without_replacing_current() { var manager = new StoreGenerationManager(protection); manager.ConfigureDataRoot(root); var good = Guid.NewGuid().ToString("N"); var bad = Guid.NewGuid().ToString("N"); await CreateProtectedGenerationAsync(good, "good"); await manager.ActivateAsync(good, "good", CancellationToken.None); var invalid = new LedgerDb(root, bad); protection.EnsureDataRoot(invalid.GenerationDirectory); await File.WriteAllTextAsync(invalid.DatabasePath, "not sqlite"); await File.WriteAllTextAsync(invalid.ManifestPath, "bad"); protection.ProtectArtifact(invalid.DatabasePath); protection.ProtectArtifact(invalid.ManifestPath); await Assert.ThrowsAnyAsync<SqliteException>(() => manager.ActivateAsync(bad, "bad", CancellationToken.None)); Assert.Equal(good, (await File.ReadAllTextAsync(Path.Combine(root, "CURRENT"))).Trim()); }
    // DM-LEDGER-STORE-GENERATION
    [Fact] public void Invalid_generation_identity_is_rejected_before_path_construction() => Assert.Throws<ArgumentException>(() => new LedgerDb(root, "../outside"));
    // DD-LEDGER-CANDIDATE-ACTIVATION
    [Fact]
    public async Task Candidate_roots_are_owner_only()
    {
        var manager = new StoreGenerationManager(protection);
        var candidate = await manager.CreateCandidateAsync(root, Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(candidate.DataRoot));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(candidate.GenerationDirectory));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(root)) { Directory.Delete(root, true); } return Task.CompletedTask; }

    private async Task<LedgerDb> OpenAsync() { var db = new LedgerDb(root, Guid.NewGuid().ToString("N")); await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(db, 1, CancellationToken.None); return db; }
    private async Task<SqliteConnection> ConnectionAsync() { var db = new LedgerDb(root, Guid.NewGuid().ToString("N")); return await new LedgerConnectionFactory(protection).OpenAsync(db, 1, CancellationToken.None); }
    private LedgerSchemaFragmentRegistry Registry() => new([new V001StorageSchema()], [V001StorageSchema.FragmentName]);
    private async Task<LedgerDb> CreateGenerationAsync(string generation) { var db = new LedgerDb(root, generation); await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(db, 1, CancellationToken.None); return db; }
    private async Task CreateProtectedGenerationAsync(string generation, string fingerprint) { var db = await CreateGenerationAsync(generation); await File.WriteAllTextAsync(db.ManifestPath, fingerprint); protection.ProtectArtifact(db.ManifestPath); }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) => Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<string[]> TableNamesAsync(SqliteConnection connection) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) { names.Add(reader.GetString(0)); } return names.ToArray(); }

    private sealed class FailingFragment : ILedgerSchemaFragment
    {
        public int Version => 1;
        public string Name => "failing";
        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE should_rollback (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("Injected migration failure.");
        }
    }

    private sealed class NamedFragment(int version, string name) : ILedgerSchemaFragment
    {
        public int Version => version;
        public string Name => name;
        public Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
