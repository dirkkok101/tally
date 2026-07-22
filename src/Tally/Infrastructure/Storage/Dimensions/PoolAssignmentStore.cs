using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;

namespace Tally.Infrastructure.Storage.Dimensions;

public sealed record PoolAssignmentCurrent(string EventId, string TransactionId, TransactionPoolState State, string? PoolId, TransactionAssignmentAction Action);

public sealed class PoolAssignmentStore
{
    public async Task<PoolAssignmentCurrent?> FindCurrentAsync(SqliteConnection connection, SqliteTransaction? transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT pool_assignment_event_id, transaction_id, assignment_state, pool_id, action FROM current_pool_assignment WHERE transaction_id = $id;", ("$id", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), ParseState(reader.GetString(2)), Optional(reader, 3), ParseAction(reader.GetString(4))) : null;
    }

    public async Task AppendAsync(SqliteConnection connection, SqliteTransaction transaction, string eventId, string transactionId, TransactionPoolState state, string? poolId, TransactionAssignmentAction action, string? previousEventId, string? sourceTransactionId, string? decisionId, string reason, string actor, string occurredAt, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO pool_assignment_event (pool_assignment_event_id, transaction_id, assignment_state, pool_id, action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $state, $poolId, $action, $previousId, $sourceId, $decisionId, $reason, $actor, $at);
            """, ("$eventId", eventId), ("$transactionId", transactionId), ("$state", StateValue(state)), ("$poolId", poolId), ("$action", ActionValue(action)), ("$previousId", previousEventId), ("$sourceId", sourceTransactionId), ("$decisionId", decisionId), ("$reason", reason), ("$actor", actor), ("$at", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PoolCarryForwardResult> CarryForwardAsync(SqliteConnection connection, SqliteTransaction transaction, string sourceId, string replacementId, string decisionId, string reason, string actor, string occurredAt, CancellationToken cancellationToken)
    {
        await using var authority = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM reconciliation_decision_authority a JOIN transaction_lifecycle_event l ON l.transaction_id = a.prior_transaction_id AND l.replacement_transaction_id = a.active_transaction_id AND l.reconciliation_decision_id = a.decision_id WHERE a.decision_id = $decisionId AND a.disposition_detail = 'corrected_from_statement' AND a.prior_transaction_id = $sourceId AND a.active_transaction_id = $replacementId AND l.action = 'statement_authoritative_replacement');
            """, ("$decisionId", decisionId), ("$sourceId", sourceId), ("$replacementId", replacementId));
        if (Convert.ToInt64(await authority.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Pool carry-forward requires statement-correction authority.");
        if (await FindCurrentAsync(connection, transaction, replacementId, cancellationToken) is not null) throw new InvalidOperationException("Replacement pool is already initialized.");
        var source = await FindCurrentAsync(connection, transaction, sourceId, cancellationToken) ?? throw new InvalidOperationException("Source pool assignment is missing.");
        var eventId = LedgerId.New().ToString();
        await AppendAsync(connection, transaction, eventId, replacementId, source.State, source.PoolId, TransactionAssignmentAction.CarryForward, null, sourceId, decisionId, reason, actor, occurredAt, cancellationToken);
        return new(sourceId, replacementId, decisionId, eventId);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); return command; }
    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string StateValue(TransactionPoolState state) => state == TransactionPoolState.Assigned ? "assigned" : state == TransactionPoolState.Unassigned ? "unassigned" : throw new ArgumentOutOfRangeException(nameof(state));
    private static TransactionPoolState ParseState(string value) => value == "assigned" ? TransactionPoolState.Assigned : value == "unassigned" ? TransactionPoolState.Unassigned : throw new InvalidOperationException("Stored pool state is invalid.");
    private static string ActionValue(TransactionAssignmentAction action) => action switch { TransactionAssignmentAction.Initialize => "initialize", TransactionAssignmentAction.Assign => "assign", TransactionAssignmentAction.Correct => "correct", TransactionAssignmentAction.CarryForward => "carry_forward", _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    private static TransactionAssignmentAction ParseAction(string value) => value switch { "initialize" => TransactionAssignmentAction.Initialize, "assign" => TransactionAssignmentAction.Assign, "correct" => TransactionAssignmentAction.Correct, "carry_forward" => TransactionAssignmentAction.CarryForward, _ => throw new InvalidOperationException("Stored pool action is invalid.") };
}
