using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Relationships;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Relationships;

namespace Tally.Infrastructure.Storage.Relationships;

public sealed class RelationshipStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<bool> HasActiveRoleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string firstTransactionId,
        string secondTransactionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM financial_relationship_current
                WHERE state = 'active'
                  AND (source_transaction_id IN ($first, $second) OR target_transaction_id IN ($first, $second)));
            """, ("$first", firstTransactionId), ("$second", secondTransactionId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FinancialRelationship relationship,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO financial_relationship (
                relationship_id, relationship_type, source_transaction_id, source_role,
                target_transaction_id, target_role, amount_minor, state, created_at,
                actor_context, reconciliation_decision_id)
            VALUES ($id, $type, $sourceId, $sourceRole, $targetId, $targetRole, $amount,
                    'active', $createdAt, $actor, $decisionId);
            """,
            ("$id", relationship.RelationshipId), ("$type", TypeValue(relationship.Type)),
            ("$sourceId", relationship.SourceTransactionId), ("$sourceRole", RoleValue(relationship.SourceRole)),
            ("$targetId", relationship.TargetTransactionId), ("$targetRole", RoleValue(relationship.TargetRole)),
            ("$amount", relationship.PrincipalMinor), ("$createdAt", relationship.CreatedAt),
            ("$actor", relationship.Actor), ("$decisionId", relationship.ReconciliationDecisionId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FinancialRelationshipDetail?> GetAsync(string relationshipId, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, relationshipId, includeHistory, cancellationToken);
    }

    public async Task<FinancialRelationshipDetail?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string relationshipId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT relationship_id, relationship_type, source_transaction_id, source_role,
                   target_transaction_id, target_role, amount_minor, state, actor_context,
                   created_at, reconciliation_decision_id
            FROM financial_relationship_current
            WHERE relationship_id = $id;
            """, ("$id", relationshipId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new FinancialRelationshipDetail(
            reader.GetString(0), ParseType(reader.GetString(1)), reader.GetString(2), ParseRole(reader.GetString(3)),
            reader.GetString(4), ParseRole(reader.GetString(5)), Money.FromMinorUnits(reader.GetInt64(6)).ToString(), "ZAR",
            ParseState(reader.GetString(7)), reader.GetString(8), reader.GetString(9), Optional(reader, 10), []);
        await reader.DisposeAsync();
        return includeHistory ? detail with { History = await HistoryAsync(connection, transaction, relationshipId, cancellationToken) } : detail;
    }

    public async Task<string?> RetireForTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        string lifecycleEventId,
        string? replacementRelationshipId,
        string? reconciliationDecisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var find = Command(connection, transaction, """
            SELECT relationship_id FROM financial_relationship_current
            WHERE state = 'active' AND (source_transaction_id = $id OR target_transaction_id = $id);
            """, ("$id", transactionId));
        var relationshipId = (string?)await find.ExecuteScalarAsync(cancellationToken);
        if (relationshipId is null) return null;
        await using var command = Command(connection, transaction, """
            INSERT INTO relationship_lifecycle_event (
                lifecycle_event_id, relationship_id, event_type, replacement_relationship_id,
                reconciliation_decision_id, reason, actor_context, occurred_at)
            VALUES ($eventId, $relationshipId, $action, $replacementId, $decisionId, $reason, $actor, $occurredAt);
            """, ("$eventId", lifecycleEventId), ("$relationshipId", relationshipId),
            ("$action", replacementRelationshipId is null ? "revoked" : "replaced"),
            ("$replacementId", replacementRelationshipId), ("$decisionId", reconciliationDecisionId),
            ("$reason", reason), ("$actor", actor), ("$occurredAt", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return relationshipId;
    }

    private static async Task<IReadOnlyList<RelationshipLifecycleHistoryItem>> HistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string relationshipId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT lifecycle_event_id, event_type, replacement_relationship_id, reconciliation_decision_id,
                   reason, actor_context, occurred_at
            FROM relationship_lifecycle_event WHERE relationship_id = $id ORDER BY occurred_at, lifecycle_event_id;
            """, ("$id", relationshipId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<RelationshipLifecycleHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParseAction(reader.GetString(1)), Optional(reader, 2), Optional(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return items;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); return command; }
    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string TypeValue(FinancialRelationshipType value) => value == FinancialRelationshipType.Transfer ? "transfer" : "refund";
    private static FinancialRelationshipType ParseType(string value) => value == "transfer" ? FinancialRelationshipType.Transfer : value == "refund" ? FinancialRelationshipType.Refund : throw new InvalidOperationException("Stored relationship type is invalid.");
    private static string RoleValue(FinancialRelationshipRole value) => value switch { FinancialRelationshipRole.TransferOutflow => "transfer_outflow", FinancialRelationshipRole.TransferInflow => "transfer_inflow", FinancialRelationshipRole.RefundOriginal => "refund_original", FinancialRelationshipRole.RefundCredit => "refund_credit", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static FinancialRelationshipRole ParseRole(string value) => value switch { "transfer_outflow" => FinancialRelationshipRole.TransferOutflow, "transfer_inflow" => FinancialRelationshipRole.TransferInflow, "refund_original" => FinancialRelationshipRole.RefundOriginal, "refund_credit" => FinancialRelationshipRole.RefundCredit, _ => throw new InvalidOperationException("Stored relationship role is invalid.") };
    private static FinancialRelationshipState ParseState(string value) => value == "active" ? FinancialRelationshipState.Active : value == "retired" ? FinancialRelationshipState.Retired : throw new InvalidOperationException("Stored relationship state is invalid.");
    private static RelationshipLifecycleAction ParseAction(string value) => value == "revoked" ? RelationshipLifecycleAction.Revoked : value == "replaced" ? RelationshipLifecycleAction.Replaced : throw new InvalidOperationException("Stored relationship lifecycle action is invalid.");
}
