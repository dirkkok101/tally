using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ingest;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class RecoveryStateStore(IngestDatabase database, BatchErrorEventStore errorEvents)
{
    public sealed record BatchSnapshot(
        string BatchId,
        BatchStatus Status,
        string SourceFingerprint,
        string SelectedAccountId,
        string AdapterIdentity,
        string LedgerContractVersion,
        string ManifestSchemaVersion,
        string? ManifestRevisionId,
        string? ManifestDigest,
        bool Approved,
        int PriorLedgerEffectCount,
        IReadOnlyList<string> LedgerTransactionRefs);

    private async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenAsync(cancellationToken);
        await new IngestSchemaMigrator().ApplyAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<BatchSnapshot?> LoadBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string sql = """
            SELECT batch_id, status, source_fingerprint, selected_account_id, adapter_identity,
                   ledger_contract_version, manifest_schema_version
            FROM ingest_batch
            WHERE batch_id = $batchId;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = (BatchStatus)reader.GetInt32(1);
        var fingerprint = reader.GetString(2);
        var accountId = reader.GetString(3);
        var adapter = reader.GetString(4);
        var ledgerVersion = reader.GetString(5);
        var schemaVersion = reader.GetString(6);
        await reader.DisposeAsync();

        string? revisionId = null;
        string? digest = null;
        var approved = false;
        const string revisionSql = """
            SELECT m.manifest_revision_id, m.canonical_digest,
                   EXISTS(SELECT 1 FROM manifest_approval a WHERE a.manifest_revision_id = m.manifest_revision_id AND a.active = 1)
            FROM manifest_revision m
            WHERE m.batch_id = $batchId
            ORDER BY m.revision_number DESC
            LIMIT 1;
            """;
        await using (var revisionCommand = connection.CreateCommand())
        {
            revisionCommand.CommandText = revisionSql;
            revisionCommand.Parameters.AddWithValue("$batchId", batchId);
            await using var revisionReader = await revisionCommand.ExecuteReaderAsync(cancellationToken);
            if (await revisionReader.ReadAsync(cancellationToken))
            {
                revisionId = revisionReader.GetString(0);
                digest = revisionReader.GetString(1);
                approved = revisionReader.GetInt32(2) == 1;
            }
        }

        var ledgerRefs = new List<string>();
        const string refsSql = """
            SELECT DISTINCT ledger_transaction_id
            FROM candidate_receipt
            WHERE ledger_transaction_id IS NOT NULL
              AND receipt_id IN (SELECT receipt_id FROM import_receipt WHERE batch_id = $batchId);
            """;
        await using (var refsCommand = connection.CreateCommand())
        {
            refsCommand.CommandText = refsSql;
            refsCommand.Parameters.AddWithValue("$batchId", batchId);
            await using var refsReader = await refsCommand.ExecuteReaderAsync(cancellationToken);
            while (await refsReader.ReadAsync(cancellationToken))
            {
                if (!refsReader.IsDBNull(0))
                {
                    ledgerRefs.Add(refsReader.GetString(0));
                }
            }
        }

        return new BatchSnapshot(
            batchId,
            status,
            fingerprint,
            accountId,
            adapter,
            ledgerVersion,
            schemaVersion,
            revisionId,
            digest,
            approved,
            ledgerRefs.Count,
            ledgerRefs);
    }

    public async Task<bool> AbandonAsync(
        string batchId,
        string reason,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string loadSql = "SELECT status FROM ingest_batch WHERE batch_id = $batchId;";
            await using var load = connection.CreateCommand();
            load.Transaction = transaction;
            load.CommandText = loadSql;
            load.Parameters.AddWithValue("$batchId", batchId);
            var statusValue = await load.ExecuteScalarAsync(cancellationToken);
            if (statusValue is null or DBNull)
            {
                return false;
            }

            var status = (BatchStatus)Convert.ToInt32(statusValue, CultureInfo.InvariantCulture);
            // Terminal statuses cannot be abandoned. Committing is allowed when the OS lock is free
            // (orphan/crash frontier) — the handler acquires BatchCommitLock first.
            if (status is BatchStatus.Completed or BatchStatus.Abandoned or BatchStatus.Cleaned)
            {
                return false;
            }

            const string statusSql = """
                UPDATE ingest_batch
                SET status = $status, updated_at = $updatedAt
                WHERE batch_id = $batchId;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = statusSql;
                command.Parameters.AddWithValue("$status", (int)BatchStatus.Abandoned);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deactivateSql = """
                UPDATE manifest_approval
                SET active = 0
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deactivateSql;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // Compact sensitive frozen ledger request payloads while retaining candidate identity rows.
            // immutable_facts_json is left structurally loadable so status/inspect fail closed on approval
            // rather than throwing JSON required-property errors.
            const string compactSql = """
                UPDATE import_candidate
                SET frozen_ledger_request_json = '{}'
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = compactSql;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string tombstoneSql = """
                INSERT INTO import_receipt (receipt_id, batch_id, status, summary_json, completed_at)
                VALUES ($id, $batchId, $status, $summary, $completedAt)
                ON CONFLICT(receipt_id) DO NOTHING;
                """;
            // Upsert latest receipt to abandoned if present; otherwise insert tombstone.
            const string updateReceiptSql = """
                UPDATE import_receipt
                SET status = $status, summary_json = $summary, completed_at = $completedAt
                WHERE batch_id = $batchId;
                """;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"reason\":{JsonEscape(reason)},\"tombstone\":true,\"abandonedAt\":{JsonEscape(updatedAt)}}}");
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = updateReceiptSql;
                command.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Abandoned);
                command.Parameters.AddWithValue("$summary", summary);
                command.Parameters.AddWithValue("$completedAt", updatedAt);
                command.Parameters.AddWithValue("$batchId", batchId);
                var updated = await command.ExecuteNonQueryAsync(cancellationToken);
                if (updated == 0)
                {
                    command.Parameters.Clear();
                    command.CommandText = tombstoneSql;
                    command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$batchId", batchId);
                    command.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Abandoned);
                    command.Parameters.AddWithValue("$summary", summary);
                    command.Parameters.AddWithValue("$completedAt", updatedAt);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<(bool Ok, string? Error, IReadOnlyList<ArtifactKind> Removed)> CleanupAsync(
        string batchId,
        BatchStatus expectedTerminalStatus,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string loadSql = "SELECT status FROM ingest_batch WHERE batch_id = $batchId;";
            await using var load = connection.CreateCommand();
            load.Transaction = transaction;
            load.CommandText = loadSql;
            load.Parameters.AddWithValue("$batchId", batchId);
            var statusValue = await load.ExecuteScalarAsync(cancellationToken);
            if (statusValue is null or DBNull)
            {
                return (false, "not_found", Array.Empty<ArtifactKind>());
            }

            var status = (BatchStatus)Convert.ToInt32(statusValue, CultureInfo.InvariantCulture);
            if (status is not (BatchStatus.Completed or BatchStatus.Abandoned))
            {
                return (false, "retained_for_recovery", Array.Empty<ArtifactKind>());
            }

            if (status != expectedTerminalStatus)
            {
                return (false, "retained_for_recovery", Array.Empty<ArtifactKind>());
            }

            var removed = new List<ArtifactKind>();

            // Remove candidate payloads and outcomes for this batch's revisions.
            const string deleteCandidates = """
                DELETE FROM import_candidate
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteCandidates;
                command.Parameters.AddWithValue("$batchId", batchId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    removed.Add(ArtifactKind.Candidates);
                }
            }

            const string deleteOutcomes = """
                DELETE FROM source_record_outcome
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteOutcomes;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deleteControls = """
                DELETE FROM reconciliation_control
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteControls;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deleteApprovals = """
                DELETE FROM manifest_approval
                WHERE manifest_revision_id IN (
                    SELECT manifest_revision_id FROM manifest_revision WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteApprovals;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deleteRevisions = """
                DELETE FROM manifest_revision WHERE batch_id = $batchId;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteRevisions;
                command.Parameters.AddWithValue("$batchId", batchId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    removed.Add(ArtifactKind.Manifest);
                }
            }

            const string deleteReceiptItems = """
                DELETE FROM candidate_receipt
                WHERE receipt_id IN (SELECT receipt_id FROM import_receipt WHERE batch_id = $batchId);
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteReceiptItems;
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deleteReceipts = "DELETE FROM import_receipt WHERE batch_id = $batchId;";
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = deleteReceipts;
                command.Parameters.AddWithValue("$batchId", batchId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    removed.Add(ArtifactKind.Receipt);
                }
            }

            const string markCleaned = """
                UPDATE ingest_batch
                SET status = $status, updated_at = $updatedAt
                WHERE batch_id = $batchId;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = markCleaned;
                command.Parameters.AddWithValue("$status", (int)BatchStatus.Cleaned);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.Parameters.AddWithValue("$batchId", batchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
                removed.Add(ArtifactKind.Metadata);
            }

            await transaction.CommitAsync(cancellationToken);
            return (true, null, removed.Distinct().ToArray());
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string JsonEscape(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public async Task AppendErrorAsync(
        string batchId,
        string code,
        string safeMessage,
        string durableState,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var error = new IngestError(
                code,
                IngestErrorCategory.Validation,
                safeMessage,
                batchId,
                null,
                MutationPossibility.None,
                durableState,
                IngestRetryAction.None,
                null);
            await errorEvents.AppendAsync(
                connection,
                transaction,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                error,
                recordedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
