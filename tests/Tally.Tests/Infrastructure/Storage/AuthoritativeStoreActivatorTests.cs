using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class AuthoritativeStoreActivatorTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-authoritative-activation-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private readonly SafeActor actor = new("human", "activation-test");
    private LedgerDb current = null!;
    private LedgerConnectionFactory factory = null!;
    private BackupService backupService = null!;
    private RestorePrepareResult prepared = null!;

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_crash_before_pointer_replacement_keeps_old_current_and_exact_retry_completes()
    {
        var currentFingerprint = await Fingerprint(current);
        var input = new ActivateRestoreInput(prepared.CandidateId, currentFingerprint, prepared.CandidateNormalizedFingerprint, true);
        var crashing = Activator(stage =>
        {
            if (stage == AuthoritativeActivationStage.BeforePointerReplacement) throw new InjectedActivationCrash();
        });

        await Assert.ThrowsAsync<InjectedActivationCrash>(() => Invoke(crashing, input, "request-fingerprint"));
        Assert.Equal(current.GenerationId, await CurrentId());

        var retry = await Invoke(Activator(), input, "request-fingerprint");
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(prepared.CandidateId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_crash_after_pointer_replacement_is_reconciled_from_receipt()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, await Fingerprint(current), prepared.CandidateNormalizedFingerprint, true);
        var crashing = Activator(stage =>
        {
            if (stage == AuthoritativeActivationStage.AfterPointerReplacement) throw new InjectedActivationCrash();
        });

        await Assert.ThrowsAsync<InjectedActivationCrash>(() => Invoke(crashing, input, "after-request"));
        Assert.Equal(prepared.CandidateId, await CurrentId());

        var candidate = new LedgerDb(current.DataRoot, prepared.CandidateId);
        var retry = await Invoke(Activator(candidate), input, "after-request", candidate);
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(prepared.CandidateId, retry.Value!.CurrentGenerationId);
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_changed_receipt_replay_is_rejected()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, await Fingerprint(current), prepared.CandidateNormalizedFingerprint, true);
        var crashing = Activator(stage =>
        {
            if (stage == AuthoritativeActivationStage.BeforePointerReplacement) throw new InjectedActivationCrash();
        });
        await Assert.ThrowsAsync<InjectedActivationCrash>(() => Invoke(crashing, input, "original-request"));

        var result = await Invoke(Activator(), input, "changed-request");

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.ActivationConflict, result.ErrorCode);
        Assert.Equal(current.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_malformed_receipt_is_rejected_without_changing_current()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, await Fingerprint(current), prepared.CandidateNormalizedFingerprint, true);
        var candidate = new LedgerDb(current.DataRoot, prepared.CandidateId);
        var activationPath = Path.Combine(candidate.GenerationDirectory, "activation");
        await File.WriteAllTextAsync(activationPath, JsonSerializer.Serialize(new
        {
            contractVersion = "1",
            requestFingerprint = Hash("malformed-request"),
            expectedCurrentFingerprint = input.ExpectedCurrentFingerprint,
            expectedCandidateFingerprint = input.ExpectedCandidateFingerprint,
            result = (object?)null
        }));
        protection.ProtectArtifact(activationPath);

        var result = await Invoke(Activator(), input, "malformed-request");

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.ActivationConflict, result.ErrorCode);
        Assert.Equal(current.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_revalidates_current_under_the_writer_lock()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, new string('0', 64), prepared.CandidateNormalizedFingerprint, true);

        var result = await Invoke(Activator(), input, "stale-current");

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.StaleCurrent, result.ErrorCode);
        Assert.Equal(current.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_revalidates_candidate_under_the_writer_lock()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, await Fingerprint(current), new string('0', 64), true);

        var result = await Invoke(Activator(), input, "stale-candidate");

        Assert.False(result.IsSuccess);
        Assert.Equal(RestoreErrors.StaleCandidate, result.ErrorCode);
        Assert.Equal(current.GenerationId, await CurrentId());
    }

    [Fact]
    public async Task DD_LEDGER_CANDIDATE_ACTIVATION_success_retains_prior_generation_and_owner_only_receipt()
    {
        var input = new ActivateRestoreInput(prepared.CandidateId, await Fingerprint(current), prepared.CandidateNormalizedFingerprint, true);

        var result = await Invoke(Activator(), input, "success");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(Directory.Exists(current.GenerationDirectory));
        var receipt = Path.Combine(new LedgerDb(current.DataRoot, prepared.CandidateId).GenerationDirectory, "activation");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(receipt));
    }

    public async Task InitializeAsync()
    {
        protection.EnsureDataRoot(root);
        current = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "live"), CancellationToken.None);
        factory = new(protection);
        backupService = BackupFor(current);
        var backupRoot = Path.Combine(root, "backups");
        protection.EnsureDataRoot(backupRoot);
        var backupPath = Path.Combine(backupRoot, "ledger.tally-backup");
        var backupResult = await backupService.CreateAsync(new(backupPath), actor, "backup", CancellationToken.None);
        Assert.True(backupResult.IsSuccess, backupResult.ErrorCode);
        var backup = backupResult.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)!;
        var restore = RestoreFor(current, backupService);
        var prepareResult = await restore.PrepareAsync(new(backupPath, backup.ArtifactChecksum), actor, "prepare", CancellationToken.None);
        Assert.True(prepareResult.IsSuccess, prepareResult.ErrorCode);
        prepared = prepareResult.Value!.Deserialize(RestoreJsonContext.Default.RestorePrepareResult)!;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private AuthoritativeStoreActivator Activator(Action<AuthoritativeActivationStage>? checkpoint = null) => Activator(current, checkpoint);

    private AuthoritativeStoreActivator Activator(LedgerDb operationDatabase, Action<AuthoritativeActivationStage>? checkpoint = null)
    {
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(operationDatabase.DataRoot);
        return new(operationDatabase, new DurableLedgerVerifier(protection), manager, BackupFor(operationDatabase), new ArtifactReconciler(), protection, checkpoint);
    }

    private async Task<CommandResult<RestoreActivationResult>> Invoke(
        AuthoritativeStoreActivator activator,
        ActivateRestoreInput input,
        string requestFingerprint,
        LedgerDb? operationDatabase = null)
    {
        await using var connection = await factory.OpenAsync(operationDatabase ?? current, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var result = await activator.ActivateAsync(connection, transaction, input, Hash(requestFingerprint), CancellationToken.None);
            await transaction.RollbackAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private BackupService BackupFor(LedgerDb target) => new(
        new LedgerMutationExecutor(target, factory, new IdempotencyStore()),
        new DurableLedgerVerifier(protection),
        new ArtifactReconciler(),
        protection);

    private RestoreService RestoreFor(LedgerDb target, BackupService backup)
    {
        var verifier = new DurableLedgerVerifier(protection);
        var reconciler = new ArtifactReconciler();
        var activator = Activator(target);
        return new(target, new LedgerMutationExecutor(target, factory, new IdempotencyStore()), verifier, backup, reconciler, protection, activator);
    }

    private async Task<string> CurrentId() => (await File.ReadAllTextAsync(Path.Combine(current.DataRoot, "CURRENT"))).Trim();

    private async Task<string> Fingerprint(LedgerDb sourceDatabase)
    {
        var clone = new LedgerDb(Path.Combine(root, "fingerprints", Guid.NewGuid().ToString("N")), Guid.NewGuid().ToString("N"));
        protection.EnsureDataRoot(clone.DataRoot);
        protection.EnsureDataRoot(Path.GetDirectoryName(clone.GenerationDirectory)!);
        protection.EnsureDataRoot(clone.GenerationDirectory);
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabase.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = clone.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await source.OpenAsync();
            await target.OpenAsync();
            source.BackupDatabase(target);
        }
        protection.ProtectArtifact(clone.DatabasePath);
        var verification = await new DurableLedgerVerifier(protection).VerifyAsync(clone, CancellationToken.None);
        Assert.True(verification.IsVerified, verification.ErrorCode);
        return verification.Report!.NormalizedFingerprint;
    }

    private sealed class InjectedActivationCrash : Exception;

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
