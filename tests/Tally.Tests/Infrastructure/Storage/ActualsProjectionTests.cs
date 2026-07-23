using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-ACTUALS-QUERY-CONTRACT.
public sealed class ActualsProjectionTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-actuals-" + Guid.NewGuid().ToString("N"));
    private readonly LedgerConnectionFactory factory = new(new HostArtifactProtection());
    private LedgerDb database = null!;
    private ActualsProjectionStore store = null!;

    private string accountA = null!;
    private string accountB = null!;
    private string food = null!;
    private string groceries = null!;
    private string travel = null!;
    private string personalPool = null!;
    private string companyPool = null!;
    private string instrument = null!;
    private string cardholder = null!;
    private string purchase = null!;
    private string uncategorized = null!;
    private string income = null!;
    private string transferOut = null!;
    private string transferIn = null!;
    private string refundOriginal = null!;
    private string refundCredit = null!;
    private string voided = null!;
    private string superseded = null!;
    private string replacement = null!;

    [Fact]
    public async Task Default_projection_returns_only_active_economic_facts_in_stable_order()
    {
        var items = await Project();

        Assert.Equal(8, items.Count);
        Assert.DoesNotContain(items, item => item.TransactionId is var id && (id == voided || id == superseded));
        Assert.Equal(
            items.OrderByDescending(item => item.EffectiveDate.Value).ThenByDescending(item => item.TransactionId, StringComparer.Ordinal).Select(item => item.TransactionId),
            items.Select(item => item.TransactionId));
    }

    [Fact]
    public async Task Account_and_inclusive_date_filters_compose_conjunctively()
    {
        var filter = new ActualsFilter(AccountIds: [accountA], EffectiveFrom: Date("2026-07-02"), EffectiveTo: Date("2026-07-04"));

        var items = await Project(filter);

        Assert.Equal([uncategorized, transferOut], items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Exact_category_filter_uses_current_direct_category()
    {
        var items = await Project(new(CategoryIds: [groceries], CategoryScope: ActualsCategoryScope.Exact));

        Assert.Equal([purchase, replacement], items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Subtree_category_filter_uses_current_ancestry_without_duplicates()
    {
        var items = await Project(new(CategoryIds: [food], CategoryScope: ActualsCategoryScope.Subtree));

        Assert.Equal([purchase, replacement], items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
        Assert.Equal(items.Count, items.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Categorization_filter_keeps_uncategorized_as_an_explicit_bucket()
    {
        var items = await Project(new(CategorizationStates: [TransactionCategoryState.Uncategorized]));

        Assert.Contains(items, item => item.TransactionId == uncategorized);
        Assert.All(items, item =>
        {
            Assert.Equal(TransactionCategoryState.Uncategorized, item.CategoryState);
            Assert.Null(item.CategoryId);
            Assert.Empty(item.CurrentAncestryIds);
        });
    }

    [Fact]
    public async Task Pool_id_and_assignment_state_filters_are_independent_and_conjunctive()
    {
        var assigned = await Project(new(PoolIds: [companyPool], PoolStates: [TransactionPoolState.Assigned]));
        var unassigned = await Project(new(PoolStates: [TransactionPoolState.Unassigned]));

        Assert.Equal(new[] { refundCredit, refundOriginal }.Order(StringComparer.Ordinal), assigned.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
        Assert.Contains(unassigned, item => item.TransactionId == uncategorized);
        Assert.DoesNotContain(unassigned, item => item.TransactionId == refundCredit);
    }

    [Fact]
    public async Task Known_instrument_and_cardholder_filters_match_only_explicit_attribution()
    {
        var items = await Project(new(InstrumentIds: [instrument], CardholderIds: [cardholder]));

        Assert.Equal([purchase, refundOriginal], items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unknown_instrument_and_cardholder_are_explicit_filterable_states()
    {
        var items = await Project(new(
            InstrumentStates: [TransactionKnowledgeState.Unknown],
            CardholderStates: [TransactionKnowledgeState.Unknown]));

        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.Equal(TransactionKnowledgeState.Unknown, item.InstrumentState);
            Assert.Equal(TransactionKnowledgeState.Unknown, item.CardholderState);
            Assert.Null(item.InstrumentId);
            Assert.Null(item.CardholderId);
        });
    }

    [Fact]
    public async Task Evidence_kind_filter_reads_only_current_active_evidence_links()
    {
        var items = await Project(new(EvidenceKinds: [EvidenceKind.Receipt]));

        Assert.Equal(new[] { refundCredit, uncategorized }.Order(StringComparer.Ordinal), items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
        Assert.All(items, item => Assert.Contains(EvidenceKind.Receipt, item.EvidenceKinds));
    }

    [Fact]
    public async Task Reconciliation_filter_uses_latest_explicit_transaction_state()
    {
        var items = await Project(new(ReconciliationStates: [TransactionReconciliationState.StatementReconciled]));

        Assert.Equal(purchase, Assert.Single(items).TransactionId);
    }

    [Fact]
    public async Task Every_supplied_dimension_filter_composes_conjunctively()
    {
        var items = await Project(new(
            AccountIds: [accountA],
            EffectiveFrom: Date("2026-07-01"),
            EffectiveTo: Date("2026-07-01"),
            CategoryIds: [food],
            CategoryScope: ActualsCategoryScope.Subtree,
            CategorizationStates: [TransactionCategoryState.Categorized],
            PoolIds: [personalPool],
            PoolStates: [TransactionPoolState.Assigned],
            InstrumentIds: [instrument],
            InstrumentStates: [TransactionKnowledgeState.Known],
            CardholderIds: [cardholder],
            CardholderStates: [TransactionKnowledgeState.Known],
            EvidenceKinds: [EvidenceKind.StatementRow],
            ReconciliationStates: [TransactionReconciliationState.StatementReconciled],
            RelationshipStates: [ActualsRelationshipState.None],
            LifecycleStates: [TransactionLifecycleStatus.Active]));

        Assert.Equal(purchase, Assert.Single(items).TransactionId);
    }

    [Fact]
    public async Task Relationship_role_filter_exposes_owned_transfer_principal_without_classifying_it_as_spend()
    {
        var items = await Project(new(RelationshipStates: [ActualsRelationshipState.TransferOutflow, ActualsRelationshipState.TransferInflow]));
        var calculated = ActualsCalculator.Calculate(items, ActualsGroupKind.None);

        Assert.Equal(new[] { transferIn, transferOut }.Order(StringComparer.Ordinal), items.Select(item => item.TransactionId).Order(StringComparer.Ordinal));
        Assert.Equal(0, calculated.Totals.ExternalSpend.MinorUnits);
        Assert.Equal(0, calculated.Totals.BudgetActual.MinorUnits);
    }

    [Fact]
    public async Task Refund_credit_uses_original_current_category_and_pool_only()
    {
        var item = Assert.Single(await Project(), item => item.TransactionId == refundCredit);

        Assert.Equal(ActualsRelationshipState.RefundCredit, item.RelationshipState);
        Assert.Equal(travel, item.CategoryId);
        Assert.Equal([travel], item.CurrentAncestryIds);
        Assert.Equal(companyPool, item.PoolId);
        Assert.Equal(TransactionKnowledgeState.Unknown, item.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, item.CardholderState);
    }

    [Fact]
    public async Task Explicit_lifecycle_override_returns_history_while_inactive_facts_contribute_zero()
    {
        var items = await Project(new(LifecycleStates: [TransactionLifecycleStatus.Superseded]));
        var calculated = ActualsCalculator.Calculate(items, ActualsGroupKind.None);

        Assert.Equal(superseded, Assert.Single(items).TransactionId);
        Assert.Equal(0, calculated.Totals.NetAccountMovement.MinorUnits);
        Assert.Equal(0, calculated.Totals.ExternalSpend.MinorUnits);
    }

    [Fact]
    public async Task Reparenting_changes_subtree_membership_without_rewriting_transaction_assignment()
    {
        await using (var connection = await Open())
        {
            var prior = await Scalar(connection, "SELECT parent_event_id FROM category_parent_current WHERE category_id = $id;", ("$id", groceries));
            await Execute(connection, """
                INSERT INTO category_parent_event(parent_event_id, category_id, parent_category_id, action, reason, actor, occurred_at, previous_parent_event_id)
                VALUES ($event, $category, $parent, 'reparent', 'owner correction', 'system:test', $at, $previous);
                """, ("$event", Id()), ("$category", groceries), ("$parent", travel), ("$at", At), ("$previous", prior));
        }

        var oldSubtree = await Project(new(CategoryIds: [food], CategoryScope: ActualsCategoryScope.Subtree));
        var newSubtree = await Project(new(CategoryIds: [travel], CategoryScope: ActualsCategoryScope.Subtree));

        Assert.Empty(oldSubtree);
        Assert.Contains(newSubtree, item => item.TransactionId == purchase);
        Assert.Equal(groceries, newSubtree.Single(item => item.TransactionId == purchase).CategoryId);
    }

    [Fact]
    public async Task Retired_relationships_do_not_affect_current_relationship_membership()
    {
        await using (var connection = await Open())
        {
            var relationshipId = await Scalar(connection, "SELECT relationship_id FROM financial_relationship_current WHERE source_transaction_id = $id;", ("$id", transferOut));
            await Execute(connection, """
                INSERT INTO relationship_lifecycle_event(lifecycle_event_id, relationship_id, event_type, replacement_relationship_id, reconciliation_decision_id, reason, actor_context, occurred_at)
                VALUES ($event, $relationship, 'revoked', NULL, NULL, 'owner correction', 'system:test', $at);
                """, ("$event", Id()), ("$relationship", relationshipId), ("$at", At));
        }

        var transfers = await Project(new(RelationshipStates: [ActualsRelationshipState.TransferOutflow, ActualsRelationshipState.TransferInflow]));
        var none = await Project(new(RelationshipStates: [ActualsRelationshipState.None]));

        Assert.Empty(transfers);
        Assert.Contains(none, item => item.TransactionId == transferOut);
        Assert.Contains(none, item => item.TransactionId == transferIn);
    }

    [Fact]
    public async Task No_matching_transactions_return_a_successful_empty_projection_and_zero_totals()
    {
        var items = await Project(new(EffectiveFrom: Date("2030-01-01"), EffectiveTo: Date("2030-01-31")));
        var calculated = ActualsCalculator.Calculate(items, ActualsGroupKind.None);

        Assert.Empty(items);
        Assert.Equal(ActualsTotals.Zero, calculated.Totals);
    }

    [Fact]
    public async Task Invalid_filter_is_rejected_before_storage_is_read()
    {
        var filter = new ActualsFilter(EffectiveFrom: Date("2026-07-31"), EffectiveTo: Date("2026-07-01"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Project(filter));

        Assert.Contains(ActualsFilter.InvalidError, error.Message, StringComparison.Ordinal);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        store = new(database, factory);
        await using var connection = await Open();
        await SeedAsync(connection);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<ActualsItem>> Project(ActualsFilter? filter = null) =>
        store.ProjectAsync(filter ?? new(), CancellationToken.None);

    private async Task SeedAsync(SqliteConnection connection)
    {
        accountA = Id();
        accountB = Id();
        await Account(connection, accountA, "Primary", "1111");
        await Account(connection, accountB, "Savings", "2222");

        food = Id();
        groceries = Id();
        travel = Id();
        await Category(connection, food, "Food", null);
        await Category(connection, groceries, "Groceries", food);
        await Category(connection, travel, "Travel", null);

        personalPool = Id();
        companyPool = Id();
        instrument = Id();
        cardholder = Id();
        await Catalogue(connection, "spend_pool", "pool_id", personalPool, "spend_pool", "Personal");
        await Catalogue(connection, "spend_pool", "pool_id", companyPool, "spend_pool", "Company");
        await Execute(connection, "INSERT INTO payment_instrument(instrument_id, account_id, masked_suffix, created_at) VALUES ($id, $account, '1111', $at);", ("$id", instrument), ("$account", accountA), ("$at", At));
        await Lifecycle(connection, "payment_instrument", instrument, "Primary card");
        await Catalogue(connection, "cardholder", "cardholder_id", cardholder, "cardholder", "Owner");

        purchase = await Transaction(connection, accountA, -1_000, "2026-07-01", EvidenceKind.StatementRow, groceries, personalPool, instrument, cardholder);
        uncategorized = await Transaction(connection, accountA, -200, "2026-07-02", EvidenceKind.Receipt);
        income = await Transaction(connection, accountB, 5_000, "2026-07-03", EvidenceKind.AgentCapture);
        transferOut = await Transaction(connection, accountA, -1_000, "2026-07-04", EvidenceKind.AgentCapture);
        transferIn = await Transaction(connection, accountB, 1_000, "2026-07-04", EvidenceKind.AgentCapture);
        refundOriginal = await Transaction(connection, accountA, -300, "2026-07-05", EvidenceKind.AgentCapture, travel, companyPool, instrument, cardholder);
        refundCredit = await Transaction(connection, accountA, 300, "2026-07-20", EvidenceKind.Receipt, groceries);
        voided = await Transaction(connection, accountA, -400, "2026-07-06", EvidenceKind.AgentCapture);
        superseded = await Transaction(connection, accountA, -700, "2026-07-07", EvidenceKind.AgentCapture, groceries, personalPool);
        replacement = await Transaction(connection, accountB, -750, "2026-07-08", EvidenceKind.AgentCapture, groceries, personalPool);

        await Relationship(connection, "transfer", transferOut, "transfer_outflow", transferIn, "transfer_inflow", 1_000);
        await Relationship(connection, "refund", refundOriginal, "refund_original", refundCredit, "refund_credit", 300);
        await Execute(connection, "INSERT INTO transaction_lifecycle_event VALUES ($event, $transaction, 'void', NULL, NULL, 'owner correction', 'system:test', $at);", ("$event", Id()), ("$transaction", voided), ("$at", At));
        await Execute(connection, "INSERT INTO transaction_lifecycle_event VALUES ($event, $transaction, 'superseded', $replacement, NULL, 'owner correction', 'system:test', $at);", ("$event", Id()), ("$transaction", superseded), ("$replacement", replacement), ("$at", At));
        await StatementCoverage(connection, purchase);
    }

    private static async Task Account(SqliteConnection connection, string id, string name, string suffix)
    {
        await Execute(connection, "INSERT INTO account VALUES ($id, 'Test Bank', 'cheque', 'asset', $masked, 'ZAR', $at);", ("$id", id), ("$masked", "****" + suffix), ("$at", At));
        await Lifecycle(connection, "account", id, name);
    }

    private static async Task Category(SqliteConnection connection, string id, string name, string? parent)
    {
        await Execute(connection, "INSERT INTO spend_category VALUES ($id, $at);", ("$id", id), ("$at", At));
        await Execute(connection, """
            INSERT INTO category_parent_event(parent_event_id, category_id, parent_category_id, action, reason, actor, occurred_at, previous_parent_event_id)
            VALUES ($event, $id, $parent, 'initialize', 'Initial parent', 'system:test', $at, NULL);
            """, ("$event", Id()), ("$id", id), ("$parent", parent), ("$at", At));
        await Lifecycle(connection, "category", id, name);
    }

    private static async Task Catalogue(SqliteConnection connection, string table, string idColumn, string id, string kind, string label)
    {
        await Execute(connection, $"INSERT INTO {table}({idColumn}, created_at) VALUES ($id, $at);", ("$id", id), ("$at", At));
        await Lifecycle(connection, kind, id, label);
    }

    private static Task Lifecycle(SqliteConnection connection, string kind, string id, string label) => Execute(connection, """
        INSERT INTO catalogue_lifecycle_event(lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label, normalized_label, reason, actor, occurred_at, previous_event_id)
        VALUES ($event, $kind, $id, 'create', NULL, $label, lower(trim($label)), NULL, 'system:test', $at, NULL);
        """, ("$event", Id()), ("$kind", kind), ("$id", id), ("$label", label), ("$at", At));

    private static async Task<string> Transaction(
        SqliteConnection connection,
        string accountId,
        long amountMinor,
        string date,
        EvidenceKind evidenceKind,
        string? categoryId = null,
        string? poolId = null,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var id = Id();
        await Execute(connection, """
            INSERT INTO transaction_fact(transaction_id, account_id, signed_amount_minor, currency_code, transaction_date, posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($id, $account, $amount, 'ZAR', $date, NULL, 'Owner-safe bank transaction', $at, 'system:test');
            """, ("$id", id), ("$account", accountId), ("$amount", amountMinor), ("$date", date), ("$at", At));

        var attributionRoot = Id();
        await Execute(connection, """
            INSERT INTO transaction_attribution_event VALUES ($event, $transaction, 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'Initial unknown attribution', 'system:test', $at);
            """, ("$event", attributionRoot), ("$transaction", id), ("$at", At));
        if (instrumentId is not null || cardholderId is not null)
        {
            await Execute(connection, """
                INSERT INTO transaction_attribution_event VALUES ($event, $transaction, $instrumentState, $instrument, $cardholderState, $cardholder, 'assign', $previous, NULL, NULL, 'Owner attribution', 'system:test', $at);
                """, ("$event", Id()), ("$transaction", id), ("$instrumentState", instrumentId is null ? "unknown" : "known"), ("$instrument", instrumentId),
                ("$cardholderState", cardholderId is null ? "unknown" : "known"), ("$cardholder", cardholderId), ("$previous", attributionRoot), ("$at", At));
        }

        var poolRoot = Id();
        await Execute(connection, """
            INSERT INTO pool_assignment_event VALUES ($event, $transaction, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'Initial unassigned pool', 'system:test', $at);
            """, ("$event", poolRoot), ("$transaction", id), ("$at", At));
        if (poolId is not null)
        {
            await Execute(connection, """
                INSERT INTO pool_assignment_event VALUES ($event, $transaction, 'assigned', $pool, 'assign', $previous, NULL, NULL, 'Owner pool', 'system:test', $at);
                """, ("$event", Id()), ("$transaction", id), ("$pool", poolId), ("$previous", poolRoot), ("$at", At));
        }
        if (categoryId is not null)
        {
            await Execute(connection, """
                INSERT INTO category_allocation_event VALUES ($event, $transaction, $category, 'assign', NULL, NULL, NULL, 'Owner category', 'system:test', $at);
                """, ("$event", Id()), ("$transaction", id), ("$category", categoryId), ("$at", At));
        }

        var evidenceId = Id();
        await Execute(connection, "INSERT INTO evidence_record VALUES ($evidence, $kind, $digest, NULL, NULL, 'system:test', $at);",
            ("$evidence", evidenceId), ("$kind", EvidenceValue(evidenceKind)), ("$digest", "digest-" + evidenceId), ("$at", At));
        await Execute(connection, "INSERT INTO evidence_link_event VALUES ($link, $evidence, $transaction, 'supporting', 'link', NULL, 'Initial evidence', 'system:test', $at, NULL);",
            ("$link", Id()), ("$evidence", evidenceId), ("$transaction", id), ("$at", At));
        return id;
    }

    private static Task Relationship(SqliteConnection connection, string type, string source, string sourceRole, string target, string targetRole, long amount) => Execute(connection, """
        INSERT INTO financial_relationship VALUES ($id, $type, $source, $sourceRole, $target, $targetRole, $amount, 'active', $at, 'system:test', NULL);
        """, ("$id", Id()), ("$type", type), ("$source", source), ("$sourceRole", sourceRole), ("$target", target), ("$targetRole", targetRole), ("$amount", amount), ("$at", At));

    private static async Task StatementCoverage(SqliteConnection connection, string transactionId)
    {
        var evidenceId = await Scalar(connection, """
            SELECT link.evidence_id FROM evidence_link_event AS link
            JOIN evidence_record AS evidence ON evidence.evidence_id = link.evidence_id
            WHERE link.transaction_id = $id AND evidence.kind = 'statement_row';
            """, ("$id", transactionId));
        var accountId = await Scalar(connection, "SELECT account_id FROM transaction_fact WHERE transaction_id = $id;", ("$id", transactionId));
        var scopeId = Id();
        await Execute(connection, "INSERT INTO statement_scope VALUES ($scope, $account, '2026-07-01', '2026-07-31', 'statement:test', 'completed', 'system:test', $at);",
            ("$scope", scopeId), ("$account", accountId), ("$at", At));
        await Execute(connection, "INSERT INTO statement_scope_evidence VALUES ($scope, $evidence);", ("$scope", scopeId), ("$evidence", evidenceId));
        await Execute(connection, "INSERT INTO coverage_entry VALUES ($entry, $scope, $evidence, $transaction, 'statement_reconciled', 'covered', NULL, 'system:test', $at);",
            ("$entry", Id()), ("$scope", scopeId), ("$evidence", evidenceId), ("$transaction", transactionId), ("$at", At));
    }

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static EffectiveDate Date(string value)
    {
        Assert.True(EffectiveDate.TryParse(value, out var date, out _));
        return date;
    }

    private static string Id() => LedgerId.New().ToString();

    private static string EvidenceValue(EvidenceKind kind) => kind switch
    {
        EvidenceKind.AgentCapture => "agent_capture",
        EvidenceKind.StatementRow => "statement_row",
        EvidenceKind.Receipt => "receipt",
        EvidenceKind.ExternalDocument => "external_document",
        EvidenceKind.OwnerAssertion => "owner_assertion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
