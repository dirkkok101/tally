using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Tally.Infrastructure.Storage.Migrations.V002;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

public sealed class LedgerStatementAuthoritySchemaTests : IAsyncLifetime
{
    private const string At = "2026-07-21T00:00:00Z";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"tally-statement-authority-{Guid.NewGuid():N}.db");

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task V001_upgrade_preserves_rows_and_maps_existing_decision_authority()
    {
        await using var connection = await OpenAsync();
        await V1Registry().ApplyAsync(connection, CancellationToken.None);
        await SeedDimensionsAndFactsAsync(connection);
        await EvidenceAsync(connection, "agent", "agent_capture");
        await DecisionAsync(connection, "legacy", "agent", "prior", "owner_confirmed", 0, null, decidedAt: "now");
        await LinkAsync(connection, "support", "agent", "prior", "supporting", null, null);

        await V2Registry().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal("owner_confirmed_match|owner|legacy_v1|prior", await ScalarStringAsync(connection, "SELECT disposition_detail || '|' || authority_kind || '|' || schema_origin || '|' || active_transaction_id FROM reconciliation_decision_authority WHERE decision_id = 'legacy';"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_record;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM reconciliation_decision;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_link_event;"));
        Assert.Equal(2L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task V001_dispositions_map_to_the_expanded_v002_vocabulary()
    {
        await using var connection = await OpenAsync();
        await V1Registry().ApplyAsync(connection, CancellationToken.None);
        await SeedDimensionsAndFactsAsync(connection);
        var dispositions = new[] { "deterministic_match", "statement_only", "ambiguous", "exception", "owner_confirmed", "rejected", "revoked", "replaced" };
        for (var index = 0; index < dispositions.Length; index++)
        {
            await EvidenceAsync(connection, $"e{index}", "owner_assertion");
            await DecisionAsync(connection, $"d{index}", $"e{index}", null, dispositions[index], dispositions[index] == "deterministic_match" ? 1 : 0, null, dispositions[index] == "deterministic_match" ? "policy" : null);
        }

        await V2Registry().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(
            "ambiguous,confirmed_existing,exception,owner_confirmed_match,rejected,replaced,revoked,statement_only",
            await ScalarStringAsync(connection, "SELECT group_concat(disposition_detail, ',') FROM (SELECT disposition_detail FROM reconciliation_decision_authority ORDER BY disposition_detail);"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Fresh_v002_composition_creates_the_exact_authority_inventory()
    {
        await using var connection = await OpenV2Async();

        Assert.Equal(
            ["reconciliation_decision_authority", "statement_correction", "statement_correction_relationship_event", "statement_unknown_attribution_authority"],
            await V2TableNamesAsync(connection));
        Assert.Equal(1L, await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM migration_metadata WHERE version = 2 AND fragment_name = '{V002StatementAuthoritySchema.FragmentName}';"));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task New_authority_rejects_unknown_disposition_and_mismatched_base_authority()
    {
        await using var connection = await OpenV2Async();
        await EvidenceAsync(connection, "statement", "statement_row");
        await DecisionAsync(connection, "decision", "statement", "active", "owner_confirmed", 0, null);

        await Assert.ThrowsAsync<SqliteException>(() => AuthorityAsync(connection, "decision", "unknown", null, "active", "owner", null));
        await Assert.ThrowsAsync<SqliteException>(() => AuthorityAsync(connection, "decision", "confirmed_existing", null, "active", "deterministic_policy", null));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task Statement_correction_binds_supersession_and_all_carry_forward_dimensions()
    {
        await using var connection = await OpenV2Async();

        await CreateCorrectionAsync(connection, "carry", "prior", "active", paymentResolution: "carry_forward", categoryResolution: "carry_forward");

        Assert.Equal("prior|active|carry_forward|carry_forward|statement-exact", await ScalarStringAsync(connection, "SELECT prior_transaction_id || '|' || active_transaction_id || '|' || category_resolution || '|' || payment_resolution || '|' || authority_basis FROM statement_correction WHERE correction_id = 'carry-correction';"));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task Statement_correction_can_authorize_explicit_unknown_attribution_initialization()
    {
        await using var connection = await OpenV2Async();

        await CreateCorrectionAsync(connection, "unknown", "prior", "active", paymentResolution: "unknown_initialization", categoryResolution: "uncategorized");

        Assert.Equal("unknown-attribution|prior|unknown-decision", await ScalarStringAsync(connection, "SELECT attribution_event_id || '|' || source_transaction_id || '|' || decision_id FROM statement_unknown_attribution_authority;"));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task Statement_correction_rejects_mismatched_supersession_and_authority_basis()
    {
        await using var connection = await OpenV2Async();
        var prepared = await PrepareCorrectionAsync(connection, "mismatch", "prior", "active", "carry_forward", "uncategorized");

        await Assert.ThrowsAsync<SqliteException>(() => CorrectionAsync(connection, "bad-basis", prepared, "different", prepared.SupersessionId));
        await Assert.ThrowsAsync<SqliteException>(() => CorrectionAsync(connection, "bad-lifecycle", prepared, "statement-exact", "missing"));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task Statement_correction_rejects_dimension_events_for_another_target()
    {
        await using var connection = await OpenV2Async();
        var prepared = await PrepareCorrectionAsync(connection, "dimension", "prior", "active", "carry_forward", "carry_forward");
        await CategoryCarryAsync(connection, "wrong-category", "other", "category-a", "prior", prepared.DecisionId);
        await PoolCarryAsync(connection, "wrong-pool", "other", "pool", "prior", prepared.DecisionId);

        await Assert.ThrowsAsync<SqliteException>(() => CorrectionAsync(connection, "bad-category", prepared with { CategoryEventId = "wrong-category" }, "statement-exact", prepared.SupersessionId));
        await Assert.ThrowsAsync<SqliteException>(() => CorrectionAsync(connection, "bad-pool", prepared with { PoolEventId = "wrong-pool" }, "statement-exact", prepared.SupersessionId));
    }

    // DM-LEDGER-RECONCILIATION-HISTORY, DM-LEDGER-FINANCIAL-RELATIONSHIP
    [Fact]
    public async Task Statement_correction_accepts_only_decision_bound_relationship_replacements()
    {
        await using var connection = await OpenV2Async();
        await RelationshipAsync(connection, "old-relationship", "prior", "refund", null);
        var prepared = await PrepareCorrectionAsync(connection, "relationship", "prior", "active", "carry_forward", "uncategorized");
        await CorrectionAsync(connection, "relationship-correction", prepared, "statement-exact", prepared.SupersessionId);
        await using (var transaction = connection.BeginTransaction())
        {
            await RelationshipLifecycleAsync(connection, "relationship-replacement", "old-relationship", "replaced", "new-relationship", prepared.DecisionId, transaction);
            await RelationshipAsync(connection, "new-relationship", "active", "refund", prepared.DecisionId, transaction);
            await transaction.CommitAsync();
        }

        await ExecuteAsync(connection, "INSERT INTO statement_correction_relationship_event VALUES ('relationship-correction', 0, 'relationship-replacement');");
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO statement_correction_relationship_event VALUES ('relationship-correction', 1, 'missing');"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Evidence_history_retains_agent_support_and_one_active_statement_confirmation()
    {
        await using var connection = await OpenV2Async();
        await EvidenceAsync(connection, "agent", "agent_capture");
        await EvidenceAsync(connection, "statement", "statement_row");
        await DecisionAsync(connection, "confirm", "statement", "prior", "owner_confirmed", 0, null);
        await AuthorityAsync(connection, "confirm", "owner_confirmed_match", null, "prior", "owner", "statement-row");
        await LinkAsync(connection, "agent-support", "agent", "prior", "supporting", null, null);
        await LinkAsync(connection, "statement-old", "statement", "prior", "confirming", "confirm", null);
        await LinkAsync(connection, "statement-active", "statement", "active", "confirming", "confirm", "statement-old", "replace");

        Assert.Equal(3L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_link_history_v2;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_link_history_v2 WHERE evidence_kind = 'statement_row' AND role = 'confirming' AND is_active = 1;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_link_history_v2 WHERE evidence_kind = 'agent_capture' AND role = 'supporting' AND is_active = 1;"));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task V002_authority_and_correction_history_are_immutable()
    {
        await using var connection = await OpenV2Async();
        await CreateCorrectionAsync(connection, "immutable", "prior", "active", "carry_forward", "uncategorized");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE reconciliation_decision_authority SET statement_authority_basis = 'changed' WHERE decision_id = 'immutable-decision';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM statement_correction WHERE correction_id = 'immutable-correction';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM reconciliation_decision WHERE decision_id = 'immutable-decision';"));
    }

    // ADR-CORE-0030, C18
    [Fact]
    public async Task V002_columns_are_provider_and_transport_neutral()
    {
        await using var connection = await OpenV2Async();
        var columns = string.Join(',', await ColumnNamesAsync(connection, "statement_correction")) + ',' + string.Join(',', await ColumnNamesAsync(connection, "reconciliation_decision_authority"));

        Assert.DoesNotContain("email", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mime", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient", columns, StringComparison.OrdinalIgnoreCase);
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public void Duplicate_v002_fragment_registration_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new LedgerSchemaFragmentRegistry(
            [new V002StatementAuthoritySchema(), new V002StatementAuthoritySchema()],
            [V002StatementAuthoritySchema.FragmentName]));

    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact]
    public async Task Failed_v002_upgrade_rolls_back_without_touching_v001()
    {
        await using var connection = await OpenAsync();
        await V1Registry().ApplyAsync(connection, CancellationToken.None);
        await SeedDimensionsAndFactsAsync(connection);
        await EvidenceAsync(connection, "retained", "owner_assertion");
        var registry = V2Registry(new FailingV2Fragment());

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyAsync(connection, CancellationToken.None));

        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evidence_record WHERE evidence_id = 'retained';"));
        Assert.False(await TableExistsAsync(connection, "statement_correction"));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Reopening_v002_is_idempotent_and_preserves_observable_counts()
    {
        await using var connection = await OpenV2Async();
        await CreateCorrectionAsync(connection, "reopen", "prior", "active", "carry_forward", "uncategorized");
        var before = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM statement_correction;");

        await V2Registry().ApplyAsync(connection, CancellationToken.None);

        Assert.Equal(before, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM statement_correction;"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM migration_metadata WHERE version = 2 AND fragment_name = 'statement_authority';"));
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

    private async Task<SqliteConnection> OpenV2Async()
    {
        var connection = await OpenAsync();
        await V2Registry().ApplyAsync(connection, CancellationToken.None);
        await SeedDimensionsAndFactsAsync(connection);
        return connection;
    }

    private static LedgerSchemaFragmentRegistry V1Registry() => new(V1Fragments(), V1Fragments().Select(fragment => fragment.Name));

    private static LedgerSchemaFragmentRegistry V2Registry(ILedgerSchemaFragment? finalFragment = null)
    {
        var fragments = V1Fragments();
        fragments.Add(new V002StatementAuthoritySchema());
        if (finalFragment is not null)
        {
            fragments.Add(finalFragment);
        }

        return new LedgerSchemaFragmentRegistry(fragments, fragments.Select(fragment => fragment.Name));
    }

    private static List<ILedgerSchemaFragment> V1Fragments() =>
        [new V001StorageSchema(), new V001CatalogueSchema(), new V001TransactionSchema(), new V001RelationshipActualsSchema(), new V001EvidenceReconciliationSchema()];

    private static async Task SeedDimensionsAndFactsAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, $"""
            INSERT INTO account VALUES ('account', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '{At}');
            INSERT INTO spend_category VALUES ('category-a', '{At}');
            INSERT INTO category_parent_event VALUES ('category-parent', 'category-a', NULL, 'initialize', 'reason', 'owner', '{At}', NULL);
            INSERT INTO payment_instrument VALUES ('instrument', 'account', '4321', '{At}');
            INSERT INTO cardholder VALUES ('cardholder', '{At}');
            INSERT INTO spend_pool VALUES ('pool', '{At}');
            """);
        await CatalogueLifecycleAsync(connection, "account-create", "account", "account", "Primary", "primary");
        await CatalogueLifecycleAsync(connection, "category-create", "category", "category-a", "Category", "category");
        await CatalogueLifecycleAsync(connection, "instrument-create", "payment_instrument", "instrument", "Card", "card");
        await CatalogueLifecycleAsync(connection, "cardholder-create", "cardholder", "cardholder", "Owner", "owner");
        await CatalogueLifecycleAsync(connection, "pool-create", "spend_pool", "pool", "Company", "company");
        await FactAsync(connection, "prior", -100);
        await FactAsync(connection, "active", -100);
        await FactAsync(connection, "other", -100);
        await FactAsync(connection, "refund", 100);
    }

    private static async Task CreateCorrectionAsync(SqliteConnection connection, string prefix, string priorId, string activeId, string paymentResolution, string categoryResolution)
    {
        var prepared = await PrepareCorrectionAsync(connection, prefix, priorId, activeId, paymentResolution, categoryResolution);
        await CorrectionAsync(connection, $"{prefix}-correction", prepared, "statement-exact", prepared.SupersessionId);
    }

    private static async Task<PreparedCorrection> PrepareCorrectionAsync(SqliteConnection connection, string prefix, string priorId, string activeId, string paymentResolution, string categoryResolution)
    {
        var evidenceId = $"{prefix}-evidence";
        var priorDecisionId = $"{prefix}-prior-decision";
        var decisionId = $"{prefix}-decision";
        var supersessionId = $"{prefix}-supersession";
        var categoryEventId = categoryResolution == "carry_forward" ? $"{prefix}-category" : null;
        var poolEventId = $"{prefix}-pool";
        var attributionEventId = paymentResolution == "carry_forward" ? $"{prefix}-attribution" : $"{prefix}-attribution";
        await EvidenceAsync(connection, evidenceId, "statement_row");
        await DecisionAsync(connection, priorDecisionId, evidenceId, priorId, "owner_confirmed", 0, null);
        await AuthorityAsync(connection, priorDecisionId, "owner_confirmed_match", null, priorId, "owner", "owner-review");
        await DecisionAsync(connection, decisionId, evidenceId, activeId, "replaced", 0, priorDecisionId);
        await AuthorityAsync(connection, decisionId, "corrected_from_statement", priorId, activeId, "owner", "statement-exact");
        if (categoryEventId is not null)
        {
            await CategoryCarryAsync(connection, categoryEventId, activeId, "category-a", priorId, decisionId);
        }

        await PoolCarryAsync(connection, poolEventId, activeId, "pool", priorId, decisionId);
        if (paymentResolution == "carry_forward")
        {
            await AttributionCarryAsync(connection, attributionEventId, activeId, priorId, decisionId);
        }
        else
        {
            await AttributionInitializeUnknownAsync(connection, attributionEventId, activeId);
            await ExecuteAsync(connection, $"INSERT INTO statement_unknown_attribution_authority VALUES ('{attributionEventId}', '{priorId}', '{decisionId}', 'account changed', 'owner', '{At}');");
        }

        await ExecuteAsync(connection, $"INSERT INTO transaction_lifecycle_event VALUES ('{supersessionId}', '{priorId}', 'statement_authoritative_replacement', '{activeId}', '{decisionId}', 'statement authority', 'owner', '{At}');");
        return new PreparedCorrection(decisionId, priorDecisionId, priorId, activeId, supersessionId, categoryResolution, categoryEventId, poolEventId, paymentResolution, attributionEventId);
    }

    private static Task CorrectionAsync(SqliteConnection connection, string correctionId, PreparedCorrection value, string authorityBasis, string supersessionId) =>
        ExecuteAsync(connection, $"INSERT INTO statement_correction VALUES ('{correctionId}', '{value.DecisionId}', '{value.PriorId}', '{value.ActiveId}', '{supersessionId}', '{value.CategoryResolution}', {Sql(value.CategoryEventId)}, '{value.PoolEventId}', '{value.PaymentResolution}', '{value.AttributionEventId}', '{authorityBasis}', '{value.PreviousDecisionId}', 'reason', 'owner', '{At}');");

    private static Task AuthorityAsync(SqliteConnection connection, string decisionId, string disposition, string? priorId, string? activeId, string authorityKind, string? basis) =>
        ExecuteAsync(connection, $"INSERT INTO reconciliation_decision_authority VALUES ('{decisionId}', '{disposition}', {Sql(priorId)}, {Sql(activeId)}, '{authorityKind}', {Sql(basis)}, 'v2', '{At}');");

    private static Task EvidenceAsync(SqliteConnection connection, string id, string kind) =>
        ExecuteAsync(connection, $"INSERT INTO evidence_record VALUES ('{id}', '{kind}', 'digest-{id}', NULL, NULL, 'owner', '{At}');");

    private static Task DecisionAsync(SqliteConnection connection, string id, string evidenceId, string? transactionId, string disposition, int deterministic, string? previousId, string? policyId = null, string decidedAt = At) =>
        ExecuteAsync(connection, $"INSERT INTO reconciliation_decision VALUES ('{id}', '{evidenceId}', {Sql(transactionId)}, '{disposition}', {Sql(policyId)}, {Sql(policyId is null ? null : "v1")}, 'basis', {deterministic}, 'reason', 'owner', '{decidedAt}', {Sql(previousId)});");

    private static Task LinkAsync(SqliteConnection connection, string id, string evidenceId, string transactionId, string role, string? decisionId, string? previousId, string action = "link") =>
        ExecuteAsync(connection, $"INSERT INTO evidence_link_event VALUES ('{id}', '{evidenceId}', '{transactionId}', '{role}', '{action}', {Sql(decisionId)}, 'reason', 'owner', '{At}', {Sql(previousId)});");

    private static Task CategoryCarryAsync(SqliteConnection connection, string id, string targetId, string categoryId, string sourceId, string decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO category_allocation_event VALUES ('{id}', '{targetId}', '{categoryId}', 'carry_forward', NULL, '{sourceId}', '{decisionId}', 'reason', 'owner', '{At}');");

    private static Task PoolCarryAsync(SqliteConnection connection, string id, string targetId, string poolId, string sourceId, string decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO pool_assignment_event VALUES ('{id}', '{targetId}', 'assigned', '{poolId}', 'carry_forward', NULL, '{sourceId}', '{decisionId}', 'reason', 'owner', '{At}');");

    private static Task AttributionCarryAsync(SqliteConnection connection, string id, string targetId, string sourceId, string decisionId) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_attribution_event VALUES ('{id}', '{targetId}', 'known', 'instrument', 'known', 'cardholder', 'carry_forward', NULL, '{sourceId}', '{decisionId}', 'reason', 'owner', '{At}');");

    private static Task AttributionInitializeUnknownAsync(SqliteConnection connection, string id, string targetId) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_attribution_event VALUES ('{id}', '{targetId}', 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'reason', 'owner', '{At}');");

    private static Task RelationshipAsync(SqliteConnection connection, string id, string originalId, string refundId, string? decisionId, SqliteTransaction? transaction = null) =>
        ExecuteAsync(connection, $"INSERT INTO financial_relationship VALUES ('{id}', 'refund', '{originalId}', 'refund_original', '{refundId}', 'refund_credit', 100, 'active', '{At}', 'owner', {Sql(decisionId)});", transaction);

    private static Task RelationshipLifecycleAsync(SqliteConnection connection, string id, string relationshipId, string action, string? replacementId, string? decisionId, SqliteTransaction transaction) =>
        ExecuteAsync(connection, $"INSERT INTO relationship_lifecycle_event VALUES ('{id}', '{relationshipId}', '{action}', {Sql(replacementId)}, {Sql(decisionId)}, 'reason', 'owner', '{At}');", transaction);

    private static Task CatalogueLifecycleAsync(SqliteConnection connection, string eventId, string kind, string entityId, string label, string normalized) =>
        ExecuteAsync(connection, $"INSERT INTO catalogue_lifecycle_event VALUES ('{eventId}', '{kind}', '{entityId}', 'create', NULL, '{label}', '{normalized}', 'reason', 'owner', '{At}', NULL);");

    private static Task FactAsync(SqliteConnection connection, string id, long amount) =>
        ExecuteAsync(connection, $"INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, posting_date, original_description, recorded_at, recorded_by_os_identity) VALUES ('{id}', 'account', {amount}, 'ZAR', '2026-07-21', NULL, 'Description', '{At}', 'owner');");

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

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';") == 1;

    private static async Task<string[]> V2TableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('reconciliation_decision_authority', 'statement_correction', 'statement_correction_relationship_event', 'statement_unknown_attribution_authority') ORDER BY name;";
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

    private sealed record PreparedCorrection(
        string DecisionId,
        string PreviousDecisionId,
        string PriorId,
        string ActiveId,
        string SupersessionId,
        string CategoryResolution,
        string? CategoryEventId,
        string PoolEventId,
        string PaymentResolution,
        string AttributionEventId);

    private sealed class FailingV2Fragment : ILedgerSchemaFragment
    {
        public int Version => 2;
        public string Name => "zz_failing";

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE injected_v2_rollback_probe (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("Injected V002 migration failure.");
        }
    }
}
