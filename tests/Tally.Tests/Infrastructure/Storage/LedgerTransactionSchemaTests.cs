using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class LedgerTransactionSchemaTests : IAsyncLifetime
{
    private const string At = "2026-07-21T00:00:00Z";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"tally-transactions-{Guid.NewGuid():N}.db");

    // DM-LEDGER-TRANSACTION-FACT, DM-LEDGER-TRANSACTION-HISTORY
    [Fact]
    public async Task V001_creates_only_transaction_and_dimensional_history_tables()
    {
        await using var connection = await OpenSchemaAsync();

        Assert.Equal(
            ["category_allocation_event", "pool_assignment_event", "transaction_attribution_event", "transaction_fact", "transaction_lifecycle_event"],
            await TransactionTableNamesAsync(connection));
    }

    // DD-LEDGER-FINANCIAL-REPRESENTATION
    [Fact]
    public async Task Transaction_fact_requires_nonzero_integer_zar_and_valid_local_dates()
    {
        await using var connection = await OpenSchemaAsync();

        await Assert.ThrowsAsync<SqliteException>(() => FactAsync(connection, "zero", 0));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('real', 'account', 1.25, 'ZAR', '2026-07-21', 'Coffee', '{At}', 'owner');"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('usd', 'account', -100, 'USD', '2026-07-21', 'Coffee', '{At}', 'owner');"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('date', 'account', -100, 'ZAR', '21 July', 'Coffee', '{At}', 'owner');"));
    }

    // DM-LEDGER-TRANSACTION-FACT
    [Fact]
    public async Task Effective_date_is_derived_and_posting_date_is_optional()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -1099, "2026-07-20", null);
        await FactAsync(connection, "tx2", 500, "2026-07-19", "2026-07-21");

        Assert.Equal("2026-07-20|", await ScalarStringAsync(connection, "SELECT effective_date || '|' || COALESCE(posting_date, '') FROM transaction_fact WHERE transaction_id = 'tx1';"));
        Assert.Equal("2026-07-19|2026-07-21", await ScalarStringAsync(connection, "SELECT effective_date || '|' || posting_date FROM transaction_fact WHERE transaction_id = 'tx2';"));
    }

    // ADR-CORE-0030, DM-LEDGER-TRANSACTION-FACT
    [Fact]
    public async Task Transaction_fact_contains_no_embedded_provenance_or_dimensions()
    {
        await using var connection = await OpenSchemaAsync();

        Assert.Equal(
            ["transaction_id", "account_id", "signed_amount_minor", "currency_code", "transaction_date", "posting_date", "effective_date", "original_description", "recorded_at", "recorded_by_os_identity"],
            await ColumnNamesAsync(connection, "transaction_fact"));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Transaction_facts_are_insert_only_and_account_references_are_restricted()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE transaction_fact SET signed_amount_minor = -200 WHERE transaction_id = 'tx1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM transaction_fact WHERE transaction_id = 'tx1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM account WHERE account_id = 'account';"));
    }

    // DM-LEDGER-TRANSACTION-FACT
    [Fact]
    public async Task New_transactions_require_an_active_owned_account()
    {
        await using var connection = await OpenSchemaAsync();
        await ExecuteAsync(connection, $"INSERT INTO account VALUES ('archived-account', 'Bank', 'cheque', 'asset', '9999', 'ZAR', '{At}');");
        await LifecycleAsync(connection, "archived-create", "account", "archived-account", "create", null, "Archived", "archived", null);
        await LifecycleAsync(connection, "archived-stop", "account", "archived-account", "archive", "Archived", null, "archived", "archived-create");

        await Assert.ThrowsAsync<SqliteException>(() => FactAsync(connection, "tx1", -100, accountId: "archived-account"));
        await Assert.ThrowsAsync<SqliteException>(() => FactAsync(connection, "tx2", -100, accountId: "missing"));
    }

    // DM-LEDGER-TRANSACTION-HISTORY
    [Fact]
    public async Task Lifecycle_terminal_event_is_unique_and_replacement_is_constrained()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "original", -100);
        await FactAsync(connection, "replacement", -100);
        await LifecycleTerminalAsync(connection, "terminal", "original", "superseded", "replacement", null);

        await Assert.ThrowsAsync<SqliteException>(() => LifecycleTerminalAsync(connection, "again", "original", "void", null, null));
        await Assert.ThrowsAsync<SqliteException>(() => LifecycleTerminalAsync(connection, "self", "replacement", "superseded", "replacement", null));
        await Assert.ThrowsAsync<SqliteException>(() => LifecycleTerminalAsync(connection, "missing-replacement", "replacement", "superseded", null, null));
    }

    // DM-LEDGER-TRANSACTION-HISTORY
    [Fact]
    public async Task Category_allocation_is_a_single_append_only_current_chain()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await CategoryAllocationAsync(connection, "c1", "tx1", "category-a", "assign", null, null, null);
        await CategoryAllocationAsync(connection, "c2", "tx1", "category-b", "correct", "c1", null, null);

        Assert.Equal("category-b", await ScalarStringAsync(connection, "SELECT category_id FROM current_category_allocation WHERE transaction_id = 'tx1';"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM category_allocation_event WHERE transaction_id = 'tx1';"));
        await Assert.ThrowsAsync<SqliteException>(() => CategoryAllocationAsync(connection, "fork", "tx1", "category-a", "correct", "c1", null, null));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE category_allocation_event SET reason = 'changed' WHERE allocation_event_id = 'c1';"));
    }

    // DM-LEDGER-TRANSACTION-HISTORY
    [Fact]
    public async Task Category_allocation_requires_an_active_category()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await LifecycleAsync(connection, "category-b-archive", "category", "category-b", "archive", "Category B", null, "category b", "category-b-create");

        await Assert.ThrowsAsync<SqliteException>(() => CategoryAllocationAsync(connection, "c1", "tx1", "category-b", "assign", null, null, null));
    }

    // DM-LEDGER-PAYMENT-ATTRIBUTION
    [Fact]
    public async Task Initial_payment_attribution_is_explicitly_unknown_then_independently_assignable()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await AttributionAsync(connection, "a1", "tx1", "unknown", null, "unknown", null, "initialize", null, null, null);
        await AttributionAsync(connection, "a2", "tx1", "known", "instrument", "unknown", null, "assign", "a1", null, null);

        Assert.Equal("known|instrument|unknown|", await ScalarStringAsync(connection, "SELECT instrument_state || '|' || COALESCE(instrument_id, '') || '|' || cardholder_state || '|' || COALESCE(cardholder_id, '') FROM current_transaction_attribution WHERE transaction_id = 'tx1';"));
        await Assert.ThrowsAsync<SqliteException>(() => AttributionAsync(connection, "bad-root", "tx1", "known", "instrument", "known", "cardholder", "initialize", null, null, null));
    }

    // DM-LEDGER-PAYMENT-ATTRIBUTION
    [Fact]
    public async Task Attribution_known_state_requires_an_active_matching_identity()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await AttributionAsync(connection, "a1", "tx1", "unknown", null, "unknown", null, "initialize", null, null, null);

        await Assert.ThrowsAsync<SqliteException>(() => AttributionAsync(connection, "missing", "tx1", "known", "missing", "unknown", null, "assign", "a1", null, null));
        await Assert.ThrowsAsync<SqliteException>(() => AttributionAsync(connection, "inconsistent", "tx1", "unknown", "instrument", "unknown", null, "assign", "a1", null, null));
    }

    // DM-LEDGER-SPEND-POOL-ASSIGNMENT, A15
    [Fact]
    public async Task Initial_pool_assignment_is_explicitly_unassigned_then_has_one_current_pool()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await PoolAssignmentAsync(connection, "p1", "tx1", "unassigned", null, "initialize", null, null, null);
        await PoolAssignmentAsync(connection, "p2", "tx1", "assigned", "pool", "assign", "p1", null, null);

        Assert.Equal("assigned|pool", await ScalarStringAsync(connection, "SELECT assignment_state || '|' || pool_id FROM current_pool_assignment WHERE transaction_id = 'tx1';"));
        await Assert.ThrowsAsync<SqliteException>(() => PoolAssignmentAsync(connection, "fork", "tx1", "unassigned", null, "correct", "p1", null, null));
        await Assert.ThrowsAsync<SqliteException>(() => PoolAssignmentAsync(connection, "invalid", "tx1", "assigned", null, "correct", "p2", null, null));
    }

    // DM-LEDGER-TRANSACTION-HISTORY, DM-LEDGER-PAYMENT-ATTRIBUTION, DM-LEDGER-SPEND-POOL-ASSIGNMENT
    [Fact]
    public async Task Terminal_transactions_reject_new_dimensional_history()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "tx1", -100);
        await LifecycleTerminalAsync(connection, "void", "tx1", "void", null, null);

        await Assert.ThrowsAsync<SqliteException>(() => CategoryAllocationAsync(connection, "c1", "tx1", "category-a", "assign", null, null, null));
        await Assert.ThrowsAsync<SqliteException>(() => AttributionAsync(connection, "a1", "tx1", "unknown", null, "unknown", null, "initialize", null, null, null));
        await Assert.ThrowsAsync<SqliteException>(() => PoolAssignmentAsync(connection, "p1", "tx1", "unassigned", null, "initialize", null, null, null));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Carry_forward_and_statement_replacement_require_restricted_source_and_decision_references()
    {
        await using var connection = await OpenSchemaAsync();
        await FactAsync(connection, "source", -100);
        await FactAsync(connection, "category-target", -100);
        await FactAsync(connection, "attribution-target", -100);
        await FactAsync(connection, "pool-target", -100);
        await FactAsync(connection, "lifecycle-target", -100);
        await ExecuteAsync(connection, $"INSERT INTO evidence_record VALUES ('e1', 'owner_assertion', 'digest', NULL, NULL, 'owner', '{At}'); INSERT INTO reconciliation_decision VALUES ('decision', 'e1', NULL, 'owner_confirmed', NULL, NULL, 'owner', 0, 'reason', 'owner', '{At}', NULL);");

        await CategoryAllocationAsync(connection, "category-carry", "category-target", "category-a", "carry_forward", null, "source", "decision");
        await AttributionAsync(connection, "attribution-carry", "attribution-target", "known", "instrument", "known", "cardholder", "carry_forward", null, "source", "decision");
        await PoolAssignmentAsync(connection, "pool-carry", "pool-target", "assigned", "pool", "carry_forward", null, "source", "decision");
        await LifecycleTerminalAsync(connection, "statement-replacement", "source", "statement_authoritative_replacement", "lifecycle-target", "decision");

        await Assert.ThrowsAsync<SqliteException>(() => CategoryAllocationAsync(connection, "missing-decision", "lifecycle-target", "category-a", "carry_forward", null, "category-target", "missing"));
        await Assert.ThrowsAsync<SqliteException>(() => PoolAssignmentAsync(connection, "missing-source", "lifecycle-target", "unassigned", null, "carry_forward", null, "missing", "decision"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public void Duplicate_fragment_registration_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry(
            [new V001TransactionSchema(), new V001TransactionSchema()],
            [V001TransactionSchema.FragmentName]));

    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Injected_fragment_failure_rolls_back_all_transaction_state()
    {
        await using var connection = await OpenAsync();
        var registry = new LedgerSchemaFragmentRegistry(
            [new V001StorageSchema(), new V001CatalogueSchema(), new V001TransactionSchema(), new V001EvidenceReconciliationSchema(), new FailingFragment()],
            [V001StorageSchema.FragmentName, V001CatalogueSchema.FragmentName, V001TransactionSchema.FragmentName, V001EvidenceReconciliationSchema.FragmentName, "zz_failing"]);

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
            [new V001StorageSchema(), new V001CatalogueSchema(), new V001TransactionSchema(), new V001EvidenceReconciliationSchema()],
            [V001StorageSchema.FragmentName, V001CatalogueSchema.FragmentName, V001TransactionSchema.FragmentName, V001EvidenceReconciliationSchema.FragmentName]);
        await registry.ApplyAsync(connection, CancellationToken.None);
        await CataloguePrerequisitesAsync(connection);
        return connection;
    }

    private static async Task CataloguePrerequisitesAsync(SqliteConnection connection)
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
        await LifecycleAsync(connection, "account-create", "account", "account", "create", null, "Primary", "primary", null);
        await LifecycleAsync(connection, "category-a-create", "category", "category-a", "create", null, "Category A", "category a", null);
        await LifecycleAsync(connection, "category-b-create", "category", "category-b", "create", null, "Category B", "category b", null);
        await LifecycleAsync(connection, "instrument-create", "payment_instrument", "instrument", "create", null, "Card", "card", null);
        await LifecycleAsync(connection, "cardholder-create", "cardholder", "cardholder", "create", null, "Owner", "owner", null);
        await LifecycleAsync(connection, "pool-create", "spend_pool", "pool", "create", null, "Company", "company", null);
    }

    private static Task FactAsync(
        SqliteConnection connection,
        string id,
        long amount,
        string transactionDate = "2026-07-21",
        string? postingDate = null,
        string accountId = "account") =>
        ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, posting_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('{id}', '{accountId}', {amount}, 'ZAR', '{transactionDate}', {Sql(postingDate)}, 'Owner-safe description', '{At}', 'owner');");

    private static Task LifecycleTerminalAsync(SqliteConnection connection, string eventId, string transactionId, string action, string? replacementId, string? decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_lifecycle_event VALUES ('{eventId}', '{transactionId}', '{action}', {Sql(replacementId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');");

    private static Task CategoryAllocationAsync(SqliteConnection connection, string eventId, string transactionId, string categoryId, string action, string? previousId, string? sourceId, string? decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO category_allocation_event VALUES ('{eventId}', '{transactionId}', '{categoryId}', '{action}', {Sql(previousId)}, {Sql(sourceId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');");

    private static Task AttributionAsync(SqliteConnection connection, string eventId, string transactionId, string instrumentState, string? instrumentId, string cardholderState, string? cardholderId, string action, string? previousId, string? sourceId, string? decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_attribution_event VALUES ('{eventId}', '{transactionId}', '{instrumentState}', {Sql(instrumentId)}, '{cardholderState}', {Sql(cardholderId)}, '{action}', {Sql(previousId)}, {Sql(sourceId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');");

    private static Task PoolAssignmentAsync(SqliteConnection connection, string eventId, string transactionId, string state, string? poolId, string action, string? previousId, string? sourceId, string? decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO pool_assignment_event VALUES ('{eventId}', '{transactionId}', '{state}', {Sql(poolId)}, '{action}', {Sql(previousId)}, {Sql(sourceId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');");

    private static Task LifecycleAsync(SqliteConnection connection, string eventId, string kind, string entityId, string action, string? previousLabel, string? newLabel, string normalizedLabel, string? previousId) =>
        ExecuteAsync(connection, $"INSERT INTO catalogue_lifecycle_event VALUES ('{eventId}', '{kind}', '{entityId}', '{action}', {Sql(previousLabel)}, {Sql(newLabel)}, '{normalizedLabel}', 'reason', 'owner', '{At}', {Sql(previousId)});");

    private static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
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

    private static async Task<string[]> TransactionTableNamesAsync(SqliteConnection connection)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "account", "artifact_manifest", "cardholder", "catalogue_lifecycle_event", "category_parent_event", "idempotency_record",
            "coverage_entry", "evidence_link_event", "evidence_observation", "evidence_record", "logical_effect", "migration_metadata",
            "payment_instrument", "reconciliation_decision", "reconciliation_exception", "spend_category", "spend_pool", "statement_scope",
            "statement_scope_evidence", "store_generation"
        };
        return (await AllTableNamesAsync(connection)).Where(name => !excluded.Contains(name)).ToArray();
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
        command.CommandText = $"PRAGMA table_xinfo({tableName});";
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
