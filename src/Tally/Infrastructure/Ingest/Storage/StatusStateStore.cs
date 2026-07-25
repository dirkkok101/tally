using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Recovery;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class StatusStateStore(IngestDatabase database, BatchErrorEventStore errorEvents)
{
    public const string ContractVersion = "1.0";
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);

    public async Task<BatchStatusDetail?> DetailAsync(string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var batch = await ReadBatchAsync(connection, null, batchId, cancellationToken);
        if (batch is null) return null;

        var state = await ReadProjectionStateAsync(connection, null, batchId, cancellationToken);
        var summary = Summary(batch, state.ManifestCounts, state.Committable);
        var error = await errorEvents.LatestAsync(connection, batchId, cancellationToken);
        return new(
            summary,
            state.ManifestRevisionId,
            state.Approved,
            state.ReceiptStatus,
            state.TerminalCounts,
            state.UnresolvedFrontier,
            error,
            state.RetainedArtifactKinds);
    }

    public async Task<StatusSnapshotPage> CreateSnapshotAsync(
        int pageSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var createdAt = Utc(now);
        var expiresAt = Utc(now.Add(SnapshotLifetime));
        await DeleteExpiredAsync(connection, transaction, createdAt, cancellationToken);

        var generation = await StoreGenerationAsync(connection, transaction, cancellationToken);
        var summaries = await ReadSummariesAsync(connection, transaction, cancellationToken);
        var snapshotId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await InsertSnapshotAsync(connection, transaction, snapshotId, generation, createdAt, expiresAt, summaries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var items = summaries.Take(pageSize).ToArray();
        return new(
            snapshotId,
            generation,
            expiresAt,
            pageSize,
            items,
            items.Length < summaries.Count ? items.Length : null);
    }

    public async Task<StatusSnapshotReadResult> ReadSnapshotAsync(
        IngestStatusCursorPayload cursor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!TryUtc(cursor.ExpiresAt, out var cursorExpiry) || cursorExpiry <= now.ToUniversalTime())
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.SnapshotExpired);
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        var generation = await StoreGenerationAsync(connection, null, cancellationToken);
        if (!string.Equals(cursor.StoreGeneration, generation, StringComparison.Ordinal))
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.GenerationMismatch);
        }

        var header = await ReadSnapshotHeaderAsync(connection, cursor.SnapshotId, cancellationToken);
        if (header is null) return StatusSnapshotReadResult.Failure(StatusErrors.SnapshotNotFound);
        if (!string.Equals(cursor.ContractVersion, header.ContractVersion, StringComparison.Ordinal))
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.ContractMismatch);
        }
        if (!string.Equals(cursor.StoreGeneration, header.StoreGeneration, StringComparison.Ordinal))
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.GenerationMismatch);
        }
        if (!string.Equals(cursor.ExpiresAt, header.ExpiresAt, StringComparison.Ordinal)
            || !TryUtc(header.ExpiresAt, out var storedExpiry)
            || storedExpiry <= now.ToUniversalTime())
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.SnapshotExpired);
        }
        if (cursor.NextOrdinal < 1
            || cursor.NextOrdinal >= header.TotalCount
            || cursor.PageSize is < 1 or > 100)
        {
            return StatusSnapshotReadResult.Failure(StatusErrors.CursorInvalid);
        }

        var items = await ReadSnapshotItemsAsync(connection, cursor.SnapshotId, cursor.NextOrdinal, cursor.PageSize, cancellationToken);
        if (items.Count == 0) return StatusSnapshotReadResult.Failure(StatusErrors.CursorInvalid);
        var nextOrdinal = cursor.NextOrdinal + items.Count;
        return StatusSnapshotReadResult.Success(new(
            cursor.SnapshotId,
            header.StoreGeneration,
            header.ExpiresAt,
            cursor.PageSize,
            items,
            nextOrdinal < header.TotalCount ? nextOrdinal : null));
    }

    private static async Task<IReadOnlyList<BatchStatusSummary>> ReadSummariesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT batch_id, status, adapter_identity, created_at, updated_at
            FROM ingest_batch
            ORDER BY updated_at DESC, batch_id;
            """;
        var batches = new List<BatchRow>();
        await using (var command = Command(connection, transaction, sql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                batches.Add(new(
                    reader.GetString(0),
                    (BatchStatus)reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        var summaries = new List<BatchStatusSummary>(batches.Count);
        foreach (var batch in batches)
        {
            var state = await ReadProjectionStateAsync(connection, transaction, batch.BatchId, cancellationToken);
            summaries.Add(Summary(batch, state.ManifestCounts, state.Committable));
        }
        return summaries;
    }

    private static async Task<ProjectionState> ReadProjectionStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        var manifest = await LatestManifestAsync(connection, transaction, batchId, cancellationToken);
        var manifestCounts = manifest is null
            ? EmptyCounts
            : await ManifestCountsAsync(connection, transaction, manifest.RevisionId, cancellationToken);
        var approved = manifest is not null
            && await ExistsAsync(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM manifest_approval WHERE manifest_revision_id = $id AND active = 1);",
                "$id",
                manifest.RevisionId,
                cancellationToken);
        var receipt = await LatestReceiptAsync(connection, transaction, batchId, cancellationToken);
        var terminalCounts = receipt is null
            ? EmptyCounts
            : await TerminalCountsAsync(connection, transaction, receipt.ReceiptId, cancellationToken);
        var frontier = receipt is null
            ? Array.Empty<string>()
            : await UnresolvedFrontierAsync(connection, transaction, receipt.ReceiptId, cancellationToken);

        var retained = new List<ArtifactKind>();
        if (manifest is not null) retained.Add(ArtifactKind.Manifest);
        if (manifest is not null && await ExistsAsync(
                connection,
                transaction,
                "SELECT EXISTS(SELECT 1 FROM import_candidate WHERE manifest_revision_id = $id);",
                "$id",
                manifest.RevisionId,
                cancellationToken))
        {
            retained.Add(ArtifactKind.Candidates);
        }
        if (receipt is not null) retained.Add(ArtifactKind.Receipt);
        retained.Add(ArtifactKind.Metadata);

        return new(
            manifest?.RevisionId,
            manifest?.Committable ?? false,
            approved,
            receipt?.Status,
            manifestCounts,
            terminalCounts,
            frontier,
            retained);
    }

    private static BatchStatusSummary Summary(BatchRow batch, IngestOutcomeCounts counts, bool committable) => new(
        batch.BatchId,
        batch.Status,
        batch.AdapterId,
        batch.CreatedAt,
        batch.UpdatedAt,
        counts,
        NextOperations(batch.Status, committable));

    private static IReadOnlyList<string> NextOperations(BatchStatus status, bool committable) => status switch
    {
        BatchStatus.Previewed when committable => [IngestOperationIds.Inspect, IngestOperationIds.Approve, IngestOperationIds.Abandon],
        BatchStatus.Previewed => [IngestOperationIds.Inspect, IngestOperationIds.Abandon],
        BatchStatus.Approved => [IngestOperationIds.Inspect, IngestOperationIds.Commit, IngestOperationIds.Abandon],
        BatchStatus.Interrupted => [IngestOperationIds.Resume, IngestOperationIds.Abandon],
        BatchStatus.Completed or BatchStatus.Abandoned => [IngestOperationIds.Cleanup],
        _ => []
    };

    private static async Task<BatchRow?> ReadBatchAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT batch_id, status, adapter_identity, created_at, updated_at
            FROM ingest_batch
            WHERE batch_id = $batchId;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), (BatchStatus)reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    private static async Task<ManifestRow?> LatestManifestAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT manifest_revision_id, committable
            FROM manifest_revision
            WHERE batch_id = $batchId
            ORDER BY revision_number DESC
            LIMIT 1;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetInt32(1) == 1)
            : null;
    }

    private static async Task<ReceiptRow?> LatestReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT receipt_id, status
            FROM import_receipt
            WHERE batch_id = $batchId
            ORDER BY COALESCE(completed_at, '') DESC, rowid DESC
            LIMIT 1;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), (ImportReceiptStatus)reader.GetInt32(1))
            : null;
    }

    private static async Task<IngestOutcomeCounts> ManifestCountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN disposition = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN disposition = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN disposition = 2 THEN 1 ELSE 0 END),
                SUM(CASE WHEN disposition = 3 THEN 1 ELSE 0 END)
            FROM source_record_outcome
            WHERE manifest_revision_id = $id;
            """;
        return await CountsAsync(connection, transaction, sql, revisionId, cancellationToken);
    }

    private static async Task<IngestOutcomeCounts> TerminalCountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string receiptId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN outcome = 2 THEN 1 ELSE 0 END),
                SUM(CASE WHEN outcome = 3 THEN 1 ELSE 0 END),
                0,
                SUM(CASE WHEN outcome IN (4, 5) THEN 1 ELSE 0 END)
            FROM candidate_receipt
            WHERE receipt_id = $id;
            """;
        return await CountsAsync(connection, transaction, sql, receiptId, cancellationToken);
    }

    private static async Task<IngestOutcomeCounts> CountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }

    private static async Task<IReadOnlyList<string>> UnresolvedFrontierAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string receiptId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT candidate_id
            FROM candidate_receipt
            WHERE receipt_id = $id AND outcome IN (0, 1, 6)
            ORDER BY candidate_id;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        string generation,
        string createdAt,
        string expiresAt,
        IReadOnlyList<BatchStatusSummary> summaries,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            INSERT INTO status_snapshot (
                snapshot_id, contract_version, store_generation, created_at, expires_at, total_count)
            VALUES ($snapshotId, $contractVersion, $generation, $createdAt, $expiresAt, $totalCount);
            """;
        await using (var command = Command(connection, transaction, headerSql))
        {
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            command.Parameters.AddWithValue("$contractVersion", ContractVersion);
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$createdAt", createdAt);
            command.Parameters.AddWithValue("$expiresAt", expiresAt);
            command.Parameters.AddWithValue("$totalCount", summaries.Count);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string itemSql = """
            INSERT INTO status_snapshot_item (snapshot_id, ordinal, batch_status_summary_json)
            VALUES ($snapshotId, $ordinal, $summary);
            """;
        for (var ordinal = 0; ordinal < summaries.Count; ordinal++)
        {
            await using var command = Command(connection, transaction, itemSql);
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summaries[ordinal], IngestJsonContext.Default.BatchStatusSummary));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<SnapshotHeader?> ReadSnapshotHeaderAsync(
        SqliteConnection connection,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT contract_version, store_generation, expires_at, total_count
            FROM status_snapshot
            WHERE snapshot_id = $snapshotId;
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3))
            : null;
    }

    private static async Task<IReadOnlyList<BatchStatusSummary>> ReadSnapshotItemsAsync(
        SqliteConnection connection,
        string snapshotId,
        int nextOrdinal,
        int pageSize,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT batch_status_summary_json
            FROM status_snapshot_item
            WHERE snapshot_id = $snapshotId AND ordinal >= $nextOrdinal
            ORDER BY ordinal
            LIMIT $pageSize;
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$nextOrdinal", nextOrdinal);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<BatchStatusSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = JsonSerializer.Deserialize(reader.GetString(0), IngestJsonContext.Default.BatchStatusSummary);
            if (value is null) return [];
            values.Add(value);
        }
        return values;
    }

    private static async Task<string> StoreGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            "SELECT generation_id FROM ingest_store_metadata WHERE singleton_id = 1;");
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("The ingest store generation is unavailable.");
    }

    private static async Task DeleteExpiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "DELETE FROM status_snapshot WHERE expires_at <= $now;");
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string parameterName,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue(parameterName, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static bool TryUtc(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static readonly IngestOutcomeCounts EmptyCounts = new(0, 0, 0, 0);

    private sealed record BatchRow(string BatchId, BatchStatus Status, string AdapterId, string CreatedAt, string UpdatedAt);
    private sealed record ManifestRow(string RevisionId, bool Committable);
    private sealed record ReceiptRow(string ReceiptId, ImportReceiptStatus Status);
    private sealed record SnapshotHeader(string ContractVersion, string StoreGeneration, string ExpiresAt, int TotalCount);
    private sealed record ProjectionState(
        string? ManifestRevisionId,
        bool Committable,
        bool Approved,
        ImportReceiptStatus? ReceiptStatus,
        IngestOutcomeCounts ManifestCounts,
        IngestOutcomeCounts TerminalCounts,
        IReadOnlyList<string> UnresolvedFrontier,
        IReadOnlyList<ArtifactKind> RetainedArtifactKinds);
}

public sealed record StatusSnapshotPage(
    string SnapshotId,
    string StoreGeneration,
    string ExpiresAt,
    int PageSize,
    IReadOnlyList<BatchStatusSummary> Items,
    int? NextOrdinal);

public sealed record StatusSnapshotReadResult(StatusSnapshotPage? Page, string? ErrorCode)
{
    public static StatusSnapshotReadResult Success(StatusSnapshotPage page) => new(page, null);
    public static StatusSnapshotReadResult Failure(string errorCode) => new(null, errorCode);
}
