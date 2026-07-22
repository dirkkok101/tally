using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Application.Ports;
using Tally.Contracts.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Recovery;

namespace Tally.Infrastructure.Storage;

public enum AuthoritativeActivationStage
{
    BeforePointerReplacement,
    AfterPointerReplacement
}

[SupportedOSPlatform("linux")]
public sealed class AuthoritativeStoreActivator(
    LedgerDb operationDatabase,
    DurableLedgerVerifier verifier,
    IAuthoritativeStoreActivator generationManager,
    BackupService backupService,
    ArtifactReconciler artifactReconciler,
    IHostArtifactProtection artifactProtection,
    Action<AuthoritativeActivationStage>? checkpoint = null)
{
    private const string ActivationContractVersion = "1";

    public async Task<CommandResult<RestoreActivationResult>> ActivateAsync(
        SqliteConnection lockedConnection,
        SqliteTransaction writerTransaction,
        ActivateRestoreInput input,
        string requestFingerprint,
        CancellationToken cancellationToken) =>
        await ActivateCoreAsync(lockedConnection, writerTransaction, input, requestFingerprint, evolutionSource: false, cancellationToken);

    public async Task<CommandResult<RestoreActivationResult>> ActivateEvolutionAsync(
        SqliteConnection lockedConnection,
        SqliteTransaction writerTransaction,
        ActivateRestoreInput input,
        string requestFingerprint,
        CancellationToken cancellationToken) =>
        await ActivateCoreAsync(lockedConnection, writerTransaction, input, requestFingerprint, evolutionSource: true, cancellationToken);

    private async Task<CommandResult<RestoreActivationResult>> ActivateCoreAsync(
        SqliteConnection lockedConnection,
        SqliteTransaction writerTransaction,
        ActivateRestoreInput input,
        string requestFingerprint,
        bool evolutionSource,
        CancellationToken cancellationToken)
    {
        if (writerTransaction.Connection != lockedConnection
            || !Guid.TryParseExact(input.CandidateId, "N", out _)
            || !IsFingerprint(input.ExpectedCurrentFingerprint)
            || !IsFingerprint(input.ExpectedCandidateFingerprint)
            || !IsFingerprint(requestFingerprint)
            || !input.AuthorizeReplacement)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Invalid);
        }

        var currentPath = Path.Combine(operationDatabase.DataRoot, "CURRENT");
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(operationDatabase.DataRoot);
            artifactProtection.RequireOwnerOnlyArtifact(currentPath);
            var currentId = (await File.ReadAllTextAsync(currentPath, cancellationToken)).Trim();
            if (!Guid.TryParseExact(currentId, "N", out _))
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.ActivationConflict);
            }

            var candidate = new LedgerDb(operationDatabase.DataRoot, input.CandidateId);
            var activationPath = Path.Combine(candidate.GenerationDirectory, "activation");
            var existing = await ReadActivationAsync(activationPath, cancellationToken);
            if (existing is not null && !Matches(existing, input, requestFingerprint))
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.ActivationConflict);
            }

            if (currentId == input.CandidateId)
            {
                if (existing is null) return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.ActivationConflict);
                var currentVerification = await VerifyLockedCurrentAsync(lockedConnection, cancellationToken);
                if (!currentVerification.IsVerified
                    || currentVerification.Report!.NormalizedFingerprint != input.ExpectedCandidateFingerprint)
                {
                    return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.StaleCandidate);
                }
                FlushDirectory(operationDatabase.DataRoot);
                return CommandResult<RestoreActivationResult>.Success(existing.Result);
            }

            if (currentId != operationDatabase.GenerationId)
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.ActivationConflict);
            }

            string currentFingerprint;
            if (evolutionSource)
            {
                var current = await VerifyLockedEvolutionSourceAsync(lockedConnection, cancellationToken);
                if (!current.IsSuccess) return CommandResult<RestoreActivationResult>.Failure(current.ErrorCode!);
                currentFingerprint = current.Value!;
            }
            else
            {
                var current = await VerifyLockedCurrentAsync(lockedConnection, cancellationToken);
                if (!current.IsVerified)
                {
                    return CommandResult<RestoreActivationResult>.Failure(MapVerificationError(current.ErrorCode));
                }
                currentFingerprint = current.Report!.NormalizedFingerprint;
            }
            if (currentFingerprint != input.ExpectedCurrentFingerprint)
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.StaleCurrent);
            }

            var candidateVerification = await verifier.VerifyAsync(candidate, cancellationToken);
            if (!candidateVerification.IsVerified)
            {
                return CommandResult<RestoreActivationResult>.Failure(MapVerificationError(candidateVerification.ErrorCode));
            }
            if (candidateVerification.Report!.NormalizedFingerprint != input.ExpectedCandidateFingerprint)
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.StaleCandidate);
            }
            artifactProtection.RequireOwnerOnlyArtifact(candidate.ManifestPath);
            if ((await File.ReadAllTextAsync(candidate.ManifestPath, cancellationToken)).Trim() != input.ExpectedCandidateFingerprint)
            {
                return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.StaleCandidate);
            }

            var result = new RestoreActivationResult(input.CandidateId, input.ExpectedCandidateFingerprint);
            if (existing is null)
            {
                var artifact = new RestoreActivationArtifact(
                    ActivationContractVersion,
                    requestFingerprint,
                    input.ExpectedCurrentFingerprint,
                    input.ExpectedCandidateFingerprint,
                    result);
                var content = JsonSerializer.SerializeToUtf8Bytes(artifact, RestoreArtifactJsonContext.Default.RestoreActivationArtifact);
                await artifactReconciler.ReconcileAsync(
                    activationPath,
                    content,
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    cancellationToken);
            }

            Flush(candidate.DatabasePath);
            Flush(candidate.ManifestPath);
            Flush(activationPath);
            FlushDirectory(candidate.GenerationDirectory);
            checkpoint?.Invoke(AuthoritativeActivationStage.BeforePointerReplacement);
            await generationManager.ActivateAsync(input.CandidateId, input.ExpectedCandidateFingerprint, cancellationToken);
            FlushDirectory(operationDatabase.DataRoot);
            checkpoint?.Invoke(AuthoritativeActivationStage.AfterPointerReplacement);
            return CommandResult<RestoreActivationResult>.Success(result);
        }
        catch (UnauthorizedAccessException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Permission);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Busy);
        }
        catch (SqliteException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Integrity);
        }
        catch (InvalidDataException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Integrity);
        }
        catch (JsonException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Integrity);
        }
        catch (InvalidOperationException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.HostProtection);
        }
        catch (IOException)
        {
            return CommandResult<RestoreActivationResult>.Failure(RestoreErrors.Disk);
        }
    }

    private async Task<global::Tally.Domain.Ledger.Recovery.DurableLedgerVerificationResult> VerifyLockedCurrentAsync(
        SqliteConnection lockedConnection,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(operationDatabase.DataRoot, ".activation-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            artifactProtection.EnsureDataRoot(root);
            var snapshot = new LedgerDb(root, Guid.NewGuid().ToString("N"));
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(snapshot.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(snapshot.GenerationDirectory);
            return await backupService.SnapshotAndVerifyAsync(lockedConnection, snapshot, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task<CommandResult<string>> VerifyLockedEvolutionSourceAsync(
        SqliteConnection lockedConnection,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(operationDatabase.DataRoot, ".evolution-activation-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            artifactProtection.EnsureDataRoot(root);
            var snapshot = new LedgerDb(root, Guid.NewGuid().ToString("N"));
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(snapshot.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(snapshot.GenerationDirectory);
            await BackupService.OnlineBackupAsync(lockedConnection, snapshot, cancellationToken);
            await BackupService.RemoveEphemeralStateAsync(snapshot.DatabasePath, cancellationToken);
            artifactProtection.ProtectArtifact(snapshot.DatabasePath);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            var integrity = Convert.ToString(
                await LedgerConnectionFactory.ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            var foreignKeys = Convert.ToInt64(
                await LedgerConnectionFactory.ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            var version = Convert.ToInt32(
                await LedgerConnectionFactory.ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) || foreignKeys != 0)
            {
                return CommandResult<string>.Failure(RestoreErrors.Integrity);
            }
            if (version is < 1 or > CompleteLedgerSchema.CurrentVersion)
            {
                return CommandResult<string>.Failure(RestoreErrors.Incompatible);
            }
            return CommandResult<string>.Success(
                await MigrationCandidateBuilder.EvolutionFingerprintAsync(snapshot, artifactProtection, cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task<RestoreActivationArtifact?> ReadActivationAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        artifactProtection.RequireOwnerOnlyArtifact(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(stream, RestoreArtifactJsonContext.Default.RestoreActivationArtifact, cancellationToken)
            ?? throw new InvalidDataException("The activation receipt is invalid.");
    }

    private static bool Matches(RestoreActivationArtifact artifact, ActivateRestoreInput input, string requestFingerprint) =>
        artifact.ContractVersion == ActivationContractVersion
        && artifact.RequestFingerprint == requestFingerprint
        && artifact.ExpectedCurrentFingerprint == input.ExpectedCurrentFingerprint
        && artifact.ExpectedCandidateFingerprint == input.ExpectedCandidateFingerprint
        && artifact.Result is not null
        && artifact.Result.CurrentGenerationId == input.CandidateId
        && artifact.Result.NormalizedFingerprint == input.ExpectedCandidateFingerprint;

    private static string MapVerificationError(string? errorCode) => errorCode switch
    {
        global::Tally.Domain.Ledger.Recovery.DurableLedgerErrors.SchemaIncompatible
            or global::Tally.Domain.Ledger.Recovery.DurableLedgerErrors.PolicyIncompatible => RestoreErrors.Incompatible,
        global::Tally.Domain.Ledger.Recovery.DurableLedgerErrors.HostProtection => RestoreErrors.HostProtection,
        _ => RestoreErrors.Integrity
    };

    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static void Flush(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushDirectory(string path)
    {
        var descriptor = DirectorySync.Open(path, DirectorySync.ReadOnly | DirectorySync.Directory | DirectorySync.CloseOnExec);
        if (descriptor < 0) throw new IOException("The authoritative store directory could not be opened durably.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (DirectorySync.Fsync(descriptor) != 0)
            {
                throw new IOException("The authoritative store directory could not be flushed durably.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = DirectorySync.Close(descriptor);
        }
    }

    private static class DirectorySync
    {
        internal const int ReadOnly = 0;
        internal const int Directory = 0x10000;
        internal const int CloseOnExec = 0x80000;

        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int descriptor);
    }
}
