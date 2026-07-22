using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V002;
using Xunit;

namespace Tally.Tests.Infrastructure.Recovery;

[SupportedOSPlatform("linux")]
public sealed class MigrationCandidateBuilderTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-migration-candidate-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private LedgerDb source = null!;
    private LedgerConnectionFactory factory = null!;
    private MigrationCandidateBuilder builder = null!;

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_builds_a_verified_current_candidate_from_v1_without_mutating_source()
    {
        var sourceVersion = await UserVersion(source);
        var candidateId = Guid.NewGuid().ToString("N");
        await using var connection = await factory.OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var result = await builder.BuildAsync(connection, transaction, candidateId, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await transaction.RollbackAsync();

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(sourceVersion, await UserVersion(source));
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await UserVersion(new LedgerDb(source.DataRoot, candidateId)));
        Assert.True(result.Value!.CandidateVerification.IsVerified);
        Assert.Equal(1, await Count(new LedgerDb(source.DataRoot, candidateId), "account"));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_rejects_downgrade_before_creating_a_candidate()
    {
        var candidateId = Guid.NewGuid().ToString("N");
        await using var connection = await factory.OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var result = await builder.BuildAsync(connection, transaction, candidateId, 0, CancellationToken.None);
        await transaction.RollbackAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.Incompatible, result.ErrorCode);
        Assert.False(Directory.Exists(new LedgerDb(source.DataRoot, candidateId).GenerationDirectory));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_v2_to_current_is_supported_and_verified()
    {
        await using (var migration = await factory.OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None))
        {
            await new LedgerSchemaFragmentRegistry(
                [new V002StatementAuthoritySchema()],
                [V002StatementAuthoritySchema.FragmentName]).ApplyAsync(migration, CancellationToken.None);
        }
        var candidateId = Guid.NewGuid().ToString("N");

        var result = await Build(builder, candidateId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.SourceSchemaVersion);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, result.Value.TargetSchemaVersion);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_insufficient_space_fails_before_recovery_or_candidate_creation()
    {
        var candidateId = Guid.NewGuid().ToString("N");
        var constrained = new MigrationCandidateBuilder(
            source,
            factory,
            new DurableLedgerVerifier(protection),
            new ArtifactReconciler(),
            protection,
            _ => 0);

        var result = await Build(constrained, candidateId);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.InsufficientSpace, result.ErrorCode);
        Assert.False(Directory.Exists(new LedgerDb(source.DataRoot, candidateId).GenerationDirectory));
        Assert.False(Directory.Exists(Path.Combine(source.DataRoot, "recovery")));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_unsupported_source_version_fails_without_candidate()
    {
        await Execute(source, "PRAGMA user_version = 0;");
        var candidateId = Guid.NewGuid().ToString("N");

        var result = await Build(builder, candidateId);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.Incompatible, result.ErrorCode);
        Assert.False(Directory.Exists(new LedgerDb(source.DataRoot, candidateId).GenerationDirectory));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_unsafe_source_permissions_fail_closed()
    {
        File.SetUnixFileMode(source.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = await builder.InspectAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.HostProtection, result.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_tampered_existing_candidate_is_never_reused()
    {
        var candidateId = Guid.NewGuid().ToString("N");
        var first = await Build(builder, candidateId);
        Assert.True(first.IsSuccess, first.ErrorCode);
        await Execute(new LedgerDb(source.DataRoot, candidateId), "PRAGMA user_version = 1;");

        var replay = await Build(builder, candidateId);

        Assert.False(replay.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.CandidateConflict, replay.ErrorCode);
        Assert.Equal(source.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_existing_recovery_is_rejected_after_source_changes()
    {
        var candidateId = Guid.NewGuid().ToString("N");
        var first = await Build(builder, candidateId);
        Assert.True(first.IsSuccess, first.ErrorCode);
        await Execute(source, "INSERT INTO account VALUES ('second-account', 'Bank', 'cheque', 'asset', '2222', 'ZAR', '2026-07-22T00:00:00Z'); INSERT INTO catalogue_lifecycle_event VALUES ('second-event', 'account', 'second-account', 'create', NULL, 'Second', 'second', NULL, 'test', '2026-07-22T00:00:00Z', NULL);");

        var replay = await Build(builder, candidateId);

        Assert.False(replay.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.CandidateConflict, replay.ErrorCode);
        Assert.Equal(source.GenerationId, await CurrentId());
    }

    public async Task InitializeAsync()
    {
        protection.EnsureDataRoot(root);
        source = new LedgerDb(root, Guid.NewGuid().ToString("N"));
        factory = new(protection);
        await using (var connection = await factory.OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None))
        {
            await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);
        }
        await File.WriteAllTextAsync(source.ManifestPath, "legacy-v1");
        protection.ProtectArtifact(source.ManifestPath);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(root);
        await manager.ActivateAsync(source.GenerationId, "legacy-v1", CancellationToken.None);
        await Execute(source, "INSERT INTO account VALUES ('account', 'Bank', 'cheque', 'asset', '1111', 'ZAR', '2026-07-22T00:00:00Z'); INSERT INTO catalogue_lifecycle_event VALUES ('account-event', 'account', 'account', 'create', NULL, 'Primary', 'primary', NULL, 'test', '2026-07-22T00:00:00Z', NULL);");
        builder = new(source, factory, new DurableLedgerVerifier(protection), new ArtifactReconciler(), protection);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static async Task<long> UserVersion(LedgerDb database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<CommandResult<MigrationCandidateResult>> Build(
        MigrationCandidateBuilder target,
        string candidateId,
        int targetVersion = CompleteLedgerSchema.CurrentVersion)
    {
        await using var connection = await factory.OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await target.BuildAsync(connection, transaction, candidateId, targetVersion, CancellationToken.None);
        await transaction.RollbackAsync();
        return result;
    }

    private async Task<string> CurrentId() => (await File.ReadAllTextAsync(Path.Combine(source.DataRoot, "CURRENT"))).Trim();

    private static async Task<long> Count(LedgerDb target, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(LedgerDb target, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
