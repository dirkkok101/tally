using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Tally.Features.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Features.Ledger.Recovery;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-SAFE-RESTORE-CONTRACT.
public sealed class RestoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-restore-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private readonly SafeActor actor = new("human", "restore-test");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private BackupService backupService = null!;
    private RestoreService restoreService = null!;
    private RestoreOperationModule module = null!;
    private string backupRoot = null!;

    [Fact]
    public void DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_exposes_prepare_descriptor()
    {
        var descriptor = Assert.Single(module.Descriptors, item => item.OperationId == RestoreOperationModule.PrepareOperationId);

        Assert.Equal("mutation", descriptor.Kind);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(PrepareRestoreInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(RestorePrepareResult), descriptor.ResultTypeInfo.Type);
        Assert.Equal("RestoreOperationModule.Prepare", descriptor.HandlerTarget);
    }

    [Fact]
    public void DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_exposes_activate_descriptor()
    {
        var descriptor = Assert.Single(module.Descriptors, item => item.OperationId == RestoreOperationModule.ActivateOperationId);

        Assert.Equal("mutation", descriptor.Kind);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(ActivateRestoreInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(RestoreActivationResult), descriptor.ResultTypeInfo.Type);
        Assert.Equal("RestoreOperationModule.Activate", descriptor.HandlerTarget);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_prepare_creates_a_distinct_private_candidate_without_changing_current()
    {
        var backup = await Backup();
        var before = await CurrentId();

        var prepared = await Prepare(backup);

        Assert.NotEqual(before, prepared.CandidateId);
        Assert.Equal(before, await CurrentId());
        var candidate = new LedgerDb(database.DataRoot, prepared.CandidateId);
        Assert.True(File.Exists(candidate.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(candidate.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(candidate.ManifestPath));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_candidate_and_source_complete_manifests_are_equal()
    {
        var backup = await Backup();

        var prepared = await Prepare(backup);

        Assert.Equal(backup.Manifest.NormalizedFingerprint, prepared.SourceNormalizedFingerprint);
        Assert.Equal(backup.Manifest.NormalizedFingerprint, prepared.CandidateNormalizedFingerprint);
        Assert.Equal(backup.Manifest.Types.Count, prepared.Types.Count);
        Assert.Equal(backup.Manifest.Actuals.Count, prepared.Actuals.Count);
        Assert.Equal(backup.Manifest.CategoryHierarchyFingerprint, prepared.CategoryHierarchyFingerprint);
        Assert.Equal(backup.Manifest.TransactionReplacementFingerprint, prepared.TransactionReplacementFingerprint);
        Assert.Equal(backup.Manifest.RelationshipFingerprint, prepared.RelationshipFingerprint);
        Assert.Equal(backup.Manifest.ReconciliationFingerprint, prepared.ReconciliationFingerprint);
        Assert.Equal(backup.Manifest.IdempotencyFingerprint, prepared.IdempotencyFingerprint);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_prepare_replay_is_stable_and_changed_input_conflicts()
    {
        var backup = await Backup();
        var input = new PrepareRestoreInput(BackupPath(), backup.ArtifactChecksum);
        var first = await Prepare(input, "prepare-replay");

        var replay = await Prepare(input, "prepare-replay");
        var changed = await restoreService.PrepareAsync(input with { ExpectedArtifactChecksum = new string('0', 64) }, actor, "prepare-replay", CancellationToken.None);

        Assert.Equal(Serialize(first), Serialize(replay));
        Assert.False(changed.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, changed.ErrorCode);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(database.DataRoot, "generations")), path => Path.GetFileName(path) == first.CandidateId);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_corrupt_backup_fails_without_candidate_or_current_change()
    {
        var backup = await Backup();
        var path = BackupPath();
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0xff);
            stream.Flush(true);
        }
        var before = await CurrentId();
        var generations = Directory.EnumerateDirectories(Path.Combine(database.DataRoot, "generations")).Count();

        var result = await restoreService.PrepareAsync(new(path, backup.ArtifactChecksum), actor, "corrupt", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.Integrity, result.ErrorCode);
        Assert.Equal(before, await CurrentId());
        Assert.Equal(generations, Directory.EnumerateDirectories(Path.Combine(database.DataRoot, "generations")).Count());
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_unsafe_backup_fails_without_candidate()
    {
        var backup = await Backup();
        File.SetUnixFileMode(BackupPath(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = await restoreService.PrepareAsync(new(BackupPath(), backup.ArtifactChecksum), actor, "unsafe", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.HostProtection, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_tampered_prepared_candidate_is_rejected_on_prepare_replay()
    {
        var backup = await Backup();
        var prepared = await Prepare(backup, "tamper-prepare");
        var candidate = new LedgerDb(database.DataRoot, prepared.CandidateId);
        await using (var stream = new FileStream(candidate.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0xff);
            stream.Flush(true);
        }

        var result = await restoreService.PrepareAsync(new(BackupPath(), backup.ArtifactChecksum), actor, "tamper-prepare", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.Integrity, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_activation_requires_explicit_authorization()
    {
        var prepared = await Prepare(await Backup());
        var currentFingerprint = await CurrentFingerprint();

        var result = await restoreService.ActivateAsync(new(prepared.CandidateId, currentFingerprint, prepared.CandidateNormalizedFingerprint, false), actor, "unauthorized", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.NotAuthorized, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_stale_current_fingerprint_blocks_activation()
    {
        var prepared = await Prepare(await Backup());

        var result = await restoreService.ActivateAsync(new(prepared.CandidateId, new string('0', 64), prepared.CandidateNormalizedFingerprint, true), actor, "stale-current", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.StaleCurrent, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_stale_candidate_fingerprint_blocks_activation()
    {
        var prepared = await Prepare(await Backup());

        var result = await restoreService.ActivateAsync(new(prepared.CandidateId, await CurrentFingerprint(), new string('0', 64), true), actor, "stale-candidate", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.StaleCandidate, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_activation_revalidates_candidate_immediately_before_pointer_change()
    {
        var prepared = await Prepare(await Backup());
        var candidate = new LedgerDb(database.DataRoot, prepared.CandidateId);
        await Execute(candidate, "PRAGMA user_version = 1;");

        var result = await restoreService.ActivateAsync(new(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true), actor, "invalid-candidate", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.Incompatible, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_activation_is_atomic_and_retains_the_prior_generation()
    {
        var prepared = await Prepare(await Backup());
        var prior = database.GenerationId;

        var activated = await Activate(prepared, "activate");

        Assert.Equal(prepared.CandidateId, activated.CurrentGenerationId);
        Assert.Equal(prepared.CandidateNormalizedFingerprint, activated.NormalizedFingerprint);
        Assert.Equal(prepared.CandidateId, await CurrentId());
        Assert.True(Directory.Exists(new LedgerDb(database.DataRoot, prior).GenerationDirectory));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_activation_replay_is_stable_and_changed_replay_conflicts()
    {
        var prepared = await Prepare(await Backup());
        var input = new ActivateRestoreInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true);
        var first = await Activate(input, "activate-replay");

        var replay = await Activate(input, "activate-replay");
        var changed = await restoreService.ActivateAsync(input with { ExpectedCandidateFingerprint = new string('0', 64) }, actor, "activate-replay", CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.False(changed.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, changed.ErrorCode);
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_restore_failures_do_not_echo_private_values()
    {
        var result = await restoreService.PrepareAsync(new(Path.Combine(backupRoot, "PRIVATE-PATH-CANARY"), new string('0', 64)), actor, "private", CancellationToken.None);
        var diagnostic = result.ToString();

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("PRIVATE-PATH-CANARY", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(backupRoot, diagnostic, StringComparison.Ordinal);
    }

    public async Task InitializeAsync()
    {
        protection.EnsureDataRoot(root);
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "live"), CancellationToken.None);
        backupRoot = Path.Combine(root, "backups");
        protection.EnsureDataRoot(backupRoot);
        factory = new(protection);
        backupService = BackupFor(database);
        restoreService = RestoreFor(database, backupService);
        module = new(restoreService);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private string BackupPath() => Path.Combine(backupRoot, "ledger.tally-backup");

    private async Task<BackupReceipt> Backup()
    {
        var result = await backupService.CreateAsync(new(BackupPath()), actor, "backup", CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)!;
    }

    private Task<RestorePrepareResult> Prepare(BackupReceipt backup, string key = "prepare") =>
        Prepare(new PrepareRestoreInput(BackupPath(), backup.ArtifactChecksum), key);

    private async Task<RestorePrepareResult> Prepare(PrepareRestoreInput input, string key)
    {
        var result = await restoreService.PrepareAsync(input, actor, key, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(RestoreJsonContext.Default.RestorePrepareResult)!;
    }

    private async Task<RestoreActivationResult> Activate(RestorePrepareResult prepared, string key) =>
        await Activate(new ActivateRestoreInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true), key);

    private async Task<RestoreActivationResult> Activate(ActivateRestoreInput input, string key)
    {
        var result = await restoreService.ActivateAsync(input, actor, key, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(RestoreJsonContext.Default.RestoreActivationResult)!;
    }

    private BackupService BackupFor(LedgerDb target)
    {
        var executor = new LedgerMutationExecutor(target, factory, new IdempotencyStore());
        return new(executor, new DurableLedgerVerifier(protection), new ArtifactReconciler(), protection);
    }

    private RestoreService RestoreFor(LedgerDb target, BackupService backup)
    {
        var verifier = new DurableLedgerVerifier(protection);
        var reconciler = new ArtifactReconciler();
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(target.DataRoot);
        var activator = new AuthoritativeStoreActivator(target, verifier, manager, backup, reconciler, protection);
        return new(target, new LedgerMutationExecutor(target, factory, new IdempotencyStore()), verifier, backup, reconciler, protection, activator);
    }

    private async Task<string> CurrentId() => (await File.ReadAllTextAsync(Path.Combine(database.DataRoot, "CURRENT"))).Trim();

    private async Task<string> CurrentFingerprint()
    {
        var current = new LedgerDb(database.DataRoot, await CurrentId());
        var clone = new LedgerDb(Path.Combine(root, "fingerprints", Guid.NewGuid().ToString("N")), Guid.NewGuid().ToString("N"));
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = current.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = clone.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            protection.EnsureDataRoot(clone.DataRoot);
            protection.EnsureDataRoot(Path.GetDirectoryName(clone.GenerationDirectory)!);
            protection.EnsureDataRoot(clone.GenerationDirectory);
            await source.OpenAsync();
            await target.OpenAsync();
            source.BackupDatabase(target);
        }
        protection.ProtectArtifact(clone.DatabasePath);
        var result = await new DurableLedgerVerifier(protection).VerifyAsync(clone, CancellationToken.None);
        Assert.True(result.IsVerified, result.ErrorCode);
        return result.Report!.NormalizedFingerprint;
    }

    private async Task Execute(LedgerDb target, string sql)
    {
        await using var connection = await factory.OpenAsync(target, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Serialize(RestorePrepareResult value) => JsonSerializer.Serialize(value, RestoreJsonContext.Default.RestorePrepareResult);
}
