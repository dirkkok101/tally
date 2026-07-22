using System.IO.Compression;
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
public sealed class BackupService(
    LedgerMutationExecutor executor,
    DurableLedgerVerifier verifier,
    ArtifactReconciler artifactReconciler,
    IHostArtifactProtection artifactProtection)
{
    private const string ContractVersion = "1.0";
    private const string CreateOperationId = "ledger.backup.create";
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<CommandResult<JsonElement>> CreateAsync(
        CreateBackupInput input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryArtifactPath(input.TargetPath, out var targetPath)
            || actor is null
            || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Failure(BackupErrors.Invalid);
        }

        var parent = Path.GetDirectoryName(targetPath)!;
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(parent);
        }
        catch (InvalidOperationException)
        {
            return Failure(BackupErrors.HostProtection);
        }

        var normalizedInput = new CreateBackupInput(targetPath);
        var inputElement = JsonSerializer.SerializeToElement(normalizedInput, BackupJsonContext.Default.CreateBackupInput);
        var actorIdentity = Actor(actor);
        var requestFingerprint = new CanonicalRequestHasher().Hash(ContractVersion, CreateOperationId, actorIdentity, inputElement);
        var request = new IdempotencyRequest(
            ContractVersion,
            CreateOperationId,
            idempotencyKey,
            actorIdentity,
            inputElement,
            new LogicalEffectIdentity("backup:" + Hash(targetPath), "backup_artifact"));

        CommandResult<JsonElement> result;
        try
        {
            result = await executor.ExecuteAsync(request, async (source, _, token) =>
            {
                if (File.Exists(targetPath))
                {
                    var existing = await VerifyCoreAsync(targetPath, null, token);
                    if (!existing.IsSuccess) return Failure(BackupErrors.TargetExists);
                    var existingReceipt = existing.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt);
                    return existingReceipt is not null
                        && string.Equals(existingReceipt.Manifest.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                        && await SourceMatchesManifestAsync(source, existingReceipt.Manifest, parent, token)
                        ? existing
                        : Failure(BackupErrors.TargetExists);
                }

                return await CreateArtifactAsync(source, targetPath, requestFingerprint, token);
            }, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(BackupErrors.Busy);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            return Failure(BackupErrors.Integrity);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BackupErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(BackupErrors.Disk);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("schema version", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(BackupErrors.Incompatible);
        }

        if (!result.IsSuccess)
        {
            return result.ErrorCode == LedgerMutationExecutor.BusyCode
                ? Failure(BackupErrors.Busy)
                : result;
        }
        var receipt = result.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt);
        if (receipt is null) return Failure(BackupErrors.Integrity);
        var reconciled = await VerifyCoreAsync(targetPath, receipt.ArtifactChecksum, cancellationToken);
        if (!reconciled.IsSuccess) return reconciled;
        var verified = reconciled.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt);
        return verified is not null && SameReceipt(receipt, verified)
            ? result
            : Failure(BackupErrors.Integrity);
    }

    public Task<CommandResult<JsonElement>> VerifyAsync(VerifyBackupInput input, CancellationToken cancellationToken)
    {
        if (!TryArtifactPath(input.ArtifactPath, out var artifactPath)
            || input.ExpectedChecksum is not null && !IsChecksum(input.ExpectedChecksum))
        {
            return Task.FromResult(Failure(BackupErrors.Invalid));
        }

        return VerifyCoreAsync(artifactPath, input.ExpectedChecksum, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> CreateArtifactAsync(
        SqliteConnection source,
        string targetPath,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(targetPath)!;
        var stagingRoot = Path.Combine(parent, ".tally-backup-work-" + Guid.NewGuid().ToString("N"));
        var stagingArtifact = Path.Combine(parent, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".staging");
        try
        {
            artifactProtection.EnsureDataRoot(stagingRoot);
            var stagedDatabase = new LedgerDb(stagingRoot, Guid.NewGuid().ToString("N"));
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(stagedDatabase.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(stagedDatabase.GenerationDirectory);
            var verification = await SnapshotAndVerifyAsync(source, stagedDatabase, cancellationToken);
            if (!verification.IsVerified) return Failure(MapVerificationError(verification.ErrorCode));
            var manifest = Manifest(verification.Report!, requestFingerprint);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest);
            var manifestChecksum = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            await artifactReconciler.ReconcileAsync(stagedDatabase.ManifestPath, manifestBytes, manifestChecksum, cancellationToken);

            await WriteArchiveAsync(stagedDatabase.DatabasePath, stagedDatabase.ManifestPath, stagingArtifact, cancellationToken);
            artifactProtection.ProtectArtifact(stagingArtifact);
            var stagedVerification = await VerifyCoreAsync(stagingArtifact, null, cancellationToken);
            if (!stagedVerification.IsSuccess) return stagedVerification;
            var stagedReceipt = stagedVerification.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)
                ?? throw new InvalidDataException("The verified backup receipt is missing.");

            File.Move(stagingArtifact, targetPath, overwrite: false);
            artifactProtection.ProtectArtifact(targetPath);
            return Success(stagedReceipt with { ArtifactName = Path.GetFileName(targetPath) });
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            return Failure(BackupErrors.TargetExists);
        }
        finally
        {
            if (File.Exists(stagingArtifact)) File.Delete(stagingArtifact);
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private async Task<bool> SourceMatchesManifestAsync(
        SqliteConnection source,
        BackupManifest manifest,
        string parent,
        CancellationToken cancellationToken)
    {
        var comparisonRoot = Path.Combine(parent, ".tally-backup-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            artifactProtection.EnsureDataRoot(comparisonRoot);
            var comparisonDatabase = new LedgerDb(comparisonRoot, Guid.NewGuid().ToString("N"));
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(comparisonDatabase.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(comparisonDatabase.GenerationDirectory);
            var verification = await SnapshotAndVerifyAsync(source, comparisonDatabase, cancellationToken);
            if (!verification.IsVerified) return false;
            var current = Manifest(verification.Report!, manifest.RequestFingerprint);
            return manifest.FormatVersion == current.FormatVersion
                && manifest.SchemaVersion == current.SchemaVersion
                && manifest.StorageContractVersion == current.StorageContractVersion
                && manifest.ReconciliationPolicyVersions.SequenceEqual(current.ReconciliationPolicyVersions, StringComparer.Ordinal)
                && manifest.NormalizedFingerprint == current.NormalizedFingerprint;
        }
        finally
        {
            if (Directory.Exists(comparisonRoot)) Directory.Delete(comparisonRoot, recursive: true);
        }
    }

    private async Task<DurableLedgerVerificationResult> SnapshotAndVerifyAsync(
        SqliteConnection source,
        LedgerDb destination,
        CancellationToken cancellationToken)
    {
        await OnlineBackupAsync(source, destination, cancellationToken);
        await RemoveEphemeralStateAsync(destination.DatabasePath, cancellationToken);
        artifactProtection.ProtectArtifact(destination.DatabasePath);
        return await verifier.VerifyAsync(destination, null, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> VerifyCoreAsync(
        string artifactPath,
        string? expectedChecksum,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(artifactPath)) return Failure(BackupErrors.NotFound);
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(Path.GetDirectoryName(artifactPath)!);
            artifactProtection.RequireOwnerOnlyArtifact(artifactPath);
        }
        catch (InvalidOperationException)
        {
            return Failure(BackupErrors.HostProtection);
        }

        string artifactChecksum;
        try
        {
            artifactChecksum = await ChecksumAsync(artifactPath, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BackupErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(BackupErrors.Integrity);
        }
        if (expectedChecksum is not null && !string.Equals(expectedChecksum, artifactChecksum, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(BackupErrors.ChecksumMismatch);
        }

        var parent = Path.GetDirectoryName(artifactPath)!;
        var extractionRoot = Path.Combine(parent, ".tally-backup-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            artifactProtection.EnsureDataRoot(extractionRoot);
            var extractedDatabase = new LedgerDb(extractionRoot, Guid.NewGuid().ToString("N"));
            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(extractedDatabase.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(extractedDatabase.GenerationDirectory);
            var manifest = await ExtractAsync(artifactPath, extractedDatabase.DatabasePath, cancellationToken);
            artifactProtection.ProtectArtifact(extractedDatabase.DatabasePath);
            if (!IsManifestShapeValid(manifest)) return Failure(BackupErrors.Integrity);

            var verification = await verifier.VerifyAsync(extractedDatabase, manifest.DatabaseChecksum, cancellationToken);
            if (!verification.IsVerified) return Failure(MapVerificationError(verification.ErrorCode));
            var recomputed = Manifest(verification.Report!, manifest.RequestFingerprint);
            if (!SameManifest(manifest, recomputed)) return Failure(BackupErrors.Integrity);

            var receipt = new BackupReceipt(Path.GetFileName(artifactPath), new FileInfo(artifactPath).Length, artifactChecksum, manifest);
            return Success(receipt);
        }
        catch (InvalidDataException)
        {
            return Failure(BackupErrors.Integrity);
        }
        catch (JsonException)
        {
            return Failure(BackupErrors.Integrity);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(BackupErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure(BackupErrors.Integrity);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BackupErrors.Permission);
        }
        catch (IOException)
        {
            return Failure(BackupErrors.Integrity);
        }
        finally
        {
            if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, recursive: true);
        }
    }

    private static async Task OnlineBackupAsync(SqliteConnection lockedSource, LedgerDb destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var snapshotSource = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = lockedSource.DataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await snapshotSource.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        snapshotSource.BackupDatabase(target);
    }

    private static async Task RemoveEphemeralStateAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await LedgerConnectionFactory.ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await using (var transaction = connection.BeginTransaction())
        {
            await LedgerConnectionFactory.ExecuteAsync(connection, "DELETE FROM query_snapshot_payload; DELETE FROM query_snapshot_item; DELETE FROM query_snapshot_group; DELETE FROM query_snapshot;", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        await LedgerConnectionFactory.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await LedgerConnectionFactory.ExecuteAsync(connection, "PRAGMA journal_mode = DELETE;", cancellationToken);
    }

    private async Task<BackupManifest> ExtractAsync(string artifactPath, string databasePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (entries.Length != 2
            || entries.Count(entry => entry.FullName == "ledger.db") != 1
            || entries.Count(entry => entry.FullName == "manifest.json") != 1)
        {
            throw new InvalidDataException("The backup archive shape is invalid.");
        }

        var databaseEntry = entries.Single(entry => entry.FullName == "ledger.db");
        var manifestEntry = entries.Single(entry => entry.FullName == "manifest.json");
        if (databaseEntry.Length <= 0 || manifestEntry.Length <= 0 || manifestEntry.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The backup archive content is invalid.");
        }

        await using (var source = databaseEntry.Open())
        await using (var destination = new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }

        await using var manifestStream = manifestEntry.Open();
        return await JsonSerializer.DeserializeAsync(manifestStream, BackupJsonContext.Default.BackupManifest, cancellationToken)
            ?? throw new InvalidDataException("The backup manifest is missing.");
    }

    private static async Task WriteArchiveAsync(
        string databasePath,
        string manifestPath,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(artifactPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.WriteThrough);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddEntryAsync(archive, "ledger.db", databasePath, cancellationToken);
            await AddEntryAsync(archive, "manifest.json", manifestPath, cancellationToken);
        }
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task AddEntryAsync(ZipArchive archive, string name, string sourcePath, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = ArchiveTimestamp;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static BackupManifest Manifest(DurableLedgerReport report, string requestFingerprint) => new(
        BackupManifest.CurrentFormatVersion,
        requestFingerprint,
        report.Artifacts.Single(artifact => artifact.Name == "ledger.db").Checksum,
        report.SchemaVersion,
        report.StorageContractVersion,
        report.ReconciliationPolicyVersions,
        report.Types.Select(type => new BackupTypeResult(type.Name, type.RowCount, type.Fingerprint)).ToArray(),
        report.Actuals.Select(actual => new BackupActualsResult(actual.Grouping, actual.MemberCount, actual.CellCount, actual.NetAccountMovementMinor, actual.ExternalSpendMinor, actual.BudgetActualMinor, actual.CellFingerprint)).ToArray(),
        report.CategoryHierarchyFingerprint,
        report.TransactionReplacementFingerprint,
        report.RelationshipFingerprint,
        report.ReconciliationFingerprint,
        report.IdempotencyFingerprint,
        report.NormalizedFingerprint);

    private static bool IsManifestShapeValid(BackupManifest manifest) =>
        manifest.FormatVersion == BackupManifest.CurrentFormatVersion
        && IsChecksum(manifest.RequestFingerprint)
        && IsChecksum(manifest.DatabaseChecksum)
        && manifest.Types.Count > 0
        && manifest.Actuals.Count > 0;

    private static bool SameManifest(BackupManifest first, BackupManifest second) =>
        JsonSerializer.Serialize(first, BackupJsonContext.Default.BackupManifest)
        == JsonSerializer.Serialize(second, BackupJsonContext.Default.BackupManifest);

    private static bool SameReceipt(BackupReceipt first, BackupReceipt second) =>
        first.ArtifactName == second.ArtifactName
        && first.ArtifactLength == second.ArtifactLength
        && first.ArtifactChecksum == second.ArtifactChecksum
        && SameManifest(first.Manifest, second.Manifest);

    private static bool TryArtifactPath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            path = Path.GetFullPath(value);
            return !string.IsNullOrEmpty(Path.GetFileName(path)) && Path.GetDirectoryName(path) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static bool IsChecksum(string value) => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string MapVerificationError(string? errorCode) => errorCode switch
    {
        DurableLedgerErrors.SchemaIncompatible or DurableLedgerErrors.PolicyIncompatible => BackupErrors.Incompatible,
        DurableLedgerErrors.HostProtection => BackupErrors.HostProtection,
        DurableLedgerErrors.ChecksumMismatch => BackupErrors.Integrity,
        _ => BackupErrors.Integrity
    };

    private static CommandResult<JsonElement> Success(BackupReceipt receipt) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(receipt, BackupJsonContext.Default.BackupReceipt));

    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
