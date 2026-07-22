using System.Runtime.Versioning;
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
public sealed class StorageEvolutionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-evolution-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private readonly SafeActor actor = new("human", "evolution-test");
    private LedgerDb database = null!;
    private BackupService backupService = null!;
    private StorageEvolutionService service = null!;
    private StorageEvolutionOperationModule module = null!;

    [Fact]
    public void DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_exposes_status_prepare_and_activate_descriptors()
    {
        Assert.Collection(
            module.Descriptors.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal),
            descriptor => Assert.Equal(StorageEvolutionOperationModule.ActivateOperationId, descriptor.OperationId),
            descriptor => Assert.Equal(StorageEvolutionOperationModule.PrepareOperationId, descriptor.OperationId),
            descriptor => Assert.Equal(StorageEvolutionOperationModule.StatusOperationId, descriptor.OperationId));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_status_reports_safe_version_integrity_and_fingerprint_metadata()
    {
        var result = await service.StatusAsync(new(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var status = result.Value!.Deserialize(StorageEvolutionJsonContext.Default.StorageStatusResult)!;
        Assert.Equal(1, status.SchemaVersion);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, status.CurrentSchemaVersion);
        Assert.Equal(database.GenerationId, status.CurrentGenerationId);
        Assert.Equal(64, status.CurrentFingerprint.Length);
        Assert.True(status.OwnerOnlyPermissions);
        Assert.True(status.IntegrityVerified);
        Assert.True(status.HostProtectionVerified);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_startup_does_not_upgrade_the_authoritative_generation_in_place()
    {
        var reopened = await LedgerRuntimeBootstrap.InitializeCurrentAsync(database.DataRoot, CancellationToken.None);

        Assert.Equal(database.GenerationId, reopened.GenerationId);
        Assert.Equal(1, await UserVersion(reopened));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_prepare_creates_distinct_current_candidate_without_changing_current()
    {
        var before = await CurrentId();

        var prepared = await Prepare();

        Assert.Equal(before, await CurrentId());
        Assert.NotEqual(before, prepared.CandidateId);
        Assert.Equal(1, prepared.SourceSchemaVersion);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, prepared.TargetSchemaVersion);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await UserVersion(new LedgerDb(database.DataRoot, prepared.CandidateId)));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_prepare_establishes_independently_verified_recovery_artifact()
    {
        var prepared = await Prepare();
        var artifact = Path.Combine(database.DataRoot, "recovery-artifacts", prepared.CandidateId + ".tally-backup");

        var verified = await backupService.VerifyAsync(new(artifact, prepared.RecoveryArtifactChecksum), CancellationToken.None);

        Assert.True(verified.IsSuccess, verified.ErrorCode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(artifact));
        Assert.True(Directory.Exists(new LedgerDb(Path.Combine(database.DataRoot, "recovery"), prepared.RecoveryGenerationId).GenerationDirectory));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_prepare_returns_complete_candidate_equivalence_report()
    {
        var prepared = await Prepare();

        Assert.Equal(31, prepared.Types.Count);
        Assert.Equal(5, prepared.Actuals.Count);
        Assert.Equal(64, prepared.SourceFingerprint.Length);
        Assert.Equal(64, prepared.CandidateNormalizedFingerprint.Length);
        Assert.All(
            [prepared.CategoryHierarchyFingerprint, prepared.TransactionReplacementFingerprint,
                prepared.RelationshipFingerprint, prepared.ReconciliationFingerprint, prepared.IdempotencyFingerprint],
            fingerprint => Assert.Equal(64, fingerprint.Length));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_prepare_replay_and_cross_key_replay_are_stable()
    {
        var first = await Prepare("prepare-replay");

        var sameKey = await Prepare("prepare-replay");
        var crossKey = await Prepare("prepare-cross-key");

        Assert.Equal(Serialize(first), Serialize(sameKey));
        Assert.Equal(Serialize(first), Serialize(crossKey));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(database.DataRoot, "generations")), path => Path.GetFileName(path) == first.CandidateId);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_actor_replay_conflicts_without_second_candidate()
    {
        var first = await Prepare("prepare-actor");

        var changed = await service.PrepareAsync(
            new(CompleteLedgerSchema.CurrentVersion),
            new SafeActor("human", "different-actor"),
            "prepare-actor",
            CancellationToken.None);

        Assert.False(changed.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, changed.ErrorCode);
        Assert.True(Directory.Exists(new LedgerDb(database.DataRoot, first.CandidateId).GenerationDirectory));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_activation_requires_explicit_authorization()
    {
        var prepared = await Prepare();
        var input = new ActivateStorageEvolutionInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, false);

        var result = await service.ActivateAsync(input, actor, "not-authorized", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.NotAuthorized, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_stale_current_fingerprint_blocks_activation()
    {
        var prepared = await Prepare();

        var result = await service.ActivateAsync(
            new(prepared.CandidateId, new string('0', 64), prepared.CandidateNormalizedFingerprint, true),
            actor,
            "stale-current",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.StaleCurrent, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_stale_candidate_fingerprint_blocks_activation()
    {
        var prepared = await Prepare();

        var result = await service.ActivateAsync(
            new(prepared.CandidateId, await CurrentFingerprint(), new string('0', 64), true),
            actor,
            "stale-candidate",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.StaleCandidate, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_candidate_is_revalidated_immediately_before_activation()
    {
        var prepared = await Prepare();
        var candidate = new LedgerDb(database.DataRoot, prepared.CandidateId);
        await Execute(candidate, "PRAGMA user_version = 1;");

        var result = await service.ActivateAsync(
            new(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true),
            actor,
            "tampered-candidate",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.StaleCandidate, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_source_change_after_prepare_invalidates_candidate_even_with_refreshed_current_fingerprint()
    {
        var prepared = await Prepare();
        await Execute(database, "INSERT INTO account VALUES ('changed-account', 'Bank', 'cheque', 'asset', '9999', 'ZAR', '2026-07-22T00:00:00Z'); INSERT INTO catalogue_lifecycle_event VALUES ('changed-event', 'account', 'changed-account', 'create', NULL, 'Changed', 'changed', NULL, 'test', '2026-07-22T00:00:00Z', NULL);");
        var refreshedCurrent = await CurrentFingerprint();

        var result = await service.ActivateAsync(
            new(prepared.CandidateId, refreshedCurrent, prepared.CandidateNormalizedFingerprint, true),
            actor,
            "changed-source",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.StaleCurrent, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_activation_switches_once_and_retains_prior_generation()
    {
        var prepared = await Prepare();
        var prior = database.GenerationId;

        var activated = await Activate(prepared, "activate");

        Assert.Equal(prepared.CandidateId, activated.CurrentGenerationId);
        Assert.Equal(prepared.CandidateId, await CurrentId());
        Assert.True(Directory.Exists(new LedgerDb(database.DataRoot, prior).GenerationDirectory));
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await UserVersion(new LedgerDb(database.DataRoot, prepared.CandidateId)));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_activation_replay_is_stable_and_changed_actor_conflicts()
    {
        var prepared = await Prepare();
        var input = new ActivateStorageEvolutionInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true);
        var first = await Activate(input, "activation-replay");

        var replay = await Activate(input, "activation-replay");
        var changed = await service.ActivateAsync(input, new SafeActor("human", "different-actor"), "activation-replay", CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.False(changed.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, changed.ErrorCode);
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_crash_before_pointer_replacement_keeps_old_current_and_retry_completes()
    {
        var prepared = await Prepare();
        var input = new ActivateStorageEvolutionInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true);
        var crashing = CreateService(database, stage =>
        {
            if (stage == AuthoritativeActivationStage.BeforePointerReplacement) throw new InjectedEvolutionCrash();
        });

        await Assert.ThrowsAsync<InjectedEvolutionCrash>(() => crashing.ActivateAsync(input, actor, "before-crash", CancellationToken.None));
        Assert.Equal(database.GenerationId, await CurrentId());

        var retry = await service.ActivateAsync(input, actor, "before-crash", CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(prepared.CandidateId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_crash_after_pointer_replacement_is_recovered_from_receipt()
    {
        var prepared = await Prepare();
        var input = new ActivateStorageEvolutionInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true);
        var crashing = CreateService(database, stage =>
        {
            if (stage == AuthoritativeActivationStage.AfterPointerReplacement) throw new InjectedEvolutionCrash();
        });

        await Assert.ThrowsAsync<InjectedEvolutionCrash>(() => crashing.ActivateAsync(input, actor, "after-crash", CancellationToken.None));
        Assert.Equal(prepared.CandidateId, await CurrentId());

        var restarted = CreateService(new LedgerDb(database.DataRoot, prepared.CandidateId));
        var retry = await restarted.ActivateAsync(input, actor, "after-crash", CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_new_current_rejects_a_second_evolution()
    {
        var prepared = await Prepare();
        await Activate(prepared, "activate-current");
        var current = new LedgerDb(database.DataRoot, prepared.CandidateId);
        var currentService = CreateService(current);

        var result = await currentService.PrepareAsync(new(CompleteLedgerSchema.CurrentVersion), actor, "already-current", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.AlreadyCurrent, result.ErrorCode);
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_unsafe_source_is_rejected_without_candidate()
    {
        File.SetUnixFileMode(database.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = await service.PrepareAsync(new(CompleteLedgerSchema.CurrentVersion), actor, "unsafe", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageEvolutionErrors.HostProtection, result.ErrorCode);
        Assert.Equal(database.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_failures_do_not_echo_financial_or_path_values()
    {
        var result = await service.ActivateAsync(
            new("PRIVATE-CANDIDATE-PATH", new string('0', 64), new string('0', 64), true),
            actor,
            "private",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("PRIVATE-CANDIDATE-PATH", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root, result.ToString(), StringComparison.Ordinal);
    }

    public async Task InitializeAsync()
    {
        protection.EnsureDataRoot(root);
        database = await CreateV1CurrentAsync(Path.Combine(root, "live"));
        var factory = new LedgerConnectionFactory(protection);
        var verifier = new DurableLedgerVerifier(protection);
        var reconciler = new ArtifactReconciler();
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        backupService = new BackupService(executor, verifier, reconciler, protection);
        var builder = new MigrationCandidateBuilder(database, factory, verifier, reconciler, protection);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(database.DataRoot);
        var activator = new AuthoritativeStoreActivator(database, verifier, manager, backupService, reconciler, protection);
        service = new(database, executor, builder, backupService, activator);
        module = new(service);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<LedgerDb> CreateV1CurrentAsync(string dataRoot)
    {
        protection.EnsureDataRoot(dataRoot);
        var database = new LedgerDb(dataRoot, Guid.NewGuid().ToString("N"));
        await using (var connection = await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None))
        {
            await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);
        }
        await File.WriteAllTextAsync(database.ManifestPath, "legacy-v1");
        protection.ProtectArtifact(database.ManifestPath);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(dataRoot);
        await manager.ActivateAsync(database.GenerationId, "legacy-v1", CancellationToken.None);
        return database;
    }

    private async Task<StorageEvolutionPrepareResult> Prepare(string key = "prepare")
    {
        var result = await service.PrepareAsync(new(CompleteLedgerSchema.CurrentVersion), actor, key, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult)!;
    }

    private async Task<StorageEvolutionActivationResult> Activate(StorageEvolutionPrepareResult prepared, string key) =>
        await Activate(new ActivateStorageEvolutionInput(prepared.CandidateId, await CurrentFingerprint(), prepared.CandidateNormalizedFingerprint, true), key);

    private async Task<StorageEvolutionActivationResult> Activate(ActivateStorageEvolutionInput input, string key)
    {
        var result = await service.ActivateAsync(input, actor, key, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(StorageEvolutionJsonContext.Default.StorageEvolutionActivationResult)!;
    }

    private StorageEvolutionService CreateService(LedgerDb target, Action<AuthoritativeActivationStage>? checkpoint = null)
    {
        var factory = new LedgerConnectionFactory(protection);
        var verifier = new DurableLedgerVerifier(protection);
        var reconciler = new ArtifactReconciler();
        var executor = new LedgerMutationExecutor(target, factory, new IdempotencyStore());
        var backup = new BackupService(executor, verifier, reconciler, protection);
        var builder = new MigrationCandidateBuilder(target, factory, verifier, reconciler, protection);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(target.DataRoot);
        var activator = new AuthoritativeStoreActivator(target, verifier, manager, backup, reconciler, protection, checkpoint);
        return new(target, executor, builder, backup, activator);
    }

    private async Task<string> CurrentFingerprint()
    {
        var status = await service.StatusAsync(new(), CancellationToken.None);
        Assert.True(status.IsSuccess, status.ErrorCode);
        return status.Value!.Deserialize(StorageEvolutionJsonContext.Default.StorageStatusResult)!.CurrentFingerprint;
    }

    private async Task<string> CurrentId() => (await File.ReadAllTextAsync(Path.Combine(database.DataRoot, "CURRENT"))).Trim();

    private static string Serialize(StorageEvolutionPrepareResult value) =>
        JsonSerializer.Serialize(value, StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult);

    private static async Task<long> UserVersion(LedgerDb target)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
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

    private sealed class InjectedEvolutionCrash : Exception;
}
