using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Recovery;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Recovery;

[SupportedOSPlatform("linux")]
public sealed class StorageEvolutionService(
    LedgerDb database,
    LedgerMutationExecutor executor,
    MigrationCandidateBuilder builder,
    BackupService backupService,
    AuthoritativeStoreActivator activator)
{
    private const string ContractVersion = "1.0";
    private const string PrepareOperationId = "ledger.storage.evolution.prepare";
    private const string ActivateOperationId = "ledger.storage.evolution.activate";

    public async Task<CommandResult<JsonElement>> StatusAsync(StorageStatusInput input, CancellationToken cancellationToken)
    {
        _ = input;
        var inspection = await builder.InspectAsync(cancellationToken);
        if (!inspection.IsSuccess) return Failure(inspection.ErrorCode!);
        var report = inspection.Value!.CurrentEquivalentVerification.Report!;
        var result = new StorageStatusResult(
            ContractVersion,
            inspection.Value.SchemaVersion,
            CompleteLedgerSchema.CurrentVersion,
            report.StorageContractVersion,
            report.ReconciliationPolicyVersions,
            database.GenerationId,
            inspection.Value.SourceFingerprint,
            OwnerOnlyPermissions: true,
            IntegrityVerified: true,
            HostProtectionVerified: true);
        return Success(result);
    }

    public async Task<CommandResult<JsonElement>> PrepareAsync(
        PrepareStorageEvolutionInput input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (input.TargetSchemaVersion != CompleteLedgerSchema.CurrentVersion
            || actor is null
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Failure(StorageEvolutionErrors.Invalid);
        }

        var preflight = await builder.InspectAsync(cancellationToken);
        if (!preflight.IsSuccess) return Failure(preflight.ErrorCode!);
        if (preflight.Value!.SchemaVersion == input.TargetSchemaVersion)
        {
            return Failure(StorageEvolutionErrors.AlreadyCurrent);
        }
        if (preflight.Value.SchemaVersion > input.TargetSchemaVersion)
        {
            return Failure(StorageEvolutionErrors.Incompatible);
        }

        var inputElement = JsonSerializer.SerializeToElement(input, StorageEvolutionJsonContext.Default.PrepareStorageEvolutionInput);
        var actorIdentity = Actor(actor);
        var requestFingerprint = new CanonicalRequestHasher().Hash(ContractVersion, PrepareOperationId, actorIdentity, inputElement);
        var candidateId = Hash(database.DataRoot + "\n" + database.GenerationId + "\n" + input.TargetSchemaVersion)[..32];
        var request = new IdempotencyRequest(
            ContractVersion,
            PrepareOperationId,
            idempotencyKey,
            actorIdentity,
            inputElement,
            new LogicalEffectIdentity(
                "storage-evolution:" + database.GenerationId + ":" + input.TargetSchemaVersion,
                "storage_evolution_prepare"));

        CommandResult<JsonElement> result;
        try
        {
            result = await executor.ExecuteAsync(request, async (connection, transaction, token) =>
            {
                var candidate = await builder.BuildAsync(connection, transaction, candidateId, input.TargetSchemaVersion, token);
                if (!candidate.IsSuccess) return Failure(candidate.ErrorCode!);

                var artifactRoot = Path.Combine(database.DataRoot, "recovery-artifacts");
                Directory.CreateDirectory(artifactRoot);
                File.SetUnixFileMode(artifactRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var artifactPath = Path.Combine(artifactRoot, candidateId + ".tally-backup");
                var backup = await backupService.CreateVerifiedArtifactAsync(
                    new LedgerDb(database.DataRoot, candidateId),
                    artifactPath,
                    requestFingerprint,
                    token);
                if (!backup.IsSuccess) return Failure(MapBackupError(backup.ErrorCode));

                var independentlyVerified = await backupService.VerifyAsync(
                    new VerifyBackupInput(artifactPath, backup.Value!.ArtifactChecksum),
                    token);
                var verifiedReceipt = independentlyVerified.IsSuccess
                    ? independentlyVerified.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)
                    : null;
                if (verifiedReceipt is null
                    || verifiedReceipt.Manifest.NormalizedFingerprint != candidate.Value!.CandidateVerification.Report!.NormalizedFingerprint)
                {
                    return Failure(independentlyVerified.ErrorCode is null
                        ? StorageEvolutionErrors.Integrity
                        : MapBackupError(independentlyVerified.ErrorCode));
                }

                return Success(Result(candidate.Value, verifiedReceipt));
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(StorageEvolutionErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(StorageEvolutionErrors.Disk);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(StorageEvolutionErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure(StorageEvolutionErrors.Integrity);
        }
        catch (InvalidOperationException)
        {
            return Failure(StorageEvolutionErrors.HostProtection);
        }

        if (!result.IsSuccess)
        {
            return result.ErrorCode == LedgerMutationExecutor.BusyCode
                ? Failure(StorageEvolutionErrors.Busy)
                : result;
        }
        var prepared = result.Value!.Deserialize(StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult);
        return prepared is not null && await VerifyPreparedAsync(prepared, cancellationToken)
            ? result
            : Failure(StorageEvolutionErrors.Integrity);
    }

    public async Task<CommandResult<JsonElement>> ActivateAsync(
        ActivateStorageEvolutionInput input,
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
            return Failure(StorageEvolutionErrors.Invalid);
        }
        if (!input.AuthorizeReplacement) return Failure(StorageEvolutionErrors.NotAuthorized);

        var normalized = input with
        {
            ExpectedCurrentFingerprint = input.ExpectedCurrentFingerprint.ToLowerInvariant(),
            ExpectedCandidateFingerprint = input.ExpectedCandidateFingerprint.ToLowerInvariant()
        };
        var preparedSourceFingerprint = await builder.ReadCandidateSourceFingerprintAsync(normalized.CandidateId, cancellationToken);
        if (preparedSourceFingerprint is null)
        {
            return Failure(StorageEvolutionErrors.StaleCandidate);
        }
        if (preparedSourceFingerprint != normalized.ExpectedCurrentFingerprint)
        {
            return Failure(StorageEvolutionErrors.StaleCurrent);
        }
        var preparedCandidate = await builder.VerifyCandidateAsync(
            normalized.CandidateId,
            preparedSourceFingerprint,
            cancellationToken);
        if (preparedCandidate is null) return Failure(StorageEvolutionErrors.StaleCandidate);
        if (preparedCandidate.CandidateVerification.Report!.NormalizedFingerprint != normalized.ExpectedCandidateFingerprint)
        {
            return Failure(StorageEvolutionErrors.StaleCandidate);
        }
        var inputElement = JsonSerializer.SerializeToElement(normalized, StorageEvolutionJsonContext.Default.ActivateStorageEvolutionInput);
        var actorIdentity = Actor(actor);
        var requestFingerprint = new CanonicalRequestHasher().Hash(ContractVersion, ActivateOperationId, actorIdentity, inputElement);
        var request = new IdempotencyRequest(
            ContractVersion,
            ActivateOperationId,
            idempotencyKey,
            actorIdentity,
            inputElement,
            new LogicalEffectIdentity("storage-evolution-activation:" + normalized.CandidateId, "storage_evolution_activation"));

        CommandResult<JsonElement> result;
        try
        {
            result = await executor.ExecuteAsync(request, async (connection, transaction, token) =>
            {
                var activation = await activator.ActivateEvolutionAsync(
                    connection,
                    transaction,
                    new ActivateRestoreInput(
                        normalized.CandidateId,
                        normalized.ExpectedCurrentFingerprint,
                        normalized.ExpectedCandidateFingerprint,
                        normalized.AuthorizeReplacement),
                    requestFingerprint,
                    token);
                return activation.IsSuccess
                    ? Success(new StorageEvolutionActivationResult(
                        activation.Value!.CurrentGenerationId,
                        activation.Value.NormalizedFingerprint))
                    : Failure(MapActivationError(activation.ErrorCode));
            }, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(StorageEvolutionErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(StorageEvolutionErrors.Disk);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(StorageEvolutionErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure(StorageEvolutionErrors.Integrity);
        }
        catch (InvalidOperationException)
        {
            return Failure(StorageEvolutionErrors.HostProtection);
        }
        return !result.IsSuccess && result.ErrorCode == LedgerMutationExecutor.BusyCode
            ? Failure(StorageEvolutionErrors.Busy)
            : result;
    }

    private async Task<bool> VerifyPreparedAsync(StorageEvolutionPrepareResult prepared, CancellationToken cancellationToken)
    {
        var candidate = await builder.VerifyCandidateAsync(prepared.CandidateId, prepared.SourceFingerprint, cancellationToken);
        if (candidate is null) return false;
        var artifactPath = Path.Combine(database.DataRoot, "recovery-artifacts", prepared.CandidateId + ".tally-backup");
        var backup = await backupService.VerifyAsync(new(artifactPath, prepared.RecoveryArtifactChecksum), cancellationToken);
        var receipt = backup.IsSuccess ? backup.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt) : null;
        if (receipt is null) return false;
        var recomputed = Result(candidate, receipt);
        return JsonSerializer.Serialize(recomputed, StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult)
            == JsonSerializer.Serialize(prepared, StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult);
    }

    private static StorageEvolutionPrepareResult Result(MigrationCandidateResult candidate, BackupReceipt backup)
    {
        var report = candidate.CandidateVerification.Report!;
        return new(
            candidate.CandidateId,
            candidate.RecoveryGenerationId,
            backup.ArtifactChecksum,
            candidate.SourceSchemaVersion,
            candidate.TargetSchemaVersion,
            candidate.SourceFingerprint,
            report.NormalizedFingerprint,
            report.StorageContractVersion,
            report.ReconciliationPolicyVersions,
            report.Types.Select(type => new BackupTypeResult(type.Name, type.RowCount, type.Fingerprint)).ToArray(),
            report.Actuals.Select(actual => new BackupActualsResult(actual.Grouping, actual.MemberCount, actual.CellCount, actual.NetAccountMovementMinor, actual.ExternalSpendMinor, actual.BudgetActualMinor, actual.CellFingerprint)).ToArray(),
            report.CategoryHierarchyFingerprint,
            report.TransactionReplacementFingerprint,
            report.RelationshipFingerprint,
            report.ReconciliationFingerprint,
            report.IdempotencyFingerprint);
    }

    private static string MapBackupError(string? error) => error switch
    {
        BackupErrors.Incompatible => StorageEvolutionErrors.Incompatible,
        BackupErrors.HostProtection => StorageEvolutionErrors.HostProtection,
        BackupErrors.Permission => StorageEvolutionErrors.Permission,
        BackupErrors.Disk => StorageEvolutionErrors.Disk,
        BackupErrors.Busy => StorageEvolutionErrors.Busy,
        BackupErrors.TargetExists => StorageEvolutionErrors.CandidateConflict,
        _ => StorageEvolutionErrors.Integrity
    };

    private static string MapActivationError(string? error) => error switch
    {
        RestoreErrors.Invalid => StorageEvolutionErrors.Invalid,
        RestoreErrors.StaleCurrent => StorageEvolutionErrors.StaleCurrent,
        RestoreErrors.StaleCandidate => StorageEvolutionErrors.StaleCandidate,
        RestoreErrors.ActivationConflict => StorageEvolutionErrors.ActivationConflict,
        RestoreErrors.NotAuthorized => StorageEvolutionErrors.NotAuthorized,
        RestoreErrors.Incompatible => StorageEvolutionErrors.Incompatible,
        RestoreErrors.HostProtection => StorageEvolutionErrors.HostProtection,
        RestoreErrors.Permission => StorageEvolutionErrors.Permission,
        RestoreErrors.Disk => StorageEvolutionErrors.Disk,
        RestoreErrors.Busy => StorageEvolutionErrors.Busy,
        _ => StorageEvolutionErrors.Integrity
    };

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static bool IsFingerprint(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static CommandResult<JsonElement> Success(StorageStatusResult value) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(value, StorageEvolutionJsonContext.Default.StorageStatusResult));

    private static CommandResult<JsonElement> Success(StorageEvolutionPrepareResult value) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(value, StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult));

    private static CommandResult<JsonElement> Success(StorageEvolutionActivationResult value) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(value, StorageEvolutionJsonContext.Default.StorageEvolutionActivationResult));

    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
