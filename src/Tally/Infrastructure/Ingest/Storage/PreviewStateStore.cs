using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Identity;
using Tally.Domain.Ingest.Overlap;
using Tally.Features.Ingest.Preview;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class PreviewStateStore(IngestDatabase database, BatchErrorEventStore errorEvents)
{
    private async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenAsync(cancellationToken);
        await new IngestSchemaMigrator().ApplyAsync(connection, cancellationToken);
        return connection;
    }

    public sealed record StoredPreview(
        string BatchId,
        string ManifestRevisionId,
        BatchStatus Status,
        IngestOutcomeCounts Counts,
        ReconciliationSummary Reconciliation,
        string AdapterVariantId,
        string AdapterVersion,
        bool Committable,
        string? ExactReplayOf);

    public async Task<StoredPreview?> FindExactReplayAsync(
        ExactReplayKey key,
        CancellationToken cancellationToken)
    {
        var batchId = IngestIdentity.BatchId(new BatchIdentityInput(
            key.SourceFingerprint,
            key.SelectedAccountId,
            key.AdapterVersion,
            key.LedgerContractVersion));

        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string sql = """
            SELECT b.batch_id, b.status, b.adapter_identity, m.manifest_revision_id, m.committable
            FROM ingest_batch b
            JOIN manifest_revision m ON m.batch_id = b.batch_id
            WHERE b.batch_id = $batchId
              AND b.source_fingerprint = $fingerprint
              AND b.selected_account_id = $accountId
              AND b.adapter_identity = $adapter
              AND b.ledger_contract_version = $ledger
              AND b.status IN (0, 1, 2, 3, 4)
            ORDER BY m.revision_number DESC
            LIMIT 1;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$fingerprint", key.SourceFingerprint);
        command.Parameters.AddWithValue("$accountId", key.SelectedAccountId);
        command.Parameters.AddWithValue("$adapter", key.AdapterVersion);
        command.Parameters.AddWithValue("$ledger", key.LedgerContractVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var revisionId = reader.GetString(3);
        var counts = await CountOutcomesAsync(connection, revisionId, cancellationToken);
        var committable = reader.GetInt32(4) == 1;
        return new StoredPreview(
            reader.GetString(0),
            revisionId,
            (BatchStatus)reader.GetInt32(1),
            counts,
            new ReconciliationSummary(committable, []),
            reader.GetString(2),
            key.AdapterVersion,
            committable,
            reader.GetString(0));
    }

    public async Task<IReadOnlyList<PreviewWindow>> ListWindowsForAccountAsync(
        string selectedAccountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string sql = """
            SELECT b.source_fingerprint, b.selected_account_id, b.adapter_identity, b.ledger_contract_version,
                   m.manifest_revision_id, b.period_start, b.period_end
            FROM ingest_batch b
            JOIN manifest_revision m ON m.batch_id = b.batch_id
            WHERE b.selected_account_id = $accountId
              AND b.status IN (0, 1, 2, 3, 4)
              AND b.period_start IS NOT NULL
              AND b.period_end IS NOT NULL
            ORDER BY m.revision_number DESC;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$accountId", selectedAccountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var windows = new List<PreviewWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var revisionId = reader.GetString(4);
            if (!seen.Add(revisionId))
            {
                continue;
            }

            if (!DateOnly.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateOnly.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                continue;
            }

            windows.Add(new PreviewWindow(
                new ExactReplayKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)),
                revisionId,
                start,
                end));
        }

        return windows;
    }

    public async Task<StoredPreview> PersistPreviewAsync(
        string sourceFingerprint,
        string selectedAccountId,
        string ledgerContractVersion,
        PreviewManifestMapper.MappedPreview mapped,
        StatementPeriod period,
        string createdAt,
        CancellationToken cancellationToken)
    {
        var batchId = IngestIdentity.BatchId(new BatchIdentityInput(
            sourceFingerprint,
            selectedAccountId,
            mapped.AdapterVersion,
            ledgerContractVersion));

        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureBatchRowAsync(
                connection,
                transaction,
                batchId,
                sourceFingerprint,
                selectedAccountId,
                mapped.AdapterVersion,
                ledgerContractVersion,
                "1",
                period.StartDate,
                period.EndDate,
                BatchStatus.Previewed,
                createdAt,
                cancellationToken);

            await InsertManifestAsync(
                connection,
                transaction,
                batchId,
                mapped,
                createdAt,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new StoredPreview(
                batchId,
                mapped.Manifest.ManifestRevisionId,
                BatchStatus.Previewed,
                mapped.Counts,
                mapped.Reconciliation,
                mapped.AdapterVariantId,
                mapped.AdapterVersion,
                mapped.Committable,
                null);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task PersistFailedBatchAsync(
        string batchId,
        string? sourceFingerprint,
        string? selectedAccountId,
        string? adapterVersion,
        string? ledgerContractVersion,
        IngestError error,
        string createdAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceFingerprint) &&
                !string.IsNullOrWhiteSpace(selectedAccountId) &&
                !string.IsNullOrWhiteSpace(adapterVersion) &&
                !string.IsNullOrWhiteSpace(ledgerContractVersion))
            {
                await EnsureBatchRowAsync(
                    connection,
                    transaction,
                    batchId,
                    sourceFingerprint,
                    selectedAccountId,
                    adapterVersion,
                    ledgerContractVersion,
                    "1",
                    null,
                    null,
                    BatchStatus.Previewed,
                    createdAt,
                    cancellationToken);
            }

            var durableError = error with { BatchId = batchId };
            await errorEvents.AppendAsync(
                connection,
                transaction,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                durableError,
                createdAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task EnsureBatchRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        string sourceFingerprint,
        string selectedAccountId,
        string adapterVersion,
        string ledgerContractVersion,
        string manifestSchemaVersion,
        string? periodStart,
        string? periodEnd,
        BatchStatus status,
        string createdAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO ingest_batch (
                batch_id, source_fingerprint, selected_account_id, adapter_identity,
                ledger_contract_version, manifest_schema_version, period_start, period_end,
                status, created_at, updated_at)
            VALUES (
                $batchId, $fingerprint, $accountId, $adapter, $ledger, $schema,
                $periodStart, $periodEnd, $status, $createdAt, $updatedAt)
            ON CONFLICT(batch_id) DO UPDATE SET
                updated_at = excluded.updated_at,
                status = excluded.status,
                period_start = COALESCE(excluded.period_start, ingest_batch.period_start),
                period_end = COALESCE(excluded.period_end, ingest_batch.period_end);
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$accountId", selectedAccountId);
        command.Parameters.AddWithValue("$adapter", adapterVersion);
        command.Parameters.AddWithValue("$ledger", ledgerContractVersion);
        command.Parameters.AddWithValue("$schema", manifestSchemaVersion);
        command.Parameters.AddWithValue("$periodStart", (object?)periodStart ?? DBNull.Value);
        command.Parameters.AddWithValue("$periodEnd", (object?)periodEnd ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$updatedAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertManifestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        PreviewManifestMapper.MappedPreview mapped,
        string createdAt,
        CancellationToken cancellationToken)
    {
        var revisionNumber = await NextRevisionAsync(connection, transaction, batchId, cancellationToken);
        const string revisionSql = """
            INSERT INTO manifest_revision (
                manifest_revision_id, batch_id, revision_number, canonical_digest, committable, created_at)
            VALUES ($id, $batchId, $revision, $digest, $committable, $createdAt);
            """;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = revisionSql;
            command.Parameters.AddWithValue("$id", mapped.Manifest.ManifestRevisionId);
            command.Parameters.AddWithValue("$batchId", batchId);
            command.Parameters.AddWithValue("$revision", revisionNumber);
            command.Parameters.AddWithValue("$digest", mapped.Manifest.CanonicalDigest);
            command.Parameters.AddWithValue("$committable", mapped.Committable ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", createdAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var outcome in mapped.Outcomes)
        {
            const string outcomeSql = """
                INSERT INTO source_record_outcome (
                    manifest_revision_id, source_record_id, record_order, disposition, reason_code, candidate_id, prior_canonical_ref)
                VALUES ($revisionId, $recordId, $order, $disposition, $reason, $candidateId, $prior);
                """;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = outcomeSql;
            command.Parameters.AddWithValue("$revisionId", mapped.Manifest.ManifestRevisionId);
            command.Parameters.AddWithValue("$recordId", outcome.SourceRecordId);
            command.Parameters.AddWithValue("$order", outcome.Order);
            command.Parameters.AddWithValue("$disposition", (int)outcome.Disposition);
            command.Parameters.AddWithValue("$reason", outcome.ReasonCode);
            command.Parameters.AddWithValue("$candidateId", (object?)outcome.CandidateId ?? DBNull.Value);
            command.Parameters.AddWithValue("$prior", (object?)outcome.PriorCanonicalRef ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var candidate in mapped.Candidates)
        {
            const string candidateSql = """
                INSERT INTO import_candidate (
                    candidate_id, manifest_revision_id, source_record_id, immutable_facts_json,
                    frozen_ledger_request_json, idempotency_key, commit_state)
                VALUES ($id, $revisionId, $recordId, $facts, $request, $idempotency, 0);
                """;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = candidateSql;
            command.Parameters.AddWithValue("$id", candidate.CandidateId);
            command.Parameters.AddWithValue("$revisionId", mapped.Manifest.ManifestRevisionId);
            command.Parameters.AddWithValue("$recordId", candidate.SourceRecordId);
            command.Parameters.AddWithValue("$facts", JsonSerializer.Serialize(candidate, IngestJsonContext.Default.ImportCandidate));
            command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(candidate.FrozenLedgerRequest, IngestJsonContext.Default.FrozenLedgerRecordRequest));
            command.Parameters.AddWithValue("$idempotency", candidate.LedgerIdempotencyKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var controlOrder = 0;
        foreach (var control in mapped.Reconciliation.Controls)
        {
            const string controlSql = """
                INSERT INTO reconciliation_control (
                    manifest_revision_id, control_order, kind, availability, evidence_json)
                VALUES ($revisionId, $order, $kind, $availability, $evidence);
                """;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = controlSql;
            command.Parameters.AddWithValue("$revisionId", mapped.Manifest.ManifestRevisionId);
            command.Parameters.AddWithValue("$order", controlOrder++);
            command.Parameters.AddWithValue("$kind", 0);
            command.Parameters.AddWithValue("$availability", control.Satisfied ? 0 : 1);
            command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(control, IngestJsonContext.Default.ReconciliationControl));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> NextRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(MAX(revision_number), 0) + 1
            FROM manifest_revision
            WHERE batch_id = $batchId;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IngestOutcomeCounts> CountOutcomesAsync(
        SqliteConnection connection,
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
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }
}
