using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Application.Ports;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Recovery;

[SupportedOSPlatform("linux")]
public sealed class RestoreService(
    LedgerDb database,
    LedgerMutationExecutor executor,
    DurableLedgerVerifier verifier,
    BackupService backupService,
    ArtifactReconciler artifactReconciler,
    IHostArtifactProtection artifactProtection,
    AuthoritativeStoreActivator activator)
{
    private const string ContractVersion = "1.0";
    private const string PrepareOperationId = "ledger.restore.prepare";
    private const string ActivateOperationId = "ledger.restore.activate";

    public async Task<CommandResult<JsonElement>> PrepareAsync(
        PrepareRestoreInput input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryPath(input.ArtifactPath, out var artifactPath)
            || !IsFingerprint(input.ExpectedArtifactChecksum)
            || actor is null
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Failure(RestoreErrors.Invalid);
        }

        var normalizedInput = new PrepareRestoreInput(artifactPath, input.ExpectedArtifactChecksum.ToLowerInvariant());
        var inputElement = JsonSerializer.SerializeToElement(normalizedInput, RestoreJsonContext.Default.PrepareRestoreInput);
        var actorIdentity = Actor(actor);
        var candidateId = Hash(database.DataRoot + "\n" + normalizedInput.ExpectedArtifactChecksum)[..32];
        var request = new IdempotencyRequest(
            ContractVersion,
            PrepareOperationId,
            idempotencyKey,
            actorIdentity,
            inputElement,
            new LogicalEffectIdentity("restore-prepare:" + candidateId, "restore_prepare"));

        CommandResult<JsonElement> result;
        try
        {
            result = await executor.ExecuteAsync(request, async (_, _, token) =>
            {
                var backup = await backupService.VerifyAsync(
                    new VerifyBackupInput(artifactPath, normalizedInput.ExpectedArtifactChecksum),
                    token);
                if (!backup.IsSuccess) return Failure(MapBackupError(backup.ErrorCode));
                var receipt = backup.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt);
                return receipt is null
                    ? Failure(RestoreErrors.Integrity)
                    : await PrepareCandidateAsync(artifactPath, candidateId, receipt, token);
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(RestoreErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(RestoreErrors.Disk);
        }
        catch (InvalidOperationException)
        {
            return Failure(RestoreErrors.HostProtection);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(RestoreErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure(RestoreErrors.Integrity);
        }

        if (!result.IsSuccess)
        {
            return result.ErrorCode == LedgerMutationExecutor.BusyCode
                ? Failure(RestoreErrors.Busy)
                : result;
        }

        var prepared = result.Value!.Deserialize(RestoreJsonContext.Default.RestorePrepareResult);
        return prepared is not null && await VerifyPreparedCandidateAsync(prepared, cancellationToken)
            ? result
            : Failure(RestoreErrors.Integrity);
    }

    public async Task<CommandResult<JsonElement>> ActivateAsync(
        ActivateRestoreInput input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(input.CandidateId, "N", out _)
            || !IsFingerprint(input.ExpectedCurrentFingerprint)
            || !IsFingerprint(input.ExpectedCandidateFingerprint)
            || actor is null
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Failure(RestoreErrors.Invalid);
        }
        if (!input.AuthorizeReplacement) return Failure(RestoreErrors.NotAuthorized);

        var normalizedInput = input with
        {
            ExpectedCurrentFingerprint = input.ExpectedCurrentFingerprint.ToLowerInvariant(),
            ExpectedCandidateFingerprint = input.ExpectedCandidateFingerprint.ToLowerInvariant()
        };
        var inputElement = JsonSerializer.SerializeToElement(normalizedInput, RestoreJsonContext.Default.ActivateRestoreInput);
        var actorIdentity = Actor(actor);
        var requestFingerprint = new CanonicalRequestHasher().Hash(ContractVersion, ActivateOperationId, actorIdentity, inputElement);
        var request = new IdempotencyRequest(
            ContractVersion,
            ActivateOperationId,
            idempotencyKey,
            actorIdentity,
            inputElement,
            new LogicalEffectIdentity("restore-activation:" + normalizedInput.CandidateId, "restore_activation"));

        CommandResult<JsonElement> result;
        try
        {
            result = await executor.ExecuteAsync(request, async (connection, transaction, token) =>
            {
                var activation = await activator.ActivateAsync(connection, transaction, normalizedInput, requestFingerprint, token);
                return activation.IsSuccess
                    ? Success(activation.Value!)
                    : Failure(activation.ErrorCode!);
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(RestoreErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(RestoreErrors.Disk);
        }
        catch (InvalidOperationException)
        {
            return Failure(RestoreErrors.HostProtection);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(RestoreErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure(RestoreErrors.Integrity);
        }
        return !result.IsSuccess && result.ErrorCode == LedgerMutationExecutor.BusyCode
            ? Failure(RestoreErrors.Busy)
            : result;
    }

    private async Task<CommandResult<JsonElement>> PrepareCandidateAsync(
        string artifactPath,
        string candidateId,
        BackupReceipt source,
        CancellationToken cancellationToken)
    {
        var candidate = new LedgerDb(database.DataRoot, candidateId);
        var current = (await File.ReadAllTextAsync(Path.Combine(database.DataRoot, "CURRENT"), cancellationToken)).Trim();
        if (candidateId == current) return Failure(RestoreErrors.CandidateConflict);
        if (Directory.Exists(candidate.GenerationDirectory))
        {
            return await VerifyCandidateAsync(candidate, source, cancellationToken) is { } existing
                ? Success(existing)
                : Failure(RestoreErrors.CandidateConflict);
        }

        var created = false;
        try
        {
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(candidate.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(candidate.GenerationDirectory);
            created = true;
            var embeddedManifest = await BackupService.ExtractArtifactAsync(artifactPath, candidate.DatabasePath, cancellationToken);
            artifactProtection.ProtectArtifact(candidate.DatabasePath);
            if (!BackupService.SameManifest(source.Manifest, embeddedManifest)) return Failure(RestoreErrors.Integrity);

            var verification = await verifier.VerifyAsync(candidate, source.Manifest.DatabaseChecksum, cancellationToken);
            if (!verification.IsVerified) return Failure(MapVerificationError(verification.ErrorCode));
            var candidateManifest = BackupService.Manifest(verification.Report!, source.Manifest.RequestFingerprint);
            if (!BackupService.SameManifest(source.Manifest, candidateManifest)) return Failure(RestoreErrors.Integrity);

            var fingerprintBytes = Encoding.UTF8.GetBytes(candidateManifest.NormalizedFingerprint);
            await artifactReconciler.ReconcileAsync(
                candidate.ManifestPath,
                fingerprintBytes,
                Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
                cancellationToken);
            return Success(Result(candidateId, source, candidateManifest));
        }
        catch (InvalidDataException)
        {
            return Failure(RestoreErrors.Integrity);
        }
        catch (JsonException)
        {
            return Failure(RestoreErrors.Integrity);
        }
        catch (SqliteException)
        {
            return Failure(RestoreErrors.Integrity);
        }
        finally
        {
            if (created && !File.Exists(candidate.ManifestPath) && Directory.Exists(candidate.GenerationDirectory))
            {
                Directory.Delete(candidate.GenerationDirectory, recursive: true);
            }
        }
    }

    private async Task<RestorePrepareResult?> VerifyCandidateAsync(
        LedgerDb candidate,
        BackupReceipt source,
        CancellationToken cancellationToken)
    {
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(candidate.GenerationDirectory);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.DatabasePath);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.ManifestPath);
            var manifestFingerprint = (await File.ReadAllTextAsync(candidate.ManifestPath, cancellationToken)).Trim();
            if (manifestFingerprint != source.Manifest.NormalizedFingerprint) return null;
            var verification = await verifier.VerifyAsync(candidate, source.Manifest.DatabaseChecksum, cancellationToken);
            if (!verification.IsVerified) return null;
            var candidateManifest = BackupService.Manifest(verification.Report!, source.Manifest.RequestFingerprint);
            return BackupService.SameManifest(source.Manifest, candidateManifest)
                ? Result(candidate.GenerationId, source, candidateManifest)
                : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            return null;
        }
    }

    private async Task<bool> VerifyPreparedCandidateAsync(RestorePrepareResult prepared, CancellationToken cancellationToken)
    {
        var candidate = new LedgerDb(database.DataRoot, prepared.CandidateId);
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(candidate.GenerationDirectory);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.DatabasePath);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.ManifestPath);
            if ((await File.ReadAllTextAsync(candidate.ManifestPath, cancellationToken)).Trim() != prepared.CandidateNormalizedFingerprint)
            {
                return false;
            }
            var verification = await verifier.VerifyAsync(candidate, cancellationToken);
            if (!verification.IsVerified) return false;
            var report = verification.Report!;
            return prepared.SourceNormalizedFingerprint == prepared.CandidateNormalizedFingerprint
                && report.NormalizedFingerprint == prepared.CandidateNormalizedFingerprint
                && report.SchemaVersion == prepared.SchemaVersion
                && report.StorageContractVersion == prepared.StorageContractVersion
                && report.ReconciliationPolicyVersions.SequenceEqual(prepared.ReconciliationPolicyVersions)
                && report.Types.Select(type => new BackupTypeResult(type.Name, type.RowCount, type.Fingerprint)).SequenceEqual(prepared.Types)
                && report.Actuals.Select(actual => new BackupActualsResult(actual.Grouping, actual.MemberCount, actual.CellCount, actual.NetAccountMovementMinor, actual.ExternalSpendMinor, actual.BudgetActualMinor, actual.CellFingerprint)).SequenceEqual(prepared.Actuals)
                && report.CategoryHierarchyFingerprint == prepared.CategoryHierarchyFingerprint
                && report.TransactionReplacementFingerprint == prepared.TransactionReplacementFingerprint
                && report.RelationshipFingerprint == prepared.RelationshipFingerprint
                && report.ReconciliationFingerprint == prepared.ReconciliationFingerprint
                && report.IdempotencyFingerprint == prepared.IdempotencyFingerprint;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            return false;
        }
    }

    private static RestorePrepareResult Result(string candidateId, BackupReceipt source, BackupManifest candidate) => new(
        candidateId,
        source.ArtifactChecksum,
        source.Manifest.NormalizedFingerprint,
        candidate.NormalizedFingerprint,
        candidate.SchemaVersion,
        candidate.StorageContractVersion,
        candidate.ReconciliationPolicyVersions,
        candidate.Types,
        candidate.Actuals,
        candidate.CategoryHierarchyFingerprint,
        candidate.TransactionReplacementFingerprint,
        candidate.RelationshipFingerprint,
        candidate.ReconciliationFingerprint,
        candidate.IdempotencyFingerprint);

    private static bool TryPath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            path = Path.GetFullPath(value);
            return !string.IsNullOrEmpty(Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static string MapBackupError(string? errorCode) => errorCode switch
    {
        BackupErrors.Incompatible => RestoreErrors.Incompatible,
        BackupErrors.HostProtection => RestoreErrors.HostProtection,
        BackupErrors.Permission => RestoreErrors.Permission,
        BackupErrors.Busy => RestoreErrors.Busy,
        BackupErrors.NotFound => BackupErrors.NotFound,
        _ => RestoreErrors.Integrity
    };

    private static string MapVerificationError(string? errorCode) => errorCode switch
    {
        DurableLedgerErrors.SchemaIncompatible or DurableLedgerErrors.PolicyIncompatible => RestoreErrors.Incompatible,
        DurableLedgerErrors.HostProtection => RestoreErrors.HostProtection,
        _ => RestoreErrors.Integrity
    };

    private static CommandResult<JsonElement> Success(RestorePrepareResult value) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(value, RestoreJsonContext.Default.RestorePrepareResult));

    private static CommandResult<JsonElement> Success(RestoreActivationResult value) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(value, RestoreJsonContext.Default.RestoreActivationResult));

    private static CommandResult<JsonElement> Failure(string errorCode) => CommandResult<JsonElement>.Failure(errorCode);
}
