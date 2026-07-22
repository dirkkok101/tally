using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class CompleteLedgerSchemaTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-complete-schema-{Guid.NewGuid():N}");

    // DM-LEDGER-STORE-GENERATION
    [Fact]
    public void Complete_schema_names_every_owned_fragment_in_version_name_order()
    {
        Assert.Equal(["storage", "v001_catalogue", "v001_relationship_actuals", "v001_transaction", "z_evidence_reconciliation"], CompleteLedgerSchema.V1FragmentNames);
        Assert.Equal(["storage", "v001_catalogue", "v001_relationship_actuals", "v001_transaction", "z_evidence_reconciliation", "statement_authority", "actuals_query_indexes"], CompleteLedgerSchema.CurrentFragmentNames);
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Fresh_store_reaches_the_complete_current_inventory()
    {
        await using var connection = await OpenAsync("fresh");
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(3L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(35L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"));
        Assert.Equal(7L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM migration_metadata;"));
        Assert.Equal(3L, await ScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN (
                  'ix_category_allocation_event_transaction',
                  'ix_pool_assignment_event_transaction',
                  'ix_transaction_attribution_event_transaction');
            """));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task V001_upgrade_and_fresh_store_have_identical_schema_sql()
    {
        await using var fresh = await OpenAsync("fresh-current");
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(fresh, CancellationToken.None);
        await using var upgraded = await OpenAsync("upgraded-current");
        await CompleteLedgerSchema.CreateV1().ApplyAsync(upgraded, CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(upgraded, CancellationToken.None);

        Assert.Equal(await SchemaSqlAsync(fresh), await SchemaSqlAsync(upgraded));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Fresh_and_upgraded_stores_have_identical_columns_indexes_and_foreign_keys()
    {
        await using var fresh = await OpenAsync("fresh-shape");
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(fresh, CancellationToken.None);
        await using var upgraded = await OpenAsync("upgraded-shape");
        await CompleteLedgerSchema.CreateV1().ApplyAsync(upgraded, CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(upgraded, CancellationToken.None);

        Assert.Equal(await StructuralInventoryAsync(fresh), await StructuralInventoryAsync(upgraded));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK, ADR-CORE-0030
    [Fact]
    public async Task Complete_schema_retains_the_evidence_privacy_allowlist_and_no_transport_canaries()
    {
        await using var connection = await OpenCurrentAsync("privacy");
        var schema = await SchemaSqlAsync(connection);

        Assert.Equal(["evidence_id", "kind", "logical_identity_digest", "opaque_external_reference", "content_fingerprint", "recorded_by", "recorded_at"], await ColumnNamesAsync(connection, "evidence_record"));
        foreach (var canary in new[] { "mailbox", "mime", "recipient", "whatsapp", "provider_cursor", "raw_payload" })
        {
            Assert.DoesNotContain(canary, schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task Complete_schema_contains_restricted_statement_authority_references()
    {
        await using var connection = await OpenCurrentAsync("authority-fks");
        var foreignKeys = await ForeignKeysAsync(connection, "statement_correction");

        Assert.Contains("reconciliation_decision_authority", foreignKeys);
        Assert.Contains("transaction_lifecycle_event", foreignKeys);
        Assert.Contains("category_allocation_event", foreignKeys);
        Assert.Contains("pool_assignment_event", foreignKeys);
        Assert.Contains("transaction_attribution_event", foreignKeys);
    }

    // DD-LEDGER-CATEGORY-HIERARCHY
    [Fact]
    public async Task Complete_schema_enforces_the_acyclic_hierarchy_rule()
    {
        await using var connection = await OpenCurrentAsync("hierarchy");
        await SeedAccountAndCategoriesAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO category_parent_event VALUES ('cycle', 'root', 'child', 'reparent', 'reason', 'owner', '2026-07-21T00:00:00Z', 'root-parent');"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Complete_schema_passes_integrity_and_foreign_key_checks()
    {
        await using var connection = await OpenCurrentAsync("integrity");

        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public void Missing_complete_fragment_is_rejected_before_sql() =>
        Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry(
            [new V001StorageSchema()],
            CompleteLedgerSchema.V1FragmentNames));

    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Injected_fresh_schema_failure_leaves_no_partial_inventory()
    {
        await using var connection = await OpenAsync("rollback");
        var registry = new LedgerSchemaFragmentRegistry([new V001StorageSchema(), new FailingFragment()], [V001StorageSchema.FragmentName, "zz_failing"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyAsync(connection, CancellationToken.None));

        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(string name)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, name + ".db")}");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private async Task<SqliteConnection> OpenCurrentAsync(string name)
    {
        var connection = await OpenAsync(name);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task SeedAccountAndCategoriesAsync(SqliteConnection connection)
    {
        const string at = "2026-07-21T00:00:00Z";
        await ExecuteAsync(connection, $"""
            INSERT INTO account VALUES ('account', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '{at}');
            INSERT INTO catalogue_lifecycle_event VALUES ('account-create', 'account', 'account', 'create', NULL, 'Primary', 'primary', 'reason', 'owner', '{at}', NULL);
            INSERT INTO spend_category VALUES ('root', '{at}');
            INSERT INTO category_parent_event VALUES ('root-parent', 'root', NULL, 'initialize', 'reason', 'owner', '{at}', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('root-create', 'category', 'root', 'create', NULL, 'Root', 'root', 'reason', 'owner', '{at}', NULL);
            INSERT INTO spend_category VALUES ('child', '{at}');
            INSERT INTO category_parent_event VALUES ('child-parent', 'child', 'root', 'initialize', 'reason', 'owner', '{at}', NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ('child-create', 'category', 'child', 'create', NULL, 'Child', 'child', 'reason', 'owner', '{at}', NULL);
            """);
    }

    private static async Task<string> SchemaSqlAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(type || ':' || name || ':' || COALESCE(sql, ''), char(10)) FROM (SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name);";
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<string> StructuralInventoryAsync(SqliteConnection connection)
    {
        var tables = await TableNamesAsync(connection);
        var parts = new List<string>();
        foreach (var table in tables)
        {
            parts.Add(table + "|columns|" + string.Join(',', await ColumnNamesAsync(connection, table)));
            parts.Add(table + "|indexes|" + await RowsAsync(connection, $"SELECT name || ':' || \"unique\" FROM pragma_index_list('{table}') ORDER BY name;"));
            parts.Add(table + "|foreign_keys|" + await RowsAsync(connection, $"SELECT \"table\" || ':' || \"from\" || ':' || \"to\" || ':' || on_delete || ':' || on_update FROM pragma_foreign_key_list('{table}') ORDER BY id;"));
        }

        return string.Join('\n', parts);
    }

    private static async Task<string[]> TableNamesAsync(SqliteConnection connection)
    {
        var rows = await RowsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        return rows.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        var rows = await RowsAsync(connection, $"SELECT name FROM pragma_table_xinfo('{tableName}') ORDER BY cid;");
        return rows.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Task<string> ForeignKeysAsync(SqliteConnection connection, string tableName) =>
        RowsAsync(connection, $"SELECT DISTINCT \"table\" FROM pragma_foreign_key_list('{tableName}') ORDER BY \"table\";");

    private static async Task<string> RowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join('\n', rows);
    }

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

    private sealed class FailingFragment : ILedgerSchemaFragment
    {
        public int Version => 1;
        public string Name => "zz_failing";

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE partial_probe (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("Injected failure.");
        }
    }
}
