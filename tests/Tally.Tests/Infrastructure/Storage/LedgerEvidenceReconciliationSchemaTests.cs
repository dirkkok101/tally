using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class LedgerEvidenceReconciliationSchemaTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"tally-evidence-{Guid.NewGuid():N}.db");

    // DM-LEDGER-EVIDENCE-RECORD-LINK, DM-LEDGER-RECONCILIATION-HISTORY
    [Fact] public async Task V001_creates_the_evidence_reconciliation_inventory() { await using var connection = await OpenSchemaAsync(); Assert.Equal(["coverage_entry", "evidence_link_event", "evidence_observation", "evidence_record", "reconciliation_decision", "reconciliation_exception", "statement_scope", "statement_scope_evidence"], await TableNamesAsync(connection)); }
    // DD-LEDGER-RECONCILIATION-CONTRACT
    [Fact] public async Task Candidate_projections_are_not_persisted() { await using var connection = await OpenSchemaAsync(); Assert.DoesNotContain("candidate_projection", await TableNamesAsync(connection)); }
    // DD-LEDGER-EMBEDDED-STORAGE, DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Evidence_record_uses_only_the_privacy_allowlist() { await using var connection = await OpenSchemaAsync(); Assert.Equal(["evidence_id", "kind", "logical_identity_digest", "opaque_external_reference", "content_fingerprint", "recorded_by", "recorded_at"], await ColumnNamesAsync(connection, "evidence_record")); }
    // DD-LEDGER-EMBEDDED-STORAGE, DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Evidence_observation_uses_only_normalized_provider_neutral_fields() { await using var connection = await OpenSchemaAsync(); Assert.Equal(["evidence_id", "account_id", "signed_amount_minor", "currency_code", "transaction_date", "posting_date", "instrument_id", "cardholder_id", "description_fingerprint"], await ColumnNamesAsync(connection, "evidence_observation")); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Duplicate_logical_evidence_is_rejected_even_when_content_changes() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => EvidenceAsync(connection, "e2", "digest", "changed")); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Evidence_kind_is_closed() { await using var connection = await OpenSchemaAsync(); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO evidence_record VALUES ('e1', 'provider_row', 'digest', NULL, NULL, 'owner', 'now');")); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Evidence_observation_rejects_unknown_dimensions_and_non_zar_currency() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO evidence_observation (evidence_id, account_id) VALUES ('e1', 'missing');")); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO evidence_observation (evidence_id, currency_code) VALUES ('e1', 'USD');")); }
    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact] public async Task Evidence_history_cannot_be_replaced() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE evidence_record SET content_fingerprint = 'changed' WHERE evidence_id = 'e1';")); }
    // DD-LEDGER-RECONCILIATION-CONTRACT
    [Fact] public async Task Decision_disposition_is_closed() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => DecisionAsync(connection, "d1", "e1", "unknown", null)); }
    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact] public async Task Decision_predecessor_must_exist_for_the_same_evidence() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "one", "first"); await EvidenceAsync(connection, "e2", "two", "second"); await DecisionAsync(connection, "d1", "e1", "owner_confirmed", null); await Assert.ThrowsAsync<SqliteException>(() => DecisionAsync(connection, "d2", "e2", "replaced", "d1")); }
    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact] public async Task Decision_history_cannot_be_replaced() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await DecisionAsync(connection, "d1", "e1", "owner_confirmed", null); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE reconciliation_decision SET reason = 'changed' WHERE decision_id = 'd1';")); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Confirming_link_requires_a_decision() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => LinkAsync(connection, "l1", "e1", "tx1", "confirming", "link", null, null)); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Decisions_and_links_reject_unknown_transactions() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO reconciliation_decision VALUES ('d1', 'e1', 'missing', 'owner_confirmed', NULL, NULL, 'exact', 0, 'reason', 'owner', 'now', NULL);")); await DecisionAsync(connection, "d2", "e1", "owner_confirmed", null); await Assert.ThrowsAsync<SqliteException>(() => LinkAsync(connection, "l1", "e1", "missing", "confirming", "link", "d2", null)); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Confirming_link_requires_statement_row_evidence() { await using var connection = await OpenSchemaAsync(); await ExecuteAsync(connection, "INSERT INTO evidence_record VALUES ('e1', 'receipt', 'digest', NULL, 'first', 'owner', 'now');"); await DecisionAsync(connection, "d1", "e1", "owner_confirmed", null); await Assert.ThrowsAsync<SqliteException>(() => LinkAsync(connection, "l1", "e1", "tx1", "confirming", "link", "d1", null)); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Confirming_link_has_only_one_active_target() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await DecisionAsync(connection, "d1", "e1", "owner_confirmed", null); await LinkAsync(connection, "l1", "e1", "tx1", "confirming", "link", "d1", null); await Assert.ThrowsAsync<SqliteException>(() => LinkAsync(connection, "l2", "e1", "tx2", "confirming", "link", "d1", null)); Assert.Equal("tx1", await ScalarStringAsync(connection, "SELECT transaction_id FROM evidence_active_confirming_target;")); }
    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact] public async Task Confirming_replacement_preserves_history_and_changes_current_target() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "digest", "first"); await DecisionAsync(connection, "d1", "e1", "owner_confirmed", null); await LinkAsync(connection, "l1", "e1", "tx1", "confirming", "link", "d1", null); await LinkAsync(connection, "l2", "e1", "tx2", "confirming", "replace", "d1", "l1"); Assert.Equal("tx2", await ScalarStringAsync(connection, "SELECT transaction_id FROM evidence_active_confirming_target;")); Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_link_event;")); }
    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact] public async Task Statement_scope_status_and_period_are_structurally_validated() { await using var connection = await OpenSchemaAsync(); await Assert.ThrowsAsync<SqliteException>(() => ScopeAsync(connection, "s1", "completed", "2026-02-01", "2026-01-01")); await Assert.ThrowsAsync<SqliteException>(() => ScopeAsync(connection, "s2", "archived", "2026-01-01", "2026-01-31")); }
    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact] public async Task Statement_scope_rejects_an_unknown_account() { await using var connection = await OpenSchemaAsync(); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO statement_scope VALUES ('s1', 'missing', '2026-01-01', '2026-01-31', 'manifest', 'open', 'owner', 'now');")); }
    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact] public async Task Coverage_must_reference_evidence_inside_its_statement_scope() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "one", "first"); await EvidenceAsync(connection, "e2", "two", "second"); await ScopeAsync(connection, "s1", "open", "2026-01-01", "2026-01-31"); await ExecuteAsync(connection, "INSERT INTO statement_scope_evidence VALUES ('s1', 'e1');"); await Assert.ThrowsAsync<SqliteException>(() => CoverageAsync(connection, "c1", "s1", "e2", null)); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Restrict_relationships_block_parent_deletion() { await using var connection = await OpenSchemaAsync(); await EvidenceAsync(connection, "e1", "one", "first"); await ScopeAsync(connection, "s1", "open", "2026-01-01", "2026-01-31"); await ExecuteAsync(connection, "INSERT INTO statement_scope_evidence VALUES ('s1', 'e1');"); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM evidence_record WHERE evidence_id = 'e1';")); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM statement_scope WHERE scope_id = 's1';")); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Injected_fragment_failure_rolls_back_evidence_schema() { await using var connection = await OpenAsync(); await ExecuteAsync(connection, "CREATE TABLE migration_metadata (version INTEGER NOT NULL, fragment_name TEXT NOT NULL, applied_at TEXT NOT NULL, PRIMARY KEY (version, fragment_name));"); var registry = new LedgerSchemaFragmentRegistry([new V001EvidenceReconciliationSchema(), new FailingFragment()], [V001EvidenceReconciliationSchema.FragmentName, "zz_failing"]); await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyAsync(connection, CancellationToken.None)); Assert.Empty(await TableNamesAsync(connection)); }
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task Duplicate_fragment_registration_is_rejected() => Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry([new V001EvidenceReconciliationSchema(), new V001EvidenceReconciliationSchema()], [V001EvidenceReconciliationSchema.FragmentName]));
    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact] public async Task V001_fragment_is_idempotent_after_successful_composition() { await using var connection = await OpenAsync(); var registry = new LedgerSchemaFragmentRegistry([new V001StorageSchema(), new V001EvidenceReconciliationSchema()], [V001StorageSchema.FragmentName, V001EvidenceReconciliationSchema.FragmentName]); await registry.ApplyAsync(connection, CancellationToken.None); await registry.ApplyAsync(connection, CancellationToken.None); Assert.Equal(1L, await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM migration_metadata WHERE fragment_name = '{V001EvidenceReconciliationSchema.FragmentName}';")); }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (File.Exists(databasePath)) { File.Delete(databasePath); } return Task.CompletedTask; }

    private async Task<SqliteConnection> OpenAsync() { var connection = new SqliteConnection($"Data Source={databasePath}"); await connection.OpenAsync(); await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;"); return connection; }
    private async Task<SqliteConnection> OpenSchemaAsync() { var connection = await OpenAsync(); await CreatePrerequisiteTablesAsync(connection); await new LedgerSchemaFragmentRegistry([new V001StorageSchema(), new V001EvidenceReconciliationSchema()], [V001StorageSchema.FragmentName, V001EvidenceReconciliationSchema.FragmentName]).ApplyAsync(connection, CancellationToken.None); return connection; }
    private static async Task CreatePrerequisiteTablesAsync(SqliteConnection connection) { await ExecuteAsync(connection, "CREATE TABLE account (account_id TEXT PRIMARY KEY); CREATE TABLE payment_instrument (instrument_id TEXT PRIMARY KEY); CREATE TABLE cardholder (cardholder_id TEXT PRIMARY KEY); CREATE TABLE transaction_fact (transaction_id TEXT PRIMARY KEY); INSERT INTO account VALUES ('account'); INSERT INTO payment_instrument VALUES ('instrument'); INSERT INTO cardholder VALUES ('cardholder'); INSERT INTO transaction_fact VALUES ('tx1'); INSERT INTO transaction_fact VALUES ('tx2');"); }
    private static Task EvidenceAsync(SqliteConnection connection, string id, string digest, string content) => ExecuteAsync(connection, $"INSERT INTO evidence_record VALUES ('{id}', 'statement_row', '{digest}', NULL, '{content}', 'owner', 'now');");
    private static Task DecisionAsync(SqliteConnection connection, string id, string evidenceId, string disposition, string? predecessor) => ExecuteAsync(connection, $"INSERT INTO reconciliation_decision VALUES ('{id}', '{evidenceId}', NULL, '{disposition}', NULL, NULL, 'exact', 0, 'reason', 'owner', 'now', {Sql(predecessor)});");
    private static Task LinkAsync(SqliteConnection connection, string id, string evidenceId, string transactionId, string role, string action, string? decisionId, string? predecessor) => ExecuteAsync(connection, $"INSERT INTO evidence_link_event VALUES ('{id}', '{evidenceId}', '{transactionId}', '{role}', '{action}', {Sql(decisionId)}, 'reason', 'owner', 'now', {Sql(predecessor)});");
    private static Task ScopeAsync(SqliteConnection connection, string id, string status, string start, string end) => ExecuteAsync(connection, $"INSERT INTO statement_scope VALUES ('{id}', 'account', '{start}', '{end}', 'manifest', '{status}', 'owner', 'now');");
    private static Task CoverageAsync(SqliteConnection connection, string id, string scopeId, string evidenceId, string? decisionId) => ExecuteAsync(connection, $"INSERT INTO coverage_entry VALUES ('{id}', '{scopeId}', '{evidenceId}', NULL, 'statement_only', 'reason', {Sql(decisionId)}, 'owner', 'now');");
    private static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";
    private static async Task ExecuteAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!; }
    private static async Task<string[]> TableNamesAsync(SqliteConnection connection) { await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('account', 'artifact_manifest', 'cardholder', 'idempotency_record', 'logical_effect', 'migration_metadata', 'payment_instrument', 'store_generation', 'transaction_fact') ORDER BY name;"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) { names.Add(reader.GetString(0)); } return names.ToArray(); }
    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string tableName) { await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({tableName});"; await using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) { names.Add(reader.GetString(1)); } return names.ToArray(); }

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
