using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Application.Ports;
using Tally.Contracts.Ledger.Recovery;
using Tally.Domain.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Recovery;

public sealed record StorageSourceInspection(
    int SchemaVersion,
    string SourceFingerprint,
    DurableLedgerVerificationResult CurrentEquivalentVerification);

public sealed record MigrationCandidateResult(
    string CandidateId,
    string RecoveryGenerationId,
    int SourceSchemaVersion,
    int TargetSchemaVersion,
    string SourceFingerprint,
    DurableLedgerVerificationResult CandidateVerification);

[SupportedOSPlatform("linux")]
public sealed class MigrationCandidateBuilder(
    LedgerDb database,
    LedgerConnectionFactory connectionFactory,
    DurableLedgerVerifier verifier,
    ArtifactReconciler artifactReconciler,
    IHostArtifactProtection artifactProtection,
    Func<string, long>? availableSpace = null)
{
    private const string CandidateContractVersion = "1";
    private const long MinimumWorkingSpace = 16L * 1024 * 1024;
    private static readonly string[] ForbiddenSchemaTerms =
    ["mailbox", "mime", "whatsapp", "recipient", "sender", "delivery", "provider_cursor", "raw_payload"];

    public async Task<CommandResult<StorageSourceInspection>> InspectAsync(CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(database.DataRoot, ".evolution-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!TryRequireCurrentProtection()) return Failure<StorageSourceInspection>(StorageEvolutionErrors.HostProtection);
            artifactProtection.EnsureDataRoot(temporaryRoot);
            var snapshot = NewDatabase(temporaryRoot);
            await using var source = await OpenReadOnlyAsync(database, cancellationToken);
            await SnapshotAsync(source, snapshot, cancellationToken);
            var raw = await InspectRawAsync(snapshot, cancellationToken);
            if (!raw.IsSuccess) return Failure<StorageSourceInspection>(raw.ErrorCode!);
            var currentEquivalent = await UpgradeAndVerifyAsync(snapshot, cancellationToken);
            if (!currentEquivalent.IsVerified) return Failure<StorageSourceInspection>(MapVerificationError(currentEquivalent.ErrorCode));
            return CommandResult<StorageSourceInspection>.Success(new(
                raw.Value!.SchemaVersion,
                raw.Value.SourceFingerprint,
                currentEquivalent));
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<StorageSourceInspection>(StorageEvolutionErrors.Permission);
        }
        catch (IOException)
        {
            return Failure<StorageSourceInspection>(StorageEvolutionErrors.Disk);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure<StorageSourceInspection>(StorageEvolutionErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure<StorageSourceInspection>(StorageEvolutionErrors.Integrity);
        }
        catch (InvalidOperationException)
        {
            return Failure<StorageSourceInspection>(StorageEvolutionErrors.HostProtection);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public async Task<CommandResult<MigrationCandidateResult>> BuildAsync(
        SqliteConnection lockedSource,
        SqliteTransaction writerTransaction,
        string candidateId,
        int targetSchemaVersion,
        CancellationToken cancellationToken)
    {
        if (writerTransaction.Connection != lockedSource
            || !Guid.TryParseExact(candidateId, "N", out _)
            || targetSchemaVersion != CompleteLedgerSchema.CurrentVersion)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Incompatible);
        }

        var temporaryRoot = Path.Combine(database.DataRoot, ".evolution-preflight-" + Guid.NewGuid().ToString("N"));
        var candidate = new LedgerDb(database.DataRoot, candidateId);
        var createdCandidate = false;
        try
        {
            if (!TryRequireCurrentProtection()) return Failure<MigrationCandidateResult>(StorageEvolutionErrors.HostProtection);
            artifactProtection.EnsureDataRoot(temporaryRoot);
            var snapshot = NewDatabase(temporaryRoot);
            await SnapshotAsync(lockedSource, snapshot, cancellationToken);
            var raw = await InspectRawAsync(snapshot, cancellationToken);
            if (!raw.IsSuccess) return Failure<MigrationCandidateResult>(raw.ErrorCode!);
            if (raw.Value!.SchemaVersion == targetSchemaVersion)
            {
                return Failure<MigrationCandidateResult>(StorageEvolutionErrors.AlreadyCurrent);
            }
            if (raw.Value.SchemaVersion > targetSchemaVersion)
            {
                return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Incompatible);
            }

            var requiredSpace = RequiredWorkingSpace(new FileInfo(snapshot.DatabasePath).Length);
            if (AvailableSpace(database.DataRoot) < requiredSpace)
            {
                return Failure<MigrationCandidateResult>(StorageEvolutionErrors.InsufficientSpace);
            }

            var recoveryId = Hash(database.GenerationId + "\n" + targetSchemaVersion + "\n" + candidateId)[..32];
            var recovery = new LedgerDb(Path.Combine(database.DataRoot, "recovery"), recoveryId);
            var recoveryResult = await ReconcileRecoveryAsync(snapshot, raw.Value, recovery, cancellationToken);
            if (!recoveryResult.IsSuccess) return Failure<MigrationCandidateResult>(recoveryResult.ErrorCode!);

            if (Directory.Exists(candidate.GenerationDirectory))
            {
                return await VerifyCandidateAsync(candidate, recoveryId, raw.Value, targetSchemaVersion, cancellationToken) is { } existing
                    ? CommandResult<MigrationCandidateResult>.Success(existing)
                    : Failure<MigrationCandidateResult>(StorageEvolutionErrors.CandidateConflict);
            }

            artifactProtection.EnsureDataRoot(Path.GetDirectoryName(candidate.GenerationDirectory)!);
            artifactProtection.EnsureDataRoot(candidate.GenerationDirectory);
            createdCandidate = true;
            await CopyDurablyAsync(recovery.DatabasePath, candidate.DatabasePath, cancellationToken);
            artifactProtection.ProtectArtifact(candidate.DatabasePath);

            await using (var candidateConnection = await connectionFactory.OpenAsync(candidate, CompleteLedgerSchema.CurrentVersion, cancellationToken))
            {
                await CompleteLedgerSchema.CreateCurrent().ApplyAsync(candidateConnection, cancellationToken);
            }
            await BackupService.RemoveEphemeralStateAsync(candidate.DatabasePath, cancellationToken);
            artifactProtection.ProtectArtifact(candidate.DatabasePath);
            var verification = await VerifyDurableCandidateAsync(candidate, cancellationToken);
            if (!verification.IsVerified) return Failure<MigrationCandidateResult>(MapVerificationError(verification.ErrorCode));

            var report = verification.Report!;
            var fingerprintBytes = Encoding.UTF8.GetBytes(report.NormalizedFingerprint);
            await artifactReconciler.ReconcileAsync(
                candidate.ManifestPath,
                fingerprintBytes,
                Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
                cancellationToken);
            var candidateArtifact = new EvolutionCandidateArtifact(
                CandidateContractVersion,
                candidateId,
                recoveryId,
                raw.Value.SchemaVersion,
                targetSchemaVersion,
                raw.Value.SourceFingerprint,
                report.NormalizedFingerprint);
            var artifactBytes = JsonSerializer.SerializeToUtf8Bytes(candidateArtifact, StorageEvolutionArtifactJsonContext.Default.EvolutionCandidateArtifact);
            await artifactReconciler.ReconcileAsync(
                CandidateArtifactPath(candidate),
                artifactBytes,
                Convert.ToHexStringLower(SHA256.HashData(artifactBytes)),
                cancellationToken);
            return CommandResult<MigrationCandidateResult>.Success(Result(candidateArtifact, verification));
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Permission);
        }
        catch (IOException)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Disk);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Busy);
        }
        catch (SqliteException)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Integrity);
        }
        catch (InvalidOperationException)
        {
            return Failure<MigrationCandidateResult>(StorageEvolutionErrors.Integrity);
        }
        finally
        {
            if (createdCandidate && !File.Exists(CandidateArtifactPath(candidate)) && Directory.Exists(candidate.GenerationDirectory))
            {
                Directory.Delete(candidate.GenerationDirectory, recursive: true);
            }
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public async Task<MigrationCandidateResult?> VerifyCandidateAsync(
        string candidateId,
        string sourceFingerprint,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(candidateId, "N", out _) || !IsFingerprint(sourceFingerprint)) return null;
        try
        {
            var candidate = new LedgerDb(database.DataRoot, candidateId);
            var artifact = await ReadCandidateArtifactAsync(candidate, cancellationToken);
            if (artifact is null || artifact.SourceFingerprint != sourceFingerprint) return null;
            var raw = new RawInspection(artifact.SourceSchemaVersion, artifact.SourceFingerprint);
            return await VerifyCandidateAsync(candidate, artifact.RecoveryGenerationId, raw, artifact.TargetSchemaVersion, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException)
        {
            return null;
        }
    }

    public async Task<string?> ReadCandidateSourceFingerprintAsync(string candidateId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(candidateId, "N", out _)) return null;
        try
        {
            var artifact = await ReadCandidateArtifactAsync(new LedgerDb(database.DataRoot, candidateId), cancellationToken);
            return artifact is not null && IsFingerprint(artifact.SourceFingerprint)
                ? artifact.SourceFingerprint
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException)
        {
            return null;
        }
    }

    private async Task<CommandResult<RawInspection>> ReconcileRecoveryAsync(
        LedgerDb snapshot,
        RawInspection source,
        LedgerDb recovery,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(recovery.GenerationDirectory))
        {
            try
            {
                artifactProtection.RequireOwnerOnlyDirectory(recovery.GenerationDirectory);
                artifactProtection.RequireOwnerOnlyArtifact(recovery.DatabasePath);
                artifactProtection.RequireOwnerOnlyArtifact(recovery.ManifestPath);
                var inspected = await InspectRawAsync(recovery, cancellationToken);
                var manifest = (await File.ReadAllTextAsync(recovery.ManifestPath, cancellationToken)).Trim();
                return inspected.IsSuccess
                    && inspected.Value == source
                    && manifest == source.SourceFingerprint
                    ? inspected
                    : Failure<RawInspection>(StorageEvolutionErrors.CandidateConflict);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
            {
                return Failure<RawInspection>(StorageEvolutionErrors.CandidateConflict);
            }
        }

        artifactProtection.EnsureDataRoot(recovery.DataRoot);
        artifactProtection.EnsureDataRoot(Path.GetDirectoryName(recovery.GenerationDirectory)!);
        artifactProtection.EnsureDataRoot(recovery.GenerationDirectory);
        await CopyDurablyAsync(snapshot.DatabasePath, recovery.DatabasePath, cancellationToken);
        artifactProtection.ProtectArtifact(recovery.DatabasePath);
        var fingerprintBytes = Encoding.UTF8.GetBytes(source.SourceFingerprint);
        await artifactReconciler.ReconcileAsync(
            recovery.ManifestPath,
            fingerprintBytes,
            Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
            cancellationToken);
        var verified = await InspectRawAsync(recovery, cancellationToken);
        var verifiedManifest = (await File.ReadAllTextAsync(recovery.ManifestPath, cancellationToken)).Trim();
        return verified.IsSuccess
            && verified.Value == source
            && verifiedManifest == source.SourceFingerprint
            ? verified
            : Failure<RawInspection>(StorageEvolutionErrors.Integrity);
    }

    private async Task<MigrationCandidateResult?> VerifyCandidateAsync(
        LedgerDb candidate,
        string recoveryId,
        RawInspection source,
        int targetSchemaVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(candidate.GenerationDirectory);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.DatabasePath);
            artifactProtection.RequireOwnerOnlyArtifact(candidate.ManifestPath);
            artifactProtection.RequireOwnerOnlyArtifact(CandidateArtifactPath(candidate));
            var artifact = await ReadCandidateArtifactAsync(candidate, cancellationToken);
            if (artifact is null
                || artifact.ContractVersion != CandidateContractVersion
                || artifact.CandidateId != candidate.GenerationId
                || artifact.RecoveryGenerationId != recoveryId
                || artifact.SourceSchemaVersion != source.SchemaVersion
                || artifact.TargetSchemaVersion != targetSchemaVersion
                || artifact.SourceFingerprint != source.SourceFingerprint)
            {
                return null;
            }
            var verification = await VerifyDurableCandidateAsync(candidate, cancellationToken);
            if (!verification.IsVerified
                || verification.Report!.NormalizedFingerprint != artifact.CandidateNormalizedFingerprint
                || (await File.ReadAllTextAsync(candidate.ManifestPath, cancellationToken)).Trim() != artifact.CandidateNormalizedFingerprint)
            {
                return null;
            }
            return Result(artifact, verification);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException)
        {
            return null;
        }
    }

    private async Task<CommandResult<RawInspection>> InspectRawAsync(LedgerDb snapshot, CancellationToken cancellationToken)
    {
        artifactProtection.RequireOwnerOnlyDirectory(snapshot.GenerationDirectory);
        artifactProtection.RequireOwnerOnlyArtifact(snapshot.DatabasePath);
        await using var connection = await OpenReadOnlyAsync(snapshot, cancellationToken);
        if (!string.Equals(await ScalarTextAsync(connection, "PRAGMA integrity_check;", cancellationToken), "ok", StringComparison.OrdinalIgnoreCase)
            || await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken) != 0)
        {
            return Failure<RawInspection>(StorageEvolutionErrors.Integrity);
        }
        var version = checked((int)await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken));
        if (version is < 1 or > CompleteLedgerSchema.CurrentVersion)
        {
            return Failure<RawInspection>(StorageEvolutionErrors.Incompatible);
        }
        if (!await HasExpectedMigrationInventoryAsync(connection, version, cancellationToken)
            || await HasForbiddenSchemaAsync(connection, cancellationToken))
        {
            return Failure<RawInspection>(StorageEvolutionErrors.Incompatible);
        }
        return CommandResult<RawInspection>.Success(new(
            version,
            await EvolutionFingerprintAsync(snapshot, artifactProtection, cancellationToken)));
    }

    private async Task<DurableLedgerVerificationResult> UpgradeAndVerifyAsync(LedgerDb snapshot, CancellationToken cancellationToken)
    {
        await using (var connection = await connectionFactory.OpenAsync(snapshot, CompleteLedgerSchema.CurrentVersion, cancellationToken))
        {
            await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, cancellationToken);
        }
        await BackupService.RemoveEphemeralStateAsync(snapshot.DatabasePath, cancellationToken);
        artifactProtection.ProtectArtifact(snapshot.DatabasePath);
        return await verifier.VerifyAsync(snapshot, cancellationToken);
    }

    private async Task<DurableLedgerVerificationResult> VerifyDurableCandidateAsync(
        LedgerDb candidate,
        CancellationToken cancellationToken)
    {
        var currentPath = Path.Combine(candidate.DataRoot, "CURRENT");
        if (!File.Exists(currentPath)
            || (await File.ReadAllTextAsync(currentPath, cancellationToken)).Trim() != candidate.GenerationId)
        {
            return await verifier.VerifyAsync(candidate, cancellationToken);
        }

        var root = Path.Combine(candidate.DataRoot, ".evolution-candidate-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            artifactProtection.EnsureDataRoot(root);
            var snapshot = NewDatabase(root);
            await using var source = await OpenReadOnlyAsync(candidate, cancellationToken);
            await SnapshotAsync(source, snapshot, cancellationToken);
            return await verifier.VerifyAsync(snapshot, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task SnapshotAsync(SqliteConnection source, LedgerDb destination, CancellationToken cancellationToken)
    {
        artifactProtection.EnsureDataRoot(destination.DataRoot);
        artifactProtection.EnsureDataRoot(Path.GetDirectoryName(destination.GenerationDirectory)!);
        artifactProtection.EnsureDataRoot(destination.GenerationDirectory);
        await BackupService.OnlineBackupAsync(source, destination, cancellationToken);
        await BackupService.RemoveEphemeralStateAsync(destination.DatabasePath, cancellationToken);
        artifactProtection.ProtectArtifact(destination.DatabasePath);
    }

    private bool TryRequireCurrentProtection()
    {
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(database.DataRoot);
            artifactProtection.RequireOwnerOnlyDirectory(database.GenerationDirectory);
            artifactProtection.RequireOwnerOnlyArtifact(database.DatabasePath);
            artifactProtection.RequireOwnerOnlyArtifact(database.ManifestPath);
            var currentPath = Path.Combine(database.DataRoot, "CURRENT");
            artifactProtection.RequireOwnerOnlyArtifact(currentPath);
            return File.ReadAllText(currentPath).Trim() == database.GenerationId;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<EvolutionCandidateArtifact?> ReadCandidateArtifactAsync(LedgerDb candidate, CancellationToken cancellationToken)
    {
        var path = CandidateArtifactPath(candidate);
        if (!File.Exists(path)) return null;
        artifactProtection.RequireOwnerOnlyArtifact(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(stream, StorageEvolutionArtifactJsonContext.Default.EvolutionCandidateArtifact, cancellationToken);
    }

    private static async Task<bool> HasExpectedMigrationInventoryAsync(SqliteConnection connection, int schemaVersion, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, fragment_name FROM migration_metadata ORDER BY version, fragment_name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new List<(long Version, string Name)>();
        while (await reader.ReadAsync(cancellationToken)) actual.Add((reader.GetInt64(0), reader.GetString(1)));
        var expectedNames = schemaVersion switch
        {
            1 => CompleteLedgerSchema.V1FragmentNames,
            2 => CompleteLedgerSchema.CurrentFragmentNames.Where(name => name != "actuals_query_indexes").ToArray(),
            _ => CompleteLedgerSchema.CurrentFragmentNames
        };
        var expected = expectedNames.Select(name => (Version: name switch
        {
            "statement_authority" => 2L,
            "actuals_query_indexes" => 3L,
            _ => 1L
        }, Name: name)).OrderBy(value => value.Version).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();
        return actual.SequenceEqual(expected);
    }

    private static async Task<bool> HasForbiddenSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var text = reader.GetString(0) + "\n" + reader.GetString(1);
            if (ForbiddenSchemaTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(LedgerDb source, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await LedgerConnectionFactory.ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        return connection;
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToString(await ScalarAsync(connection, sql, cancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task CopyDurablyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    internal static async Task<string> EvolutionFingerprintAsync(
        LedgerDb snapshot,
        IHostArtifactProtection protection,
        CancellationToken cancellationToken)
    {
        protection.RequireOwnerOnlyDirectory(snapshot.GenerationDirectory);
        protection.RequireOwnerOnlyArtifact(snapshot.DatabasePath);
        await using var connection = await OpenReadOnlyAsync(snapshot, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "evolution-source-v1");
        Append(hash, await ScalarTextAsync(connection, "PRAGMA user_version;", cancellationToken));

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT type, name, tbl_name, COALESCE(sql, '')
                FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%'
                  AND name NOT IN ('query_snapshot', 'query_snapshot_group', 'query_snapshot_item', 'query_snapshot_payload')
                  AND tbl_name NOT IN ('query_snapshot', 'query_snapshot_group', 'query_snapshot_item', 'query_snapshot_payload')
                ORDER BY type, name;
                """;
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Append(hash, reader.GetString(0));
                Append(hash, reader.GetString(1));
                Append(hash, reader.GetString(2));
                Append(hash, reader.GetString(3));
            }
        }

        var tables = new List<string>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name NOT IN ('query_snapshot', 'query_snapshot_group', 'query_snapshot_item', 'query_snapshot_payload')
                ORDER BY name;
                """;
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = new List<string>();
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.CommandText = "SELECT name FROM pragma_table_info('" + table.Replace("'", "''", StringComparison.Ordinal) + "') ORDER BY cid;";
                await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
            }
            Append(hash, table);
            foreach (var column in columns) Append(hash, column);
            var quoted = columns.Select(Quote).ToArray();
            var filter = table switch
            {
                "idempotency_record" => " WHERE operation_id <> '1.0' || char(10) || 'ledger.storage.evolution.prepare'",
                "logical_effect" => " WHERE idempotency_key NOT IN (SELECT idempotency_key FROM idempotency_record WHERE operation_id = '1.0' || char(10) || 'ledger.storage.evolution.prepare')",
                _ => string.Empty
            };
            await using var rows = connection.CreateCommand();
            rows.CommandText = "SELECT " + string.Join(", ", quoted) + " FROM " + Quote(table) + filter + " ORDER BY " + string.Join(", ", quoted) + ";";
            await using var rowReader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await rowReader.ReadAsync(cancellationToken))
            {
                Append(hash, "row");
                for (var index = 0; index < rowReader.FieldCount; index++)
                {
                    Append(hash, Cell(rowReader, index));
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Cell(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return "null";
        return reader.GetValue(ordinal) switch
        {
            byte[] value => "blob:" + Convert.ToHexString(value),
            long value => "integer:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double value => "real:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string value => "text:" + value,
            var value => "value:" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(Encoding.UTF8.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        hash.AppendData([0]);
        hash.AppendData(bytes);
    }

    private long AvailableSpace(string path) => availableSpace?.Invoke(path)
        ?? new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;

    private static long RequiredWorkingSpace(long sourceLength) => checked(Math.Max(MinimumWorkingSpace, sourceLength * 4));
    private static LedgerDb NewDatabase(string root) => new(root, Guid.NewGuid().ToString("N"));
    private static string CandidateArtifactPath(LedgerDb candidate) => Path.Combine(candidate.GenerationDirectory, "evolution");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsFingerprint(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static MigrationCandidateResult Result(EvolutionCandidateArtifact artifact, DurableLedgerVerificationResult verification) => new(
        artifact.CandidateId,
        artifact.RecoveryGenerationId,
        artifact.SourceSchemaVersion,
        artifact.TargetSchemaVersion,
        artifact.SourceFingerprint,
        verification);

    private static string MapVerificationError(string? error) => error switch
    {
        DurableLedgerErrors.SchemaIncompatible or DurableLedgerErrors.PolicyIncompatible => StorageEvolutionErrors.Incompatible,
        DurableLedgerErrors.HostProtection => StorageEvolutionErrors.HostProtection,
        _ => StorageEvolutionErrors.Integrity
    };

    private static CommandResult<T> Failure<T>(string error) => CommandResult<T>.Failure(error);

    private sealed record RawInspection(int SchemaVersion, string SourceFingerprint);
}
