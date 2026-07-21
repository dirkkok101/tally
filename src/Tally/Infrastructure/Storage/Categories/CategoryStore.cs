using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Ledger;

namespace Tally.Infrastructure.Storage.Categories;

public sealed record CategoryCurrentState(string CategoryId, string LifecycleEventId, string ParentEventId, string Name, CategoryStatus Status, string? ParentCategoryId, int Depth, IReadOnlyList<string> AncestryIds);

public sealed class CategoryStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string categoryId, string parentEventId, string lifecycleEventId, string name, string? parentCategoryId, string actor, string occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO spend_category VALUES ($id, $occurredAt);
            INSERT INTO category_parent_event (parent_event_id, category_id, parent_category_id, action, reason, actor, occurred_at, previous_parent_event_id)
            VALUES ($parentEventId, $id, $parentId, 'initialize', 'Initial parent', $actor, $occurredAt, NULL);
            INSERT INTO catalogue_lifecycle_event (lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label, normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($lifecycleEventId, 'category', $id, 'create', NULL, $name, lower(trim($name)), NULL, $actor, $occurredAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", categoryId);
        command.Parameters.AddWithValue("$occurredAt", occurredAt);
        command.Parameters.AddWithValue("$parentEventId", parentEventId);
        command.Parameters.AddWithValue("$parentId", DbValue(parentCategoryId));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$lifecycleEventId", lifecycleEventId);
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendLifecycleAsync(SqliteConnection connection, SqliteTransaction transaction, string eventId, CategoryCurrentState current, CategoryLifecycleAction action, string? newName, string reason, string actor, string occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalogue_lifecycle_event (lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label, normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'category', $id, $action, $previousName, $newName, lower(trim(COALESCE($newName, $previousName))), $reason, $actor, $occurredAt, $previousEventId);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$id", current.CategoryId);
        command.Parameters.AddWithValue("$action", LifecycleActionValue(action));
        command.Parameters.AddWithValue("$previousName", current.Name);
        command.Parameters.AddWithValue("$newName", DbValue(newName ?? (action == CategoryLifecycleAction.Reactivate ? current.Name : null)));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$occurredAt", occurredAt);
        command.Parameters.AddWithValue("$previousEventId", current.LifecycleEventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendParentAsync(SqliteConnection connection, SqliteTransaction transaction, string eventId, CategoryCurrentState current, string? parentCategoryId, string reason, string actor, string occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO category_parent_event (parent_event_id, category_id, parent_category_id, action, reason, actor, occurred_at, previous_parent_event_id)
            VALUES ($eventId, $id, $parentId, 'reparent', $reason, $actor, $occurredAt, $previousEventId);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$id", current.CategoryId);
        command.Parameters.AddWithValue("$parentId", DbValue(parentCategoryId));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$occurredAt", occurredAt);
        command.Parameters.AddWithValue("$previousEventId", current.ParentEventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CategoryCurrentState?> FindCurrentAsync(SqliteConnection connection, SqliteTransaction? transaction, string categoryId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT projection.category_id, lifecycle.lifecycle_event_id, parent.parent_event_id, projection.name,
                   projection.status, projection.parent_category_id, projection.depth, projection.ancestry_ids
            FROM current_category_projection AS projection
            JOIN catalogue_current AS lifecycle ON lifecycle.catalogue_kind = 'category' AND lifecycle.entity_id = projection.category_id
            JOIN category_parent_current AS parent ON parent.category_id = projection.category_id
            WHERE projection.category_id = $id;
            """;
        command.Parameters.AddWithValue("$id", categoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<bool> SiblingNameExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string name, string? parentId, string? exceptCategoryId, CancellationToken cancellationToken) =>
        await ExistsAsync(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM current_category_projection
                WHERE status = 'active' AND normalized_sibling_name = lower(trim($name))
                  AND parent_category_id IS $parentId
                  AND ($exceptId IS NULL OR category_id <> $exceptId));
            """, cancellationToken, ("$name", name), ("$parentId", DbValue(parentId)), ("$exceptId", DbValue(exceptCategoryId)));

    public async Task<bool> HasActiveChildrenAsync(SqliteConnection connection, SqliteTransaction transaction, string categoryId, CancellationToken cancellationToken) =>
        await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM current_category_projection WHERE parent_category_id = $id AND status = 'active');", cancellationToken, ("$id", categoryId));

    public async Task<bool> WouldCreateCycleAsync(SqliteConnection connection, SqliteTransaction transaction, string categoryId, string parentId, CancellationToken cancellationToken) =>
        await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM current_category_projection WHERE category_id = $parentId AND instr(ancestry_ids, '/' || $categoryId || '/') > 0);", cancellationToken, ("$parentId", parentId), ("$categoryId", categoryId));

    public async Task<CategoryDetail?> GetAsync(string categoryId, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, categoryId, includeHistory, cancellationToken);
    }

    public async Task<CategoryDetail?> GetAsync(SqliteConnection connection, SqliteTransaction? transaction, string categoryId, bool includeHistory, CancellationToken cancellationToken)
    {
        var state = await FindCurrentAsync(connection, transaction, categoryId, cancellationToken);
        if (state is null) return null;
        var created = await CreatedAsync(connection, transaction, categoryId, cancellationToken);
        var lifecycle = includeHistory ? await LifecycleHistoryAsync(connection, transaction, categoryId, cancellationToken) : [];
        var parents = includeHistory ? await ParentHistoryAsync(connection, transaction, categoryId, cancellationToken) : [];
        return new(state.CategoryId, state.Name, state.Status, state.ParentCategoryId, state.Depth, state.AncestryIds, created.Actor, created.At, lifecycle, parents);
    }

    public async Task<IReadOnlyList<CategorySummary>> ListAsync(CategoryStatus? status, string? parentCategoryId, CategoryListScope scope, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, name, status, parent_category_id, depth, ancestry_ids
            FROM current_category_projection
            WHERE ($status IS NULL OR status = $status)
              AND (
                $scope = 'all'
                OR ($scope = 'children' AND parent_category_id IS $parentId)
                OR ($scope = 'subtree' AND instr(ancestry_ids, '/' || $parentId || '/') > 0))
            ORDER BY substr(ancestry_ids, 1, length(ancestry_ids) - length(category_id) - 1), lower(name), category_id;
            """;
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : StatusValue(status.Value));
        command.Parameters.AddWithValue("$scope", ScopeValue(scope));
        command.Parameters.AddWithValue("$parentId", DbValue(parentCategoryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CategorySummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(reader.GetString(0), reader.GetString(1), ParseStatus(reader.GetString(2)), OptionalString(reader, 3), reader.GetInt32(4), ParseAncestry(reader.GetString(5))));
        }
        return items;
    }

    private static async Task<(string Actor, string At)> CreatedAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT actor, occurred_at FROM catalogue_lifecycle_event WHERE catalogue_kind = 'category' AND entity_id = $id AND action = 'create';";
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<CategoryLifecycleHistoryItem>> LifecycleHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT lifecycle_event_id, action, previous_label, new_label, reason, actor, occurred_at, previous_event_id FROM catalogue_lifecycle_event WHERE catalogue_kind = 'category' AND entity_id = $id ORDER BY occurred_at, lifecycle_event_id;";
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var items = new List<CategoryLifecycleHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParseLifecycleAction(reader.GetString(1)), OptionalString(reader, 2), OptionalString(reader, 3), OptionalString(reader, 4), reader.GetString(5), reader.GetString(6), OptionalString(reader, 7)));
        return items;
    }

    private static async Task<IReadOnlyList<CategoryParentHistoryItem>> ParentHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT parent_event_id, action, parent_category_id, reason, actor, occurred_at, previous_parent_event_id FROM category_parent_event WHERE category_id = $id ORDER BY occurred_at, parent_event_id;";
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var items = new List<CategoryParentHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParseParentAction(reader.GetString(1)), OptionalString(reader, 2), reader.GetString(3), reader.GetString(4), reader.GetString(5), OptionalString(reader, 6)));
        return items;
    }

    private static CategoryCurrentState ReadState(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseStatus(reader.GetString(4)), OptionalString(reader, 5), reader.GetInt32(6), ParseAncestry(reader.GetString(7)));
    private static IReadOnlyList<string> ParseAncestry(string value) => value.Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1; }
    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string LifecycleActionValue(CategoryLifecycleAction value) => value switch { CategoryLifecycleAction.Create => "create", CategoryLifecycleAction.Rename => "rename", CategoryLifecycleAction.Archive => "archive", CategoryLifecycleAction.Reactivate => "reactivate", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static CategoryLifecycleAction ParseLifecycleAction(string value) => value switch { "create" => CategoryLifecycleAction.Create, "rename" => CategoryLifecycleAction.Rename, "archive" => CategoryLifecycleAction.Archive, "reactivate" => CategoryLifecycleAction.Reactivate, _ => throw new InvalidOperationException("Stored category lifecycle action is invalid.") };
    private static CategoryParentAction ParseParentAction(string value) => value == "initialize" ? CategoryParentAction.Initialize : value == "reparent" ? CategoryParentAction.Reparent : throw new InvalidOperationException("Stored category parent action is invalid.");
    private static string StatusValue(CategoryStatus value) => value == CategoryStatus.Active ? "active" : "archived";
    private static CategoryStatus ParseStatus(string value) => value == "active" ? CategoryStatus.Active : value == "archived" ? CategoryStatus.Archived : throw new InvalidOperationException("Stored category status is invalid.");
    private static string ScopeValue(CategoryListScope value) => value switch { CategoryListScope.Children => "children", CategoryListScope.Subtree => "subtree", CategoryListScope.All => "all", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
}
