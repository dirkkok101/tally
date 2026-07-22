using System.Globalization;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Dimensions;

namespace Tally.Infrastructure.Storage.Dimensions;

public sealed record SpendPoolCurrent(string PoolId, string LifecycleEventId, string Name, SpendPoolStatus Status);

public sealed class SpendPoolStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<bool> ActiveNameExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string? exceptPoolId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM catalogue_current
                WHERE catalogue_kind = 'spend_pool' AND status = 'active'
                  AND normalized_label = lower(trim($name))
                  AND ($exceptId IS NULL OR entity_id <> $exceptId));
            """,
            ("$name", name),
            ("$exceptId", exceptPoolId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string poolId,
        string lifecycleEventId,
        string name,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO spend_pool (pool_id, created_at) VALUES ($poolId, $occurredAt);
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'spend_pool', $poolId, 'create', NULL, $name,
                    lower(trim($name)), NULL, $actor, $occurredAt, NULL);
            """,
            ("$poolId", poolId),
            ("$occurredAt", occurredAt),
            ("$eventId", lifecycleEventId),
            ("$name", name),
            ("$actor", actor));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SpendPoolCurrent?> FindCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string poolId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT entity_id, lifecycle_event_id, label, status
            FROM catalogue_current
            WHERE catalogue_kind = 'spend_pool' AND entity_id = $poolId;
            """, ("$poolId", poolId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseStatus(reader.GetString(3)))
            : null;
    }

    public async Task AppendLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string lifecycleEventId,
        SpendPoolCurrent current,
        SpendPoolLifecycleAction action,
        string? newName,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        var resultingName = newName ?? (action == SpendPoolLifecycleAction.Reactivate ? current.Name : null);
        await using var command = Command(connection, transaction, """
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'spend_pool', $poolId, $action, $previousName, $newName,
                    lower(trim(COALESCE($newName, $previousName))), $reason, $actor, $occurredAt, $previousEventId);
            """,
            ("$eventId", lifecycleEventId),
            ("$poolId", current.PoolId),
            ("$action", ActionValue(action)),
            ("$previousName", current.Name),
            ("$newName", resultingName),
            ("$reason", reason),
            ("$actor", actor),
            ("$occurredAt", occurredAt),
            ("$previousEventId", current.LifecycleEventId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> ActiveAssignmentErrorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string poolId,
        CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(connection, transaction, poolId, cancellationToken);
        return current switch
        {
            null => "LEDGER-SPEND-POOL-NOT-FOUND",
            { Status: SpendPoolStatus.Archived } => "LEDGER-SPEND-POOL-ARCHIVED",
            _ => null
        };
    }

    public async Task<SpendPoolDetail?> GetAsync(string poolId, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, poolId, includeHistory, cancellationToken);
    }

    public async Task<SpendPoolDetail?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string poolId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT pool.pool_id, current.label, current.status,
                   (SELECT COUNT(*) FROM current_pool_assignment AS assignment
                    WHERE assignment.assignment_state = 'assigned' AND assignment.pool_id = pool.pool_id
                      AND NOT EXISTS (SELECT 1 FROM transaction_lifecycle_event AS lifecycle WHERE lifecycle.transaction_id = assignment.transaction_id)),
                   (SELECT COUNT(DISTINCT assignment.transaction_id) FROM pool_assignment_event AS assignment WHERE assignment.pool_id = pool.pool_id),
                   created.actor, pool.created_at
            FROM spend_pool AS pool
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'spend_pool' AND current.entity_id = pool.pool_id
            JOIN catalogue_lifecycle_event AS created
              ON created.catalogue_kind = 'spend_pool' AND created.entity_id = pool.pool_id AND created.action = 'create'
            WHERE pool.pool_id = $poolId;
            """, ("$poolId", poolId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new SpendPoolDetail(
            reader.GetString(0), reader.GetString(1), ParseStatus(reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetString(6), []);
        await reader.DisposeAsync();
        return includeHistory
            ? detail with { LifecycleHistory = await HistoryAsync(connection, transaction, poolId, cancellationToken) }
            : detail;
    }

    public async Task<IReadOnlyList<SpendPoolDetail>> ListAsync(SpendPoolStatus? status, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var command = Command(connection, null, """
            SELECT pool.pool_id
            FROM spend_pool AS pool
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'spend_pool' AND current.entity_id = pool.pool_id
            WHERE ($status IS NULL OR current.status = $status)
            ORDER BY lower(current.label), pool.pool_id;
            """, ("$status", status is null ? null : StatusValue(status.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var pools = new List<SpendPoolDetail>();
        foreach (var id in ids) pools.Add((await GetAsync(connection, null, id, false, cancellationToken))!);
        return pools;
    }

    private static async Task<IReadOnlyList<SpendPoolHistoryItem>> HistoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string poolId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT lifecycle_event_id, action, previous_label, new_label, reason, actor, occurred_at, previous_event_id
            FROM catalogue_lifecycle_event
            WHERE catalogue_kind = 'spend_pool' AND entity_id = $poolId
            ORDER BY occurred_at, lifecycle_event_id;
            """, ("$poolId", poolId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<SpendPoolHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new(
                reader.GetString(0), ParseAction(reader.GetString(1)), Optional(reader, 2), Optional(reader, 3),
                Optional(reader, 4), reader.GetString(5), reader.GetString(6), Optional(reader, 7)));
        }

        return history;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string StatusValue(SpendPoolStatus status) => status == SpendPoolStatus.Active ? "active" : "archived";
    private static SpendPoolStatus ParseStatus(string status) => status switch { "active" => SpendPoolStatus.Active, "archived" => SpendPoolStatus.Archived, _ => throw new InvalidOperationException("Unknown Spend Pool status.") };
    private static string ActionValue(SpendPoolLifecycleAction action) => action.ToString().ToLowerInvariant();
    private static SpendPoolLifecycleAction ParseAction(string action) => action switch
    {
        "create" => SpendPoolLifecycleAction.Create,
        "rename" => SpendPoolLifecycleAction.Rename,
        "archive" => SpendPoolLifecycleAction.Archive,
        "reactivate" => SpendPoolLifecycleAction.Reactivate,
        _ => throw new InvalidOperationException("Unknown Spend Pool lifecycle action.")
    };
}
