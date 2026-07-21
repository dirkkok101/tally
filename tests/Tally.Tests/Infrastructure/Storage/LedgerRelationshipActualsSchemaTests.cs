using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class LedgerRelationshipActualsSchemaTests : IAsyncLifetime
{
    private const string At = "2026-07-21T00:00:00Z";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"tally-relationship-actuals-{Guid.NewGuid():N}.db");

    // DM-LEDGER-FINANCIAL-RELATIONSHIP, DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task V001_creates_only_relationship_and_ephemeral_actuals_tables()
    {
        await using var connection = await OpenSchemaAsync();

        Assert.Equal(
            ["financial_relationship", "query_snapshot", "query_snapshot_group", "query_snapshot_item", "relationship_lifecycle_event"],
            await OwnedTableNamesAsync(connection));
    }

    // DM-LEDGER-FINANCIAL-RELATIONSHIP
    [Fact]
    public async Task Relationship_type_roles_amount_and_distinct_transactions_are_closed()
    {
        await using var connection = await OpenSchemaAsync();

        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "bad-type", "cashback", "outflow", "inflow", 100));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "bad-role", "transfer", "outflow", "inflow", 100, sourceRole: "refund_original"));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "zero", "transfer", "outflow", "inflow", 0));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "same", "transfer", "outflow", "outflow", 100));
    }

    // DM-LEDGER-FINANCIAL-RELATIONSHIP
    [Fact]
    public async Task Relationship_requires_existing_active_transactions()
    {
        await using var connection = await OpenSchemaAsync();
        await TerminalAsync(connection, "void-inflow", "inflow", "void", null);

        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "terminal", "transfer", "outflow", "inflow", 100));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "missing", "transfer", "outflow", "missing", 100));
    }

    // DM-LEDGER-FINANCIAL-RELATIONSHIP
    [Fact]
    public async Task Active_relationship_roles_are_exclusive_across_source_and_target()
    {
        await using var connection = await OpenSchemaAsync();
        await RelationshipAsync(connection, "r1", "transfer", "outflow", "inflow", 100);

        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "source-again", "refund", "outflow", "refund", 100));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipAsync(connection, "cross-role", "refund", "refund", "outflow", 100));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Deferred_replacement_retires_the_old_relationship_and_preserves_history()
    {
        await using var connection = await OpenSchemaAsync();
        await RelationshipAsync(connection, "old", "transfer", "outflow", "inflow", 100);
        await using (var transaction = connection.BeginTransaction())
        {
            await RelationshipLifecycleAsync(connection, "replace", "old", "replaced", "new", null, transaction);
            await RelationshipAsync(connection, "new", "transfer", "outflow", "inflow", 100, transaction: transaction);
            await transaction.CommitAsync();
        }

        Assert.Equal("new|active,old|retired", await ScalarStringAsync(connection, "SELECT group_concat(relationship_id || '|' || state, ',') FROM (SELECT relationship_id, state FROM financial_relationship_current ORDER BY relationship_id);"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE relationship_lifecycle_event SET reason = 'changed' WHERE lifecycle_event_id = 'replace';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM financial_relationship WHERE relationship_id = 'new';"));
    }

    // DM-LEDGER-FINANCIAL-RELATIONSHIP
    [Fact]
    public async Task Revocation_is_terminal_and_cannot_name_a_replacement()
    {
        await using var connection = await OpenSchemaAsync();
        await RelationshipAsync(connection, "r1", "refund", "outflow", "refund", 100);
        await RelationshipLifecycleAsync(connection, "revoke", "r1", "revoked", null, null);

        await Assert.ThrowsAsync<SqliteException>(() => RelationshipLifecycleAsync(connection, "again", "r1", "revoked", null, null));
        await Assert.ThrowsAsync<SqliteException>(() => RelationshipLifecycleAsync(connection, "bad", "r1", "revoked", "r1", null));
    }

    // DM-LEDGER-FINANCIAL-RELATIONSHIP, DD-LEDGER-SNAPSHOT-ACTUALS
    [Fact]
    public async Task Refund_projection_follows_the_original_current_category_and_pool()
    {
        await using var connection = await OpenSchemaAsync();
        await CategoryAllocationAsync(connection, "category-1", "outflow", "category-a", "assign", null);
        await PoolAssignmentAsync(connection, "pool-1", "outflow", "unassigned", null, "initialize", null);
        await PoolAssignmentAsync(connection, "pool-2", "outflow", "assigned", "pool", "assign", "pool-1");
        await RelationshipAsync(connection, "refund-link", "refund", "outflow", "refund", 100);

        Assert.Equal("category-a|assigned|pool", await ScalarStringAsync(connection, "SELECT category_id || '|' || pool_state || '|' || pool_id FROM refund_current_dimensions WHERE relationship_id = 'refund-link';"));

        await CategoryAllocationAsync(connection, "category-2", "outflow", "category-b", "correct", "category-1");
        await PoolAssignmentAsync(connection, "pool-3", "outflow", "unassigned", null, "correct", "pool-2");
        Assert.Equal("category-b|unassigned|", await ScalarStringAsync(connection, "SELECT category_id || '|' || pool_state || '|' || COALESCE(pool_id, '') FROM refund_current_dimensions WHERE relationship_id = 'refund-link';"));
    }

    // DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task Snapshot_requires_versioned_canonical_ephemeral_identity_and_bounded_expiry()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot");

        Assert.Equal("ephemeral", await ScalarStringAsync(connection, "SELECT persistence_scope FROM query_snapshot WHERE snapshot_id = 'snapshot';"));
        await Assert.ThrowsAsync<SqliteException>(() => SnapshotAsync(connection, "expired", expiresAt: "2026-07-20T00:00:00Z"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO query_snapshot VALUES ('durable', 'v1', 'filter', 'generation', 'hierarchy', 'durable', '{At}', '2026-07-22T00:00:00Z', 0, 0, 0);"));
    }

    // DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task Snapshot_item_preserves_ordinal_dimensions_membership_and_exact_contributions()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot");
        await SnapshotItemAsync(connection, "snapshot", 0, "outflow", "categorized", "category-a", "assigned", "pool", "unknown", null, "unknown", null, "statement_reconciled", "transfer_outflow", -100, 0, 0);

        Assert.Equal("0|category-a|pool|unknown|unknown|statement_reconciled", await ScalarStringAsync(connection, "SELECT ordinal || '|' || category_id || '|' || pool_id || '|' || instrument_state || '|' || cardholder_state || '|' || reconciliation_state FROM query_snapshot_item WHERE snapshot_id = 'snapshot';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, SnapshotItemSql("snapshot", 1, "refund", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "recorded_unreconciled", "none", 1.5, 0, 0)));
    }

    // DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task Snapshot_item_requires_explicit_unknown_uncategorized_and_unassigned_buckets()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot");

        await Assert.ThrowsAsync<SqliteException>(() => SnapshotItemAsync(connection, "snapshot", 0, "outflow", "categorized", null, "unassigned", null, "unknown", null, "unknown", null, "recorded_unreconciled", "none", -100, 100, 100));
        await Assert.ThrowsAsync<SqliteException>(() => SnapshotItemAsync(connection, "snapshot", 1, "inflow", "uncategorized", null, "assigned", null, "known", null, "unknown", null, "owner_confirmed_match", "none", 100, 0, 0));
        await SnapshotItemAsync(connection, "snapshot", 2, "refund", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "reconciliation_exception", "refund_credit", 100, -100, -100);
    }

    // DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task Snapshot_membership_is_unique_by_ordinal_and_transaction()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot");
        await SnapshotItemAsync(connection, "snapshot", 0, "outflow", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "recorded_unreconciled", "none", -100, 100, 100);

        await Assert.ThrowsAsync<SqliteException>(() => SnapshotItemAsync(connection, "snapshot", 0, "refund", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "statement_only", "none", 100, 0, 0));
        await Assert.ThrowsAsync<SqliteException>(() => SnapshotItemAsync(connection, "snapshot", 1, "outflow", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "statement_only", "none", -100, 100, 100));
    }

    // DM-LEDGER-QUERY-SNAPSHOT
    [Fact]
    public async Task Snapshot_groups_preserve_explicit_dimension_buckets_and_exact_named_totals()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot", net: -100, externalSpend: 100, budgetActual: 100);
        await SnapshotGroupAsync(connection, "snapshot", 0, "pool_category", "assigned", "pool", "categorized", "category-a", -100, 100, 100);
        await SnapshotGroupAsync(connection, "snapshot", 1, "pool_category", "unassigned", null, "uncategorized", null, 0, 0, 0);

        Assert.Equal(100L, await ScalarLongAsync(connection, "SELECT SUM(budget_actual_minor) FROM query_snapshot_group WHERE snapshot_id = 'snapshot';"));
        await Assert.ThrowsAsync<SqliteException>(() => SnapshotGroupAsync(connection, "snapshot", 2, "pool", "assigned", null, "not_applicable", null, 0, 1, 1));
        await Assert.ThrowsAsync<SqliteException>(() => SnapshotGroupAsync(connection, "snapshot", 3, "pool_category", "assigned", "pool", "categorized", "category-a", -100, 100, 100));
    }

    // DD-LEDGER-SNAPSHOT-ACTUALS
    [Fact]
    public async Task Snapshots_are_update_immutable_but_deletable_with_their_ephemeral_children()
    {
        await using var connection = await OpenSchemaAsync();
        await SnapshotAsync(connection, "snapshot");
        await SnapshotItemAsync(connection, "snapshot", 0, "outflow", "uncategorized", null, "unassigned", null, "unknown", null, "unknown", null, "recorded_unreconciled", "none", -100, 100, 100);
        await SnapshotGroupAsync(connection, "snapshot", 0, "none", "not_applicable", null, "not_applicable", null, -100, 100, 100);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE query_snapshot SET canonical_filter_hash = 'changed' WHERE snapshot_id = 'snapshot';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE query_snapshot_item SET ordinal = 1 WHERE snapshot_id = 'snapshot';"));
        await ExecuteAsync(connection, "DELETE FROM query_snapshot WHERE snapshot_id = 'snapshot';");

        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM query_snapshot_item;") + await ScalarLongAsync(connection, "SELECT COUNT(*) FROM query_snapshot_group;"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public void Duplicate_fragment_registration_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry(
            [new V001RelationshipActualsSchema(), new V001RelationshipActualsSchema()],
            [V001RelationshipActualsSchema.FragmentName]));

    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Injected_fragment_failure_rolls_back_relationships_and_snapshots()
    {
        await using var connection = await OpenAsync();
        var registry = Registry(new FailingFragment());

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
        await Registry().ApplyAsync(connection, CancellationToken.None);
        await CatalogueAndTransactionPrerequisitesAsync(connection);
        return connection;
    }

    private static LedgerSchemaFragmentRegistry Registry(ILedgerSchemaFragment? finalFragment = null)
    {
        var fragments = new List<ILedgerSchemaFragment>
        {
            new V001StorageSchema(), new V001CatalogueSchema(), new V001TransactionSchema(), new V001RelationshipActualsSchema(), new V001EvidenceReconciliationSchema()
        };
        if (finalFragment is not null)
        {
            fragments.Add(finalFragment);
        }

        return new LedgerSchemaFragmentRegistry(fragments, fragments.Select(fragment => fragment.Name));
    }

    private static async Task CatalogueAndTransactionPrerequisitesAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, $"""
            INSERT INTO account VALUES ('account', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '{At}');
            INSERT INTO spend_category VALUES ('category-a', '{At}');
            INSERT INTO spend_category VALUES ('category-b', '{At}');
            INSERT INTO category_parent_event VALUES ('category-a-parent', 'category-a', NULL, 'initialize', 'reason', 'owner', '{At}', NULL);
            INSERT INTO category_parent_event VALUES ('category-b-parent', 'category-b', NULL, 'initialize', 'reason', 'owner', '{At}', NULL);
            INSERT INTO payment_instrument VALUES ('instrument', 'account', '4321', '{At}');
            INSERT INTO cardholder VALUES ('cardholder', '{At}');
            INSERT INTO spend_pool VALUES ('pool', '{At}');
            """);
        await CatalogueLifecycleAsync(connection, "account-create", "account", "account", "Primary", "primary");
        await CatalogueLifecycleAsync(connection, "category-a-create", "category", "category-a", "Category A", "category a");
        await CatalogueLifecycleAsync(connection, "category-b-create", "category", "category-b", "Category B", "category b");
        await CatalogueLifecycleAsync(connection, "instrument-create", "payment_instrument", "instrument", "Card", "card");
        await CatalogueLifecycleAsync(connection, "cardholder-create", "cardholder", "cardholder", "Owner", "owner");
        await CatalogueLifecycleAsync(connection, "pool-create", "spend_pool", "pool", "Company", "company");
        await FactAsync(connection, "outflow", -100);
        await FactAsync(connection, "inflow", 100);
        await FactAsync(connection, "refund", 100);
    }

    private static Task CatalogueLifecycleAsync(SqliteConnection connection, string eventId, string kind, string entityId, string label, string normalized) =>
        ExecuteAsync(connection, $"INSERT INTO catalogue_lifecycle_event VALUES ('{eventId}', '{kind}', '{entityId}', 'create', NULL, '{label}', '{normalized}', 'reason', 'owner', '{At}', NULL);");

    private static Task FactAsync(SqliteConnection connection, string id, long amount) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, posting_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('{id}', 'account', {amount}, 'ZAR', '2026-07-21', NULL, 'Description', '{At}', 'owner');");

    private static Task TerminalAsync(SqliteConnection connection, string eventId, string transactionId, string action, string? replacementId) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_lifecycle_event VALUES ('{eventId}', '{transactionId}', '{action}', {Sql(replacementId)}, NULL, 'reason', 'owner', '{At}');");

    private static Task RelationshipAsync(
        SqliteConnection connection,
        string id,
        string type,
        string sourceId,
        string targetId,
        long amount,
        string? decisionId = null,
        string? sourceRole = null,
        string? targetRole = null,
        SqliteTransaction? transaction = null)
    {
        sourceRole ??= type == "transfer" ? "transfer_outflow" : "refund_original";
        targetRole ??= type == "transfer" ? "transfer_inflow" : "refund_credit";
        return ExecuteAsync(connection, $"INSERT INTO financial_relationship VALUES ('{id}', '{type}', '{sourceId}', '{sourceRole}', '{targetId}', '{targetRole}', {amount}, 'active', '{At}', 'owner', {Sql(decisionId)});", transaction);
    }

    private static Task RelationshipLifecycleAsync(SqliteConnection connection, string eventId, string relationshipId, string eventType, string? replacementId, string? decisionId, SqliteTransaction? transaction = null) =>
        ExecuteAsync(connection, $"INSERT INTO relationship_lifecycle_event VALUES ('{eventId}', '{relationshipId}', '{eventType}', {Sql(replacementId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');", transaction);

    private static Task CategoryAllocationAsync(SqliteConnection connection, string eventId, string transactionId, string categoryId, string action, string? previousId) =>
        ExecuteAsync(connection, $"INSERT INTO category_allocation_event VALUES ('{eventId}', '{transactionId}', '{categoryId}', '{action}', {Sql(previousId)}, NULL, NULL, 'reason', 'owner', '{At}');");

    private static Task PoolAssignmentAsync(SqliteConnection connection, string eventId, string transactionId, string state, string? poolId, string action, string? previousId) =>
        ExecuteAsync(connection, $"INSERT INTO pool_assignment_event VALUES ('{eventId}', '{transactionId}', '{state}', {Sql(poolId)}, '{action}', {Sql(previousId)}, NULL, NULL, 'reason', 'owner', '{At}');");

    private static Task SnapshotAsync(SqliteConnection connection, string id, string expiresAt = "2026-07-22T00:00:00Z", long net = 0, long externalSpend = 0, long budgetActual = 0) =>
        ExecuteAsync(connection, $"INSERT INTO query_snapshot VALUES ('{id}', 'v1', 'filter', 'generation', 'hierarchy', 'ephemeral', '{At}', '{expiresAt}', {net}, {externalSpend}, {budgetActual});");

    private static Task SnapshotItemAsync(
        SqliteConnection connection,
        string snapshotId,
        int ordinal,
        string transactionId,
        string categoryState,
        string? categoryId,
        string poolState,
        string? poolId,
        string instrumentState,
        string? instrumentId,
        string cardholderState,
        string? cardholderId,
        string reconciliationState,
        string relationshipState,
        long net,
        long externalSpend,
        long budgetActual) =>
        ExecuteAsync(connection, SnapshotItemSql(snapshotId, ordinal, transactionId, categoryState, categoryId, poolState, poolId, instrumentState, instrumentId, cardholderState, cardholderId, reconciliationState, relationshipState, net, externalSpend, budgetActual));

    private static string SnapshotItemSql(
        string snapshotId,
        int ordinal,
        string transactionId,
        string categoryState,
        string? categoryId,
        string poolState,
        string? poolId,
        string instrumentState,
        string? instrumentId,
        string cardholderState,
        string? cardholderId,
        string reconciliationState,
        string relationshipState,
        object net,
        object externalSpend,
        object budgetActual)
    {
        var ancestry = categoryState == "categorized" ? "[\"category-a\"]" : "[]";
        return $"INSERT INTO query_snapshot_item VALUES ('{snapshotId}', {ordinal}, '{transactionId}', '2026-07-21', '{categoryState}', {Sql(categoryId)}, '{ancestry}', '{poolState}', {Sql(poolId)}, '{instrumentState}', {Sql(instrumentId)}, '{cardholderState}', {Sql(cardholderId)}, '[\"agent_capture\"]', '{reconciliationState}', '{relationshipState}', {net}, {externalSpend}, {budgetActual});";
    }

    private static Task SnapshotGroupAsync(SqliteConnection connection, string snapshotId, int ordinal, string groupKind, string poolBucket, string? poolId, string categoryBucket, string? categoryId, long net, long externalSpend, long budgetActual) =>
        ExecuteAsync(connection, $"INSERT INTO query_snapshot_group VALUES ('{snapshotId}', {ordinal}, '{groupKind}', '{poolBucket}', {Sql(poolId)}, '{categoryBucket}', {Sql(categoryId)}, {net}, {externalSpend}, {budgetActual});");

    private static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql) =>
        Convert.ToString(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<string[]> OwnedTableNamesAsync(SqliteConnection connection)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal)
        {
            "financial_relationship", "relationship_lifecycle_event", "query_snapshot", "query_snapshot_group", "query_snapshot_item"
        };
        return (await AllTableNamesAsync(connection)).Where(owned.Contains).ToArray();
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
