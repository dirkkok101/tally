using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class LedgerCatalogueSchemaTests : IAsyncLifetime
{
    private const string At = "2026-07-21T00:00:00Z";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"tally-catalogue-{Guid.NewGuid():N}.db");

    // DM-LEDGER-ACCOUNT, DM-LEDGER-SPEND-CATEGORY, DM-LEDGER-CATALOGUE-LIFECYCLE
    [Fact]
    public async Task V001_creates_the_financial_dimension_inventory()
    {
        await using var connection = await OpenSchemaAsync();

        Assert.Equal(
            ["account", "cardholder", "catalogue_lifecycle_event", "category_parent_event", "payment_instrument", "spend_category", "spend_pool"],
            await CatalogueTableNamesAsync(connection));
    }

    // DM-LEDGER-ACCOUNT
    [Fact]
    public async Task Account_type_class_currency_and_timestamp_are_closed()
    {
        await using var connection = await OpenSchemaAsync();

        await Assert.ThrowsAsync<SqliteException>(() => AccountIdentityAsync(connection, "bad-type", "current", "asset", "1001", At));
        await Assert.ThrowsAsync<SqliteException>(() => AccountIdentityAsync(connection, "bad-class", "credit_card", "asset", "1002", At));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO account VALUES ('bad-currency', 'Bank', 'cheque', 'asset', '1003', 'USD', '{At}');"));
        await Assert.ThrowsAsync<SqliteException>(() => AccountIdentityAsync(connection, "bad-time", "cheque", "asset", "1004", "now"));
    }

    // DM-LEDGER-ACCOUNT
    [Fact]
    public async Task Active_masked_account_identity_is_unique_within_institution()
    {
        await using var connection = await OpenSchemaAsync();
        await AccountAsync(connection, "a1", "Bank", "1001", "Primary");
        await AccountIdentityAsync(connection, "a2", "cheque", "asset", "1001", At);

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "a2-create", "account", "a2", "create", null, "Second", "second", null));

        await LifecycleAsync(connection, "a1-archive", "account", "a1", "archive", "Primary", null, "primary", "a1-create");
        await LifecycleAsync(connection, "a2-create", "account", "a2", "create", null, "Second", "second", null);
    }

    // DM-LEDGER-PAYMENT-ATTRIBUTION, DM-LEDGER-SPEND-POOL-ASSIGNMENT
    [Fact]
    public async Task Payment_cardholder_and_pool_identities_are_independent_and_privacy_bounded()
    {
        await using var connection = await OpenSchemaAsync();
        await AccountAsync(connection, "a1", "Bank", "1001", "Primary");

        await ExecuteAsync(connection, $"INSERT INTO payment_instrument VALUES ('i1', 'a1', '4321', '{At}'); INSERT INTO cardholder VALUES ('h1', '{At}'); INSERT INTO spend_pool VALUES ('p1', '{At}');");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO payment_instrument VALUES ('i2', 'missing', '1234', '{At}');"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO payment_instrument VALUES ('i3', NULL, '1234567890123456', '{At}');"));
        Assert.Equal(["instrument_id", "account_id", "masked_suffix", "created_at"], await ColumnNamesAsync(connection, "payment_instrument"));
    }

    // DM-LEDGER-CATALOGUE-LIFECYCLE
    [Fact]
    public async Task Lifecycle_kind_action_and_entity_reference_are_closed()
    {
        await using var connection = await OpenSchemaAsync();
        await ExecuteAsync(connection, $"INSERT INTO spend_pool VALUES ('p1', '{At}');");

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "bad-kind", "bank_provider", "p1", "create", null, "Pool", "pool", null));
        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "bad-action", "spend_pool", "p1", "delete", null, "Pool", "pool", null));
        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "missing", "spend_pool", "missing", "create", null, "Pool", "pool", null));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Lifecycle_is_append_only_linear_history()
    {
        await using var connection = await OpenSchemaAsync();
        await PoolAsync(connection, "p1", "Company");
        await LifecycleAsync(connection, "p1-rename", "spend_pool", "p1", "rename", "Company", "Company paid", "company paid", "p1-create");

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "p1-fork", "spend_pool", "p1", "rename", "Company", "Personal", "personal", "p1-create"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE catalogue_lifecycle_event SET reason = 'changed' WHERE lifecycle_event_id = 'p1-create';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM catalogue_lifecycle_event WHERE lifecycle_event_id = 'p1-create';"));
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Current_category_projection_derives_depth_and_ancestry_from_history()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root-a", null, "Root A");
        await CategoryAsync(connection, "root-b", null, "Root B");
        await CategoryAsync(connection, "child", "root-a", "Child");
        await ParentAsync(connection, "child-parent-2", "child", "root-b", "reparent", "child-parent-1");

        Assert.Equal("1|/root-b/child/", await ScalarStringAsync(connection, "SELECT depth || '|' || ancestry_ids FROM current_category_projection WHERE category_id = 'child';"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM category_parent_event WHERE category_id = 'child';"));
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Category_parent_rejects_self_parent_and_descendant_cycles()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root", null, "Root");
        await CategoryAsync(connection, "child", "root", "Child");
        await CategoryAsync(connection, "grandchild", "child", "Grandchild");

        await Assert.ThrowsAsync<SqliteException>(() => ParentAsync(connection, "self", "child", "child", "reparent", "child-parent-1"));
        await Assert.ThrowsAsync<SqliteException>(() => ParentAsync(connection, "cycle", "root", "grandchild", "reparent", "root-parent-1"));
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Active_normalized_category_names_are_unique_among_siblings_only()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root-a", null, "Root A");
        await CategoryAsync(connection, "root-b", null, "Root B");
        await CategoryAsync(connection, "food-a", "root-a", "Food");
        await CategoryIdentityAndParentAsync(connection, "food-duplicate", "root-a");

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "food-duplicate-create", "category", "food-duplicate", "create", null, " food ", "food", null));

        await CategoryAsync(connection, "food-b", "root-b", "Food");
        await LifecycleAsync(connection, "food-a-archive", "category", "food-a", "archive", "Food", null, "food", "food-a-create");
        await LifecycleAsync(connection, "food-duplicate-create", "category", "food-duplicate", "create", null, "Food", "food", null);
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Archived_or_inactive_ancestry_rejects_new_children_and_reactivation()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root", null, "Root");
        await CategoryAsync(connection, "child", "root", "Child");
        await LifecycleAsync(connection, "child-archive", "category", "child", "archive", "Child", null, "child", "child-create");
        await LifecycleAsync(connection, "root-archive", "category", "root", "archive", "Root", null, "root", "root-create");
        await ExecuteAsync(connection, $"INSERT INTO spend_category VALUES ('new-child', '{At}');");

        await Assert.ThrowsAsync<SqliteException>(() => ParentAsync(connection, "new-child-parent", "new-child", "root", "initialize", null));
        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "child-reactivate", "category", "child", "reactivate", "Child", "Child", "child", "child-archive"));
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Parent_with_active_children_cannot_be_archived()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root", null, "Root");
        await CategoryAsync(connection, "child", "root", "Child");

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleAsync(connection, "root-archive", "category", "root", "archive", "Root", null, "root", "root-create"));
        Assert.Equal("active", await ScalarStringAsync(connection, "SELECT status FROM current_category_projection WHERE category_id = 'root';"));
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Reparent_requires_an_active_category_and_active_parent()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root-a", null, "Root A");
        await CategoryAsync(connection, "root-b", null, "Root B");
        await CategoryAsync(connection, "child", "root-a", "Child");
        await LifecycleAsync(connection, "child-archive", "category", "child", "archive", "Child", null, "child", "child-create");

        await Assert.ThrowsAsync<SqliteException>(() => ParentAsync(connection, "child-reparent", "child", "root-b", "reparent", "child-parent-1"));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Identities_and_parent_events_cannot_be_updated_or_deleted()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAsync(connection, "root", null, "Root");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE spend_category SET created_at = '2026-07-22T00:00:00Z' WHERE category_id = 'root';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM spend_category WHERE category_id = 'root';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM category_parent_event WHERE parent_event_id = 'root-parent-1';"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public void Duplicate_fragment_registration_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry(
            [new V001CatalogueSchema(), new V001CatalogueSchema()],
            [V001CatalogueSchema.FragmentName]));

    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Injected_fragment_failure_rolls_back_the_entire_catalogue()
    {
        await using var connection = await OpenAsync();
        var registry = new LedgerSchemaFragmentRegistry(
            [new V001StorageSchema(), new V001CatalogueSchema(), new FailingFragment()],
            [V001StorageSchema.FragmentName, V001CatalogueSchema.FragmentName, "zz_failing"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyAsync(connection, CancellationToken.None));

        Assert.Empty(await AllTableNamesAsync(connection));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        return Task.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private async Task<SqliteConnection> OpenSchemaAsync()
    {
        var connection = await OpenAsync();
        var registry = new LedgerSchemaFragmentRegistry(
            [new V001StorageSchema(), new V001CatalogueSchema()],
            [V001StorageSchema.FragmentName, V001CatalogueSchema.FragmentName]);
        await registry.ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task AccountAsync(SqliteConnection connection, string id, string institution, string maskedIdentifier, string label)
    {
        await AccountIdentityAsync(connection, id, "cheque", "asset", maskedIdentifier, At, institution);
        await LifecycleAsync(connection, $"{id}-create", "account", id, "create", null, label, Normalize(label), null);
    }

    private static Task AccountIdentityAsync(SqliteConnection connection, string id, string accountType, string accountClass, string maskedIdentifier, string createdAt, string institution = "Bank") =>
        ExecuteAsync(connection, $"INSERT INTO account VALUES ('{id}', '{institution}', '{accountType}', '{accountClass}', '{maskedIdentifier}', 'ZAR', '{createdAt}');");

    private static async Task PoolAsync(SqliteConnection connection, string id, string label)
    {
        await ExecuteAsync(connection, $"INSERT INTO spend_pool VALUES ('{id}', '{At}');");
        await LifecycleAsync(connection, $"{id}-create", "spend_pool", id, "create", null, label, Normalize(label), null);
    }

    private static async Task CategoryAsync(SqliteConnection connection, string id, string? parentId, string label)
    {
        await CategoryIdentityAndParentAsync(connection, id, parentId);
        await LifecycleAsync(connection, $"{id}-create", "category", id, "create", null, label, Normalize(label), null);
    }

    private static async Task CategoryIdentityAndParentAsync(SqliteConnection connection, string id, string? parentId)
    {
        await ExecuteAsync(connection, $"INSERT INTO spend_category VALUES ('{id}', '{At}');");
        await ParentAsync(connection, $"{id}-parent-1", id, parentId, "initialize", null);
    }

    private static Task LifecycleAsync(
        SqliteConnection connection,
        string eventId,
        string kind,
        string entityId,
        string action,
        string? previousLabel,
        string? newLabel,
        string normalizedLabel,
        string? previousEventId) =>
        ExecuteAsync(connection, $"INSERT INTO catalogue_lifecycle_event VALUES ('{eventId}', '{kind}', '{entityId}', '{action}', {Sql(previousLabel)}, {Sql(newLabel)}, '{normalizedLabel}', 'reason', 'owner', '{At}', {Sql(previousEventId)});");

    private static Task ParentAsync(
        SqliteConnection connection,
        string eventId,
        string categoryId,
        string? parentId,
        string action,
        string? previousEventId) =>
        ExecuteAsync(connection, $"INSERT INTO category_parent_event VALUES ('{eventId}', '{categoryId}', {Sql(parentId)}, '{action}', 'reason', 'owner', '{At}', {Sql(previousEventId)});");

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql) =>
        Convert.ToString(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string[]> CatalogueTableNamesAsync(SqliteConnection connection)
    {
        var all = await AllTableNamesAsync(connection);
        return all.Where(name => name is not "artifact_manifest" and not "idempotency_record" and not "logical_effect" and not "migration_metadata" and not "store_generation").ToArray();
    }

    private static async Task<string[]> AllTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names.ToArray();
    }

    private sealed class FailingFragment : ILedgerSchemaFragment
    {
        public int Version => 1;
        public string Name => "zz_failing";

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE injected_rollback_probe (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("Injected migration failure.");
        }
    }
}
