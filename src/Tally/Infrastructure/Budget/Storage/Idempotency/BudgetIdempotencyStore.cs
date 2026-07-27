using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Budget.Storage.Idempotency;

/// <summary>
/// Replay metadata that references immutable outcomes without serializing financial response bodies
/// (DD-BUDGET-IDEMPOTENT-MUTATIONS / DM-BUDGET-LIFECYCLE-IDEMPOTENCY).
/// </summary>
public sealed class BudgetIdempotencyStore
{
    public const string CompletedState = "Completed";

    public async Task<BudgetIdempotencyRow?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string keyDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDigest);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT key_digest, contract_version, operation_id, request_hash, state,
                   plan_id, result_revision_id, prior_active_revision_id,
                   lifecycle_event_ids, result_hash, created_at_utc, completed_at_utc
            FROM budget_idempotency_record
            WHERE key_digest = $key_digest;
            """;
        command.Parameters.AddWithValue("$key_digest", keyDigest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? BudgetRowMapper.MapIdempotency(reader) : null;
    }

    /// <summary>
    /// Classifies an existing record as replay (exact match) or conflict (key reused with different identity).
    /// </summary>
    public BudgetIdempotencyLookup Resolve(
        BudgetIdempotencyRow? existing,
        string contractVersion,
        string operationId,
        string requestHash)
    {
        if (existing is null)
        {
            return BudgetIdempotencyLookup.Miss;
        }

        var sameIdentity =
            string.Equals(existing.ContractVersion, contractVersion, StringComparison.Ordinal)
            && string.Equals(existing.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal);

        return sameIdentity ? BudgetIdempotencyLookup.Replay(existing) : BudgetIdempotencyLookup.Conflict(existing);
    }

    public async Task CommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BudgetIdempotencyRow record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.State, CompletedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Idempotency records are committed only in Completed state.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO budget_idempotency_record (
                key_digest, contract_version, operation_id, request_hash, state,
                plan_id, result_revision_id, prior_active_revision_id,
                lifecycle_event_ids, result_hash, created_at_utc, completed_at_utc
            ) VALUES (
                $key_digest, $contract_version, $operation_id, $request_hash, $state,
                $plan_id, $result_revision_id, $prior_active_revision_id,
                $lifecycle_event_ids, $result_hash, $created_at_utc, $completed_at_utc
            );
            """;
        command.Parameters.AddWithValue("$key_digest", record.KeyDigest);
        command.Parameters.AddWithValue("$contract_version", record.ContractVersion);
        command.Parameters.AddWithValue("$operation_id", record.OperationId);
        command.Parameters.AddWithValue("$request_hash", record.RequestHash);
        command.Parameters.AddWithValue("$state", record.State);
        command.Parameters.AddWithValue("$plan_id", (object?)record.PlanId ?? DBNull.Value);
        command.Parameters.AddWithValue("$result_revision_id", (object?)record.ResultRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prior_active_revision_id", (object?)record.PriorActiveRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lifecycle_event_ids", record.LifecycleEventIds);
        command.Parameters.AddWithValue("$result_hash", record.ResultHash);
        command.Parameters.AddWithValue("$created_at_utc", record.CreatedAtUtc);
        command.Parameters.AddWithValue("$completed_at_utc", record.CompletedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public enum BudgetIdempotencyDisposition
{
    Miss,
    Replay,
    Conflict
}

public sealed record BudgetIdempotencyLookup(BudgetIdempotencyDisposition Disposition, BudgetIdempotencyRow? Record)
{
    public static BudgetIdempotencyLookup Miss { get; } = new(BudgetIdempotencyDisposition.Miss, null);

    public static BudgetIdempotencyLookup Replay(BudgetIdempotencyRow record) =>
        new(BudgetIdempotencyDisposition.Replay, record);

    public static BudgetIdempotencyLookup Conflict(BudgetIdempotencyRow record) =>
        new(BudgetIdempotencyDisposition.Conflict, record);
}
