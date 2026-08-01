using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Domain.Classify.Apply;
using Tally.Features.Classify.Contract;

namespace Tally.Infrastructure.Classify.Storage.Apply;

/// <summary>
/// Durable apply_run / apply_item intent and terminal results
/// (DM-CLASSIFY-APPLY-RUN / TASK-CLASSIFY-RULEBOOK-APPLY-RUN-SAGA).
/// Frozen request fields are immutable after insert; only item_state + result columns advance.
/// Never mutates Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationApplyRunStore
{
    public async Task InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyApplyRunRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.RequestFingerprint.Length != 64)
        {
            throw new InvalidOperationException("Apply run request fingerprint must be 64 hex chars.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO apply_run (
                apply_id, preview_id, request_fingerprint, lifecycle_state, unresolved_frontier,
                actor, started_at, completed_at
            ) VALUES (
                $apply_id, $preview_id, $request_fingerprint, $lifecycle_state, $unresolved_frontier,
                $actor, $started_at, $completed_at
            );
            """;
        command.Parameters.AddWithValue("$apply_id", row.ApplyId);
        command.Parameters.AddWithValue("$preview_id", row.PreviewId);
        command.Parameters.AddWithValue("$request_fingerprint", row.RequestFingerprint);
        command.Parameters.AddWithValue("$lifecycle_state", row.LifecycleState);
        command.Parameters.AddWithValue("$unresolved_frontier", row.UnresolvedFrontier);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$started_at", row.StartedAt);
        command.Parameters.AddWithValue("$completed_at", (object?)row.CompletedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ClassifyApplyItemRow> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordered[i].Ordinal != i)
            {
                throw new InvalidOperationException(
                    "Apply item ordinals must be contiguous from zero.");
            }

            await InsertItemAsync(connection, transaction, ordered[i], cancellationToken);
        }
    }

    public async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyApplyItemRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.LedgerRequestFingerprint.Length != 64)
        {
            throw new InvalidOperationException("Item ledger request fingerprint must be 64 hex chars.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO apply_item (
                apply_id, ordinal, transaction_id, ledger_operation_id, category_id,
                expected_active_allocation_id, expected_transaction_revision, expected_relationship_revision,
                expected_allocation_revision, correction_reason, ledger_request_fingerprint,
                ledger_idempotency_key, item_state, ledger_result_fingerprint, ledger_allocation_id,
                prior_ledger_allocation_id, safe_error_code
            ) VALUES (
                $apply_id, $ordinal, $transaction_id, $ledger_operation_id, $category_id,
                $expected_active_allocation_id, $expected_transaction_revision, $expected_relationship_revision,
                $expected_allocation_revision, $correction_reason, $ledger_request_fingerprint,
                $ledger_idempotency_key, $item_state, $ledger_result_fingerprint, $ledger_allocation_id,
                $prior_ledger_allocation_id, $safe_error_code
            );
            """;
        command.Parameters.AddWithValue("$apply_id", row.ApplyId);
        command.Parameters.AddWithValue("$ordinal", row.Ordinal);
        command.Parameters.AddWithValue("$transaction_id", row.TransactionId);
        command.Parameters.AddWithValue("$ledger_operation_id", row.LedgerOperationId);
        command.Parameters.AddWithValue("$category_id", row.CategoryId);
        command.Parameters.AddWithValue("$expected_active_allocation_id", (object?)row.ExpectedActiveAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expected_transaction_revision", row.ExpectedTransactionRevision);
        command.Parameters.AddWithValue("$expected_relationship_revision", row.ExpectedRelationshipRevision);
        command.Parameters.AddWithValue("$expected_allocation_revision", row.ExpectedAllocationRevision);
        command.Parameters.AddWithValue("$correction_reason", (object?)row.CorrectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$ledger_request_fingerprint", row.LedgerRequestFingerprint);
        command.Parameters.AddWithValue("$ledger_idempotency_key", row.LedgerIdempotencyKey);
        command.Parameters.AddWithValue("$item_state", row.ItemState);
        command.Parameters.AddWithValue("$ledger_result_fingerprint", (object?)row.LedgerResultFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$ledger_allocation_id", (object?)row.LedgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prior_ledger_allocation_id", (object?)row.PriorLedgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$safe_error_code", (object?)row.SafeErrorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Advance a planned/unresolved item to a terminal (or unresolved) result.
    /// Frozen request columns are not updated (schema-enforced).
    /// </summary>
    public async Task<bool> TryCompleteItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string applyId,
        int ordinal,
        string expectedPriorState,
        string nextState,
        string? ledgerResultFingerprint,
        string? ledgerAllocationId,
        string? priorLedgerAllocationId,
        string? safeErrorCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPriorState);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextState);

        if (!ApplyReplayPolicy.IsValidItemStateTransition(expectedPriorState, nextState))
        {
            throw new InvalidOperationException(
                $"Invalid apply_item transition {expectedPriorState} → {nextState}.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE apply_item
            SET item_state = $next,
                ledger_result_fingerprint = $result_fp,
                ledger_allocation_id = $alloc,
                prior_ledger_allocation_id = $prior,
                safe_error_code = $error
            WHERE apply_id = $apply_id
              AND ordinal = $ordinal
              AND item_state = $expected;
            """;
        command.Parameters.AddWithValue("$next", nextState);
        command.Parameters.AddWithValue("$result_fp", (object?)ledgerResultFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$alloc", (object?)ledgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prior", (object?)priorLedgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)safeErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$apply_id", applyId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$expected", expectedPriorState);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 1;
    }

    public async Task UpdateRunFrontierAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string applyId,
        int unresolvedFrontier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE apply_run
            SET unresolved_frontier = $frontier
            WHERE apply_id = $id
              AND lifecycle_state = 'running';
            """;
        command.Parameters.AddWithValue("$frontier", unresolvedFrontier);
        command.Parameters.AddWithValue("$id", applyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Complete a running apply_run. Must set completed_at and terminal lifecycle together
    /// (schema transition guards).
    /// </summary>
    public async Task<bool> TryCompleteRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string applyId,
        string lifecycleState,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleState);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);

        if (lifecycleState is not (
            ApplyReplayPolicy.RunLifecycleCompleted
            or ApplyReplayPolicy.RunLifecycleFailed
            or ApplyReplayPolicy.RunLifecycleAbandoned))
        {
            throw new InvalidOperationException("Apply run terminal lifecycle must be completed/failed/abandoned.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE apply_run
            SET lifecycle_state = $state,
                completed_at = $completed,
                unresolved_frontier = 0
            WHERE apply_id = $id
              AND lifecycle_state = 'running'
              AND completed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$state", lifecycleState);
        command.Parameters.AddWithValue("$completed", completedAtUtc);
        command.Parameters.AddWithValue("$id", applyId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<ClassifyApplyRunRow?> GetRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string applyId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT apply_id, preview_id, request_fingerprint, lifecycle_state, unresolved_frontier,
                   actor, started_at, completed_at
            FROM apply_run
            WHERE apply_id = $id;
            """;
        command.Parameters.AddWithValue("$id", applyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassifyApplyRunRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task<IReadOnlyList<ClassifyApplyItemRow>> ListItemsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string applyId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT apply_id, ordinal, transaction_id, ledger_operation_id, category_id,
                   expected_active_allocation_id, expected_transaction_revision, expected_relationship_revision,
                   expected_allocation_revision, correction_reason, ledger_request_fingerprint,
                   ledger_idempotency_key, item_state, ledger_result_fingerprint, ledger_allocation_id,
                   prior_ledger_allocation_id, safe_error_code
            FROM apply_item
            WHERE apply_id = $id
            ORDER BY ordinal ASC, transaction_id ASC;
            """;
        command.Parameters.AddWithValue("$id", applyId);
        var rows = new List<ClassifyApplyItemRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapItem(reader));
        }

        return rows;
    }

    public async Task<long> CountRunsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM apply_run;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountItemsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM apply_item;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static ClassifyApplyItemRow MapItem(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16));
}
