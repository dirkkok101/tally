using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;

namespace Tally.Infrastructure.Storage.Transactions;

public sealed record CategoryAllocationCurrent(
    string AllocationEventId,
    string TransactionId,
    string CategoryId,
    IReadOnlyList<string> CurrentAncestryIds,
    TransactionCategoryAction Action,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed class CategoryAllocationStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<CategoryAllocationCurrent?> FindCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT allocation.allocation_event_id, allocation.transaction_id, allocation.category_id,
                   category.ancestry_ids, allocation.action, event.reason, event.actor, event.occurred_at
            FROM current_category_allocation AS allocation
            JOIN category_allocation_event AS event ON event.allocation_event_id = allocation.allocation_event_id
            JOIN current_category_projection AS category ON category.category_id = allocation.category_id
            WHERE allocation.transaction_id = $transactionId;
            """, ("$transactionId", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseAncestry(reader.GetString(3)),
                ParseAction(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7))
            : null;
    }

    public async Task AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string allocationEventId,
        string transactionId,
        string categoryId,
        TransactionCategoryAction action,
        string? previousEventId,
        string? sourceTransactionId,
        string? reconciliationDecisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO category_allocation_event (
                allocation_event_id, transaction_id, category_id, action, previous_event_id,
                source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $categoryId, $action, $previousEventId,
                    $sourceTransactionId, $decisionId, $reason, $actor, $occurredAt);
            """,
            ("$eventId", allocationEventId), ("$transactionId", transactionId), ("$categoryId", categoryId),
            ("$action", ActionValue(action)), ("$previousEventId", previousEventId), ("$sourceTransactionId", sourceTransactionId),
            ("$decisionId", reconciliationDecisionId), ("$reason", reason), ("$actor", actor), ("$occurredAt", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> CarryForwardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTransactionId,
        string replacementTransactionId,
        string reconciliationDecisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedStatementCorrectionAsync(
                connection, transaction, sourceTransactionId, replacementTransactionId, reconciliationDecisionId, cancellationToken))
        {
            throw new InvalidOperationException("Category carry-forward requires statement-correction authority.");
        }

        var source = await FindCurrentAsync(connection, transaction, sourceTransactionId, cancellationToken);
        if (source is null) return null;
        var eventId = LedgerId.New().ToString();
        await AppendAsync(
            connection, transaction, eventId, replacementTransactionId, source.CategoryId,
            TransactionCategoryAction.CarryForward, null, sourceTransactionId, reconciliationDecisionId,
            reason, actor, occurredAt, cancellationToken);
        return eventId;
    }

    public Task<IReadOnlyList<string>> ListDirectMemberTransactionIdsAsync(string categoryId, CancellationToken cancellationToken) =>
        ListMembersAsync(categoryId, includeDescendants: false, cancellationToken);

    public Task<IReadOnlyList<string>> ListSubtreeMemberTransactionIdsAsync(string categoryId, CancellationToken cancellationToken) =>
        ListMembersAsync(categoryId, includeDescendants: true, cancellationToken);

    public async Task<IReadOnlyList<string>> ListAllMemberTransactionIdsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await ReadMemberIdsAsync(connection, """
            SELECT DISTINCT allocation.transaction_id
            FROM current_category_allocation AS allocation
            WHERE NOT EXISTS (SELECT 1 FROM transaction_lifecycle_event AS lifecycle WHERE lifecycle.transaction_id = allocation.transaction_id)
            ORDER BY allocation.transaction_id;
            """, null, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ListMembersAsync(string categoryId, bool includeDescendants, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await ReadMemberIdsAsync(connection, """
            SELECT DISTINCT allocation.transaction_id
            FROM current_category_allocation AS allocation
            JOIN current_category_projection AS category ON category.category_id = allocation.category_id
            WHERE ($includeDescendants = 0 AND allocation.category_id = $categoryId
                   OR $includeDescendants = 1 AND instr(category.ancestry_ids, '/' || $categoryId || '/') > 0)
              AND NOT EXISTS (SELECT 1 FROM transaction_lifecycle_event AS lifecycle WHERE lifecycle.transaction_id = allocation.transaction_id)
            ORDER BY allocation.transaction_id;
            """, (categoryId, includeDescendants), cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadMemberIdsAsync(
        SqliteConnection connection,
        string sql,
        (string CategoryId, bool IncludeDescendants)? filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (filter is { } value)
        {
            command.Parameters.AddWithValue("$categoryId", value.CategoryId);
            command.Parameters.AddWithValue("$includeDescendants", value.IncludeDescendants ? 1 : 0);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(reader.GetString(0));
        return items;
    }

    private static async Task<bool> IsAuthorizedStatementCorrectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTransactionId,
        string replacementTransactionId,
        string decisionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1
                FROM reconciliation_decision_authority AS authority
                JOIN transaction_lifecycle_event AS lifecycle
                  ON lifecycle.transaction_id = authority.prior_transaction_id
                 AND lifecycle.replacement_transaction_id = authority.active_transaction_id
                 AND lifecycle.reconciliation_decision_id = authority.decision_id
                WHERE authority.decision_id = $decisionId
                  AND authority.disposition_detail = 'corrected_from_statement'
                  AND authority.prior_transaction_id = $sourceId
                  AND authority.active_transaction_id = $replacementId
                  AND lifecycle.action = 'statement_authoritative_replacement');
            """, ("$decisionId", decisionId), ("$sourceId", sourceTransactionId), ("$replacementId", replacementTransactionId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static IReadOnlyList<string> ParseAncestry(string value) => value.Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static string ActionValue(TransactionCategoryAction action) => action switch
    {
        TransactionCategoryAction.Assign => "assign",
        TransactionCategoryAction.Correct => "correct",
        TransactionCategoryAction.CarryForward => "carry_forward",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private static TransactionCategoryAction ParseAction(string value) => value switch
    {
        "assign" => TransactionCategoryAction.Assign,
        "correct" => TransactionCategoryAction.Correct,
        "carry_forward" => TransactionCategoryAction.CarryForward,
        _ => throw new InvalidOperationException("Stored category allocation action is invalid.")
    };
}
