using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Commit;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class CommitStateStore(IngestDatabase database, BatchErrorEventStore errorEvents)
{
    public sealed record CandidateWorkItem(
        string CandidateId,
        string SourceRecordId,
        int RecordOrder,
        CandidateReceiptState CommitState,
        FrozenLedgerRecordRequest FrozenRequest,
        string IdempotencyKey,
        string? PriorCanonicalRef,
        SourceRecordDisposition Disposition,
        string? LedgerTransactionId,
        string? ErrorCode,
        int AttemptNumber);

    public sealed record ReceiptHeader(
        string ReceiptId,
        ImportReceiptStatus Status,
        string CreatedAt,
        string UpdatedAt,
        string? CompletedAt);

    public sealed record ResumeTarget(
        string BatchId,
        string? ManifestRevisionId,
        string? ManifestDigest,
        bool Approved,
        BatchStatus BatchStatus);

    private async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenAsync(cancellationToken);
        await new IngestSchemaMigrator().ApplyAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<ResumeTarget?> ResolveResumeTargetAsync(string batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string batchSql = """
            SELECT status
            FROM ingest_batch
            WHERE batch_id = $batchId;
            """;
        await using var batchCommand = connection.CreateCommand();
        batchCommand.CommandText = batchSql;
        batchCommand.Parameters.AddWithValue("$batchId", batchId);
        var statusValue = await batchCommand.ExecuteScalarAsync(cancellationToken);
        if (statusValue is null or DBNull)
        {
            return null;
        }

        var status = (BatchStatus)Convert.ToInt32(statusValue, CultureInfo.InvariantCulture);

        // Prefer the actively approved revision; fall back to the latest revision for completed receipts.
        const string approvalSql = """
            SELECT m.manifest_revision_id, m.canonical_digest
            FROM manifest_approval a
            JOIN manifest_revision m ON m.manifest_revision_id = a.manifest_revision_id
            WHERE m.batch_id = $batchId AND a.active = 1
            ORDER BY a.approved_at DESC
            LIMIT 1;
            """;
        await using var approvalCommand = connection.CreateCommand();
        approvalCommand.CommandText = approvalSql;
        approvalCommand.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await approvalCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new ResumeTarget(batchId, reader.GetString(0), reader.GetString(1), true, status);
        }

        const string latestSql = """
            SELECT manifest_revision_id, canonical_digest
            FROM manifest_revision
            WHERE batch_id = $batchId
            ORDER BY revision_number DESC
            LIMIT 1;
            """;
        await using var latestCommand = connection.CreateCommand();
        latestCommand.CommandText = latestSql;
        latestCommand.Parameters.AddWithValue("$batchId", batchId);
        await using var latestReader = await latestCommand.ExecuteReaderAsync(cancellationToken);
        if (!await latestReader.ReadAsync(cancellationToken))
        {
            return new ResumeTarget(batchId, null, null, false, status);
        }

        return new ResumeTarget(batchId, latestReader.GetString(0), latestReader.GetString(1), false, status);
    }

    public async Task<IReadOnlyList<CandidateWorkItem>> LoadWorkItemsAsync(
        string batchId,
        string manifestRevisionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string sql = """
            SELECT
                c.candidate_id,
                c.source_record_id,
                COALESCE(o.record_order, 0) AS record_order,
                c.commit_state,
                c.frozen_ledger_request_json,
                c.idempotency_key,
                o.prior_canonical_ref,
                COALESCE(o.disposition, 0) AS disposition,
                r.ledger_transaction_id,
                r.error_code,
                COALESCE(r.attempt_count, 0) AS attempt_count
            FROM import_candidate c
            LEFT JOIN source_record_outcome o
                ON o.manifest_revision_id = c.manifest_revision_id
               AND o.candidate_id = c.candidate_id
            LEFT JOIN candidate_receipt r
                ON r.candidate_id = c.candidate_id
               AND r.receipt_id = (
                    SELECT receipt_id
                    FROM import_receipt
                    WHERE batch_id = $batchId
                    ORDER BY rowid DESC
                    LIMIT 1)
            WHERE c.manifest_revision_id = $revisionId
            ORDER BY COALESCE(o.record_order, 0), c.candidate_id;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$revisionId", manifestRevisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CandidateWorkItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var frozenJson = reader.GetString(4);
            var frozen = JsonSerializer.Deserialize(frozenJson, IngestJsonContext.Default.FrozenLedgerRecordRequest)
                ?? throw new InvalidOperationException("Frozen ledger request could not be deserialized.");
            items.Add(new CandidateWorkItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                CandidateCommitStates.FromStorage(reader.GetInt32(3)),
                frozen,
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                (SourceRecordDisposition)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt32(10)));
        }

        return items;
    }

    public async Task<ReceiptHeader> EnsureReceiptAsync(
        string batchId,
        string manifestRevisionId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string existingSql = """
                SELECT receipt_id, status, summary_json, completed_at, created_at, updated_at
                FROM import_receipt
                WHERE batch_id = $batchId
                ORDER BY rowid DESC
                LIMIT 1;
                """;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = existingSql;
                existing.Parameters.AddWithValue("$batchId", batchId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var receiptId = reader.GetString(0);
                    var status = (ImportReceiptStatus)reader.GetInt32(1);
                    var completedAt = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var createdAt = reader.IsDBNull(4) ? now : reader.GetString(4);
                    var updatedAt = reader.IsDBNull(5) ? now : reader.GetString(5);
                    await reader.DisposeAsync();

                    if (status is ImportReceiptStatus.Completed or ImportReceiptStatus.Abandoned)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return new ReceiptHeader(receiptId, status, createdAt, updatedAt, completedAt);
                    }

                    // Promote is a pure status flip — never wipe summary_json or re-stamp created_at.
                    const string promoteSql = """
                        UPDATE import_receipt
                        SET status = $status, updated_at = $updatedAt
                        WHERE receipt_id = $id;
                        """;
                    await using var promote = connection.CreateCommand();
                    promote.Transaction = transaction;
                    promote.CommandText = promoteSql;
                    promote.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Committing);
                    promote.Parameters.AddWithValue("$updatedAt", now);
                    promote.Parameters.AddWithValue("$id", receiptId);
                    await promote.ExecuteNonQueryAsync(cancellationToken);

                    await SetBatchStatusAsync(connection, transaction, batchId, BatchStatus.Committing, now, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new ReceiptHeader(receiptId, ImportReceiptStatus.Committing, createdAt, now, null);
                }
            }

            var newId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            const string insertSql = """
                INSERT INTO import_receipt (receipt_id, batch_id, status, summary_json, completed_at, created_at, updated_at)
                VALUES ($id, $batchId, $status, $summary, NULL, $createdAt, $updatedAt);
                """;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = insertSql;
                insert.Parameters.AddWithValue("$id", newId);
                insert.Parameters.AddWithValue("$batchId", batchId);
                insert.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Committing);
                insert.Parameters.AddWithValue("$summary", "{}");
                insert.Parameters.AddWithValue("$createdAt", now);
                insert.Parameters.AddWithValue("$updatedAt", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await SetBatchStatusAsync(connection, transaction, batchId, BatchStatus.Committing, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReceiptHeader(newId, ImportReceiptStatus.Committing, now, now, null);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkAttemptingAsync(
        string receiptId,
        string candidateId,
        string attemptedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string candidateSql = """
                UPDATE import_candidate
                SET commit_state = $state
                WHERE candidate_id = $candidateId;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = candidateSql;
                command.Parameters.AddWithValue("$state", CandidateCommitStates.ToStorage(CandidateReceiptState.Attempting));
                command.Parameters.AddWithValue("$candidateId", candidateId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string upsertSql = """
                INSERT INTO candidate_receipt (
                    receipt_id, candidate_id, outcome, ledger_transaction_id, error_code, attempted_at, terminal_at, attempt_count)
                VALUES ($receiptId, $candidateId, $outcome, NULL, NULL, $attemptedAt, NULL, 1)
                ON CONFLICT(receipt_id, candidate_id) DO UPDATE SET
                    outcome = excluded.outcome,
                    ledger_transaction_id = NULL,
                    error_code = NULL,
                    attempted_at = excluded.attempted_at,
                    terminal_at = NULL,
                    attempt_count = candidate_receipt.attempt_count + 1;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = upsertSql;
                command.Parameters.AddWithValue("$receiptId", receiptId);
                command.Parameters.AddWithValue("$candidateId", candidateId);
                command.Parameters.AddWithValue("$outcome", CandidateCommitStates.ToStorage(CandidateReceiptState.Attempting));
                command.Parameters.AddWithValue("$attemptedAt", attemptedAt);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkTerminalAsync(
        string receiptId,
        string candidateId,
        CandidateReceiptState state,
        string? ledgerTransactionId,
        string? errorCode,
        string terminalAt,
        CancellationToken cancellationToken)
    {
        if (!CandidateCommitStates.IsTerminal(state) && state != CandidateReceiptState.Unresolved)
        {
            throw new ArgumentException("State must be terminal or unresolved.", nameof(state));
        }

        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string candidateSql = """
                UPDATE import_candidate
                SET commit_state = $state
                WHERE candidate_id = $candidateId;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = candidateSql;
                command.Parameters.AddWithValue("$state", CandidateCommitStates.ToStorage(state));
                command.Parameters.AddWithValue("$candidateId", candidateId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var terminalColumn = CandidateCommitStates.IsTerminal(state) ? terminalAt : null;
            const string upsertSql = """
                INSERT INTO candidate_receipt (
                    receipt_id, candidate_id, outcome, ledger_transaction_id, error_code, attempted_at, terminal_at)
                VALUES ($receiptId, $candidateId, $outcome, $ledgerId, $errorCode, $attemptedAt, $terminalAt)
                ON CONFLICT(receipt_id, candidate_id) DO UPDATE SET
                    outcome = excluded.outcome,
                    ledger_transaction_id = excluded.ledger_transaction_id,
                    error_code = excluded.error_code,
                    terminal_at = excluded.terminal_at;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = upsertSql;
                command.Parameters.AddWithValue("$receiptId", receiptId);
                command.Parameters.AddWithValue("$candidateId", candidateId);
                command.Parameters.AddWithValue("$outcome", CandidateCommitStates.ToStorage(state));
                command.Parameters.AddWithValue("$ledgerId", (object?)ledgerTransactionId ?? DBNull.Value);
                command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$attemptedAt", terminalAt);
                command.Parameters.AddWithValue("$terminalAt", (object?)terminalColumn ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task CompleteReceiptAsync(
        string receiptId,
        string batchId,
        ImportReceipt receipt,
        string completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE import_receipt
                SET status = $status, summary_json = $summary, completed_at = $completedAt, updated_at = $updatedAt
                WHERE receipt_id = $id;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Completed);
                command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(receipt, IngestJsonContext.Default.ImportReceipt));
                command.Parameters.AddWithValue("$completedAt", completedAt);
                command.Parameters.AddWithValue("$updatedAt", completedAt);
                command.Parameters.AddWithValue("$id", receiptId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SetBatchStatusAsync(connection, transaction, batchId, BatchStatus.Completed, completedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task InterruptReceiptAsync(
        string receiptId,
        string batchId,
        ImportReceipt receipt,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE import_receipt
                SET status = $status, summary_json = $summary, updated_at = $updatedAt
                WHERE receipt_id = $id;
                """;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Interrupted);
                command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(receipt, IngestJsonContext.Default.ImportReceipt));
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.Parameters.AddWithValue("$id", receiptId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SetBatchStatusAsync(connection, transaction, batchId, BatchStatus.Interrupted, updatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task AppendStopErrorAsync(
        string batchId,
        string? candidateId,
        string code,
        IngestErrorCategory category,
        string safeMessage,
        string durableState,
        IngestRetryAction retryAction,
        MutationPossibility mutationPossibility,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var error = new IngestError(
                code,
                category,
                safeMessage,
                batchId,
                candidateId,
                mutationPossibility,
                durableState,
                retryAction,
                candidateId is null ? null : "candidate");
            var eventId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            await errorEvents.AppendAsync(connection, transaction, eventId, error, recordedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ImportReceipt> BuildReceiptAsync(
        string receiptId,
        string batchId,
        string manifestRevisionId,
        ImportReceiptStatus status,
        string createdAt,
        string updatedAt,
        string? completedAt,
        CancellationToken cancellationToken)
    {
        var items = await LoadWorkItemsAsync(batchId, manifestRevisionId, cancellationToken);
        await using var connection = await OpenMigratedAsync(cancellationToken);

        const string receiptSql = """
            SELECT candidate_id, outcome, ledger_transaction_id, error_code, attempted_at, terminal_at
            FROM candidate_receipt
            WHERE receipt_id = $receiptId;
            """;
        var receiptRows = new Dictionary<string, (CandidateReceiptState State, string? LedgerId, string? Error, string? Attempted, string? Terminal)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = receiptSql;
            command.Parameters.AddWithValue("$receiptId", receiptId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                receiptRows[reader.GetString(0)] = (
                    CandidateCommitStates.FromStorage(reader.GetInt32(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        var outcomes = new List<CandidateReceipt>();
        var pending = 0;
        var attempting = 0;
        var accepted = 0;
        var exactDuplicates = 0;
        var conflicted = 0;
        var rejected = 0;
        var unresolved = 0;
        var unresolvedIds = new List<string>();

        foreach (var item in items)
        {
            var hasReceipt = receiptRows.TryGetValue(item.CandidateId, out var row);
            var state = hasReceipt ? row.State : item.CommitState;

            switch (state)
            {
                case CandidateReceiptState.Pending: pending++; unresolvedIds.Add(item.CandidateId); break;
                case CandidateReceiptState.Attempting: attempting++; unresolvedIds.Add(item.CandidateId); break;
                case CandidateReceiptState.Accepted: accepted++; break;
                case CandidateReceiptState.ExactDuplicate: exactDuplicates++; break;
                case CandidateReceiptState.Conflicted: conflicted++; break;
                case CandidateReceiptState.Rejected: rejected++; break;
                case CandidateReceiptState.Unresolved: unresolved++; unresolvedIds.Add(item.CandidateId); break;
            }

            var retry = state switch
            {
                CandidateReceiptState.Conflicted => IngestRetryAction.Abandon,
                CandidateReceiptState.Rejected => IngestRetryAction.CorrectSource,
                CandidateReceiptState.Unresolved or CandidateReceiptState.Attempting or CandidateReceiptState.Pending
                    => IngestRetryAction.Resume,
                _ => IngestRetryAction.None
            };

            outcomes.Add(new CandidateReceipt(
                item.CandidateId,
                state,
                item.AttemptNumber,
                item.FrozenRequest.OperationId,
                item.FrozenRequest.LedgerContractVersion,
                item.IdempotencyKey,
                CandidateCommitStates.IsReferenceBearing(state)
                    ? (hasReceipt ? row.LedgerId : null) ?? item.LedgerTransactionId
                    : null,
                hasReceipt ? row.Error : item.ErrorCode,
                retry,
                hasReceipt ? row.Attempted : null,
                CandidateCommitStates.IsTerminal(state) && hasReceipt ? row.Terminal : null));
        }

        return new ImportReceipt(
            receiptId,
            batchId,
            manifestRevisionId,
            status,
            new ImportReceiptCounts(pending, attempting, accepted, exactDuplicates, conflicted, rejected, unresolved),
            unresolvedIds,
            outcomes,
            createdAt,
            updatedAt,
            completedAt);
    }

    private static async Task SetBatchStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        BatchStatus status,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE ingest_batch
            SET status = $status, updated_at = $updatedAt
            WHERE batch_id = $batchId;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.Parameters.AddWithValue("$batchId", batchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
