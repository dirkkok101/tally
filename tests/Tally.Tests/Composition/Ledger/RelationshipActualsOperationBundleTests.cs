using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Features.Ledger.Actuals;
using Tally.Features.Ledger.Relationships;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;
using Xunit;

namespace Tally.Tests.Composition.Ledger;

[SupportedOSPlatform("linux")]
public sealed class RelationshipActualsOperationBundleTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00Z";
    private static readonly string[] ExpectedOperationIds =
    [
        "ledger.actuals.query",
        "ledger.refund.confirm",
        "ledger.refund.replace",
        "ledger.refund.revoke",
        "ledger.relationship.get",
        "ledger.transfer.confirm",
        "ledger.transfer.replace",
        "ledger.transfer.revoke"
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-relationship-actuals-bundle-" + Guid.NewGuid().ToString("N"));
    private readonly LedgerConnectionFactory factory = new(new HostArtifactProtection());
    private LedgerDb database = null!;
    private ActualsOperationModule actuals = null!;

    [Fact]
    public void Contract_contains_exactly_the_eight_relationship_and_actuals_operations()
    {
        var descriptors = Bundle().Descriptors;

        Assert.Equal(ExpectedOperationIds, descriptors.Select(descriptor => descriptor.OperationId));
        Assert.Equal(8, descriptors.Select(descriptor => descriptor.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, descriptors.Select(descriptor => descriptor.CliPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_descriptor_retains_its_canonical_source_schema_byte_for_byte()
    {
        var source = Source();
        var sourceJson = JsonSerializer.Serialize(
            source.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);
        var bundleJson = JsonSerializer.Serialize(
            Bundle(source).Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);

        Assert.Equal(sourceJson, bundleJson);
    }

    [Fact]
    public void Actuals_contract_retains_hierarchy_dimensions_reconciliation_and_exact_totals()
    {
        var descriptor = Assert.Single(Bundle().Descriptors, item => item.OperationId == ActualsOperationModule.OperationId);
        var filterProperties = ActualsJsonContext.Default.ActualsFilterInput.Properties.Select(property => property.Name).ToArray();
        var itemProperties = ActualsJsonContext.Default.ActualsPageItem.Properties.Select(property => property.Name).ToArray();
        var resultProperties = ActualsJsonContext.Default.ActualsQueryResult.Properties.Select(property => property.Name).ToArray();

        Assert.Equal(typeof(QueryActualsInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(ActualsQueryResult), descriptor.ResultTypeInfo.Type);
        Assert.Contains("categoryScope", filterProperties);
        Assert.Contains("categorizationStates", filterProperties);
        Assert.Contains("poolStates", filterProperties);
        Assert.Contains("instrumentStates", filterProperties);
        Assert.Contains("cardholderStates", filterProperties);
        Assert.Contains("reconciliationStates", filterProperties);
        Assert.Contains("relationshipStates", filterProperties);
        Assert.Contains("frozenAncestryIds", itemProperties);
        Assert.Contains("reconciliationState", itemProperties);
        Assert.Contains("totals", resultProperties);
        Assert.Contains("groups", resultProperties);
    }

    [Fact]
    public void Every_descriptor_is_bound_to_one_explicit_module_handler()
    {
        var registry = OperationRegistry.Create();

        foreach (var descriptor in Bundle().Descriptors)
        {
            var handler = descriptor.HandlerFactory(LedgerServices.Create(), registry);

            Assert.EndsWith("OperationHandler", handler.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Theory]
    [InlineData("transfers")]
    [InlineData("refunds")]
    [InlineData("relationshipLifecycle")]
    [InlineData("actuals")]
    public void Missing_module_is_rejected_before_dispatch(string missing)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Bundle(missing: missing));

        Assert.Equal(missing, exception.ParamName);
    }

    [Fact]
    public void Missing_descriptor_is_rejected_before_dispatch()
    {
        var source = Source().Where(descriptor => descriptor.OperationId != "ledger.refund.confirm").ToArray();

        Assert.Contains("exact eight-operation", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_operation_id_is_rejected_before_dispatch()
    {
        var source = Source().ToList();
        source.Add(source[0]);

        Assert.Contains("duplicate operation ID", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_cli_path_is_rejected_before_dispatch()
    {
        var source = Source();
        source[1] = source[1] with { CliPath = source[0].CliPath };

        Assert.Contains("duplicate CLI path", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void Incompatible_contract_version_is_rejected_before_dispatch(string bound)
    {
        var source = Source();
        source[0] = bound == "minimum"
            ? source[0] with { MinimumContractVersion = "2.0" }
            : source[0] with { MaximumContractVersion = "2.0" };

        Assert.Contains("incompatible contract version", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flattened_actuals_filter_is_rejected_before_dispatch()
    {
        var source = Source();
        var index = Array.FindIndex(source, descriptor => descriptor.OperationId == ActualsOperationModule.OperationId);
        source[index] = source[index] with { RequestTypeInfo = LedgerJsonContext.Default.EmptyInput };

        Assert.Contains("dimensional actuals contract", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agentmail")]
    [InlineData("whatsapp")]
    [InlineData("provider")]
    public void Provider_or_transport_vocabulary_is_rejected_before_dispatch(string forbidden)
    {
        var source = Source();
        source[0] = source[0] with { Example = source[0].Example + " " + forbidden };

        Assert.Contains("provider or transport", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_store_conserves_all_up_direct_subtree_pool_and_matrix_actuals_through_history()
    {
        var fixture = await SeedConservationFixture();

        var all = await Query(ActualsGrouping.None);
        var direct = await Query(ActualsGrouping.CategoryDirect);
        var subtree = await Query(ActualsGrouping.CategorySubtree);
        var pool = await Query(ActualsGrouping.Pool);
        var matrix = await Query(ActualsGrouping.PoolCategory);

        Assert.Equal(9, all.TotalCount);
        AssertTotals(all.Totals, "-27", "27", "27");
        Assert.All([direct, subtree, pool, matrix], result => Assert.Equal(all.Totals, result.Totals));
        Assert.Equal(all.Totals.BudgetActual, Sum(direct.Groups.Select(group => group.Totals.BudgetActual)));
        Assert.Equal(all.Totals.BudgetActual, Sum(pool.Groups.Select(group => group.Totals.BudgetActual)));
        Assert.Equal(all.Totals.BudgetActual, Sum(matrix.Groups.Select(group => group.Totals.BudgetActual)));

        Assert.Equal("21", direct.Groups.Single(group => group.CategoryId == fixture.ChildCategoryId).Totals.BudgetActual);
        Assert.Equal("21", subtree.Groups.Single(group => group.CategoryId == fixture.NewParentCategoryId).Totals.BudgetActual);
        Assert.DoesNotContain(subtree.Groups, group => group.CategoryId == fixture.OldParentCategoryId && group.Totals.BudgetActual == "21");
        Assert.Equal("21", pool.Groups.Single(group => group.PoolId == fixture.PersonalPoolId).Totals.BudgetActual);
        Assert.Contains(all.Items, item => item.TransactionId == fixture.StatementReplacementId
            && item.ReconciliationState == TransactionReconciliationState.StatementReconciled);
        Assert.DoesNotContain(all.Items, item => item.TransactionId == fixture.OrdinaryPriorId || item.TransactionId == fixture.StatementPriorId);
        Assert.Contains(all.Items, item => item.RelationshipState == ActualsRelationshipRole.TransferOutflow);
        Assert.Contains(all.Items, item => item.RelationshipState == ActualsRelationshipRole.TransferInflow);
        Assert.Equal(0, all.Items.Where(item => item.RelationshipState is ActualsRelationshipRole.TransferOutflow or ActualsRelationshipRole.TransferInflow)
            .Sum(item => DecimalMinor(item.Contribution.BudgetActual)));
    }

    [Fact]
    public async Task Resolved_cash_policy_counts_withdrawal_once_as_immediate_spend()
    {
        await SeedConservationFixture();

        var result = await Query(ActualsGrouping.None);
        var cash = Assert.Single(result.Items, item => item.TransactionId == "00000000000000000000000102");

        Assert.Equal("-5", cash.Contribution.NetAccountMovement);
        Assert.Equal("5", cash.Contribution.ExternalSpend);
        Assert.Equal("5", cash.Contribution.BudgetActual);
        Assert.Equal(ActualsRelationshipRole.None, cash.RelationshipState);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        actuals = new(new(new(database, factory)));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private RelationshipActualsOperationBundle Bundle(
        IReadOnlyList<OperationDescriptor>? source = null,
        string? missing = null) => new(
        missing == "transfers" ? null! : new TransferOperationModule(null!, null!),
        missing == "refunds" ? null! : new RefundOperationModule(null!),
        missing == "relationshipLifecycle" ? null! : new RelationshipLifecycleOperationModule(null!, null!),
        missing == "actuals" ? null! : actuals,
        source);

    private OperationDescriptor[] Source() => OperationRegistry.Create().Descriptors
        .Where(descriptor => ExpectedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal)
            && descriptor.OperationId != ActualsOperationModule.OperationId)
        .Concat(actuals.Descriptors)
        .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
        .ToArray();

    private async Task<ActualsQueryResult> Query(ActualsGrouping grouping)
    {
        var descriptor = Assert.Single(Bundle().Descriptors, item => item.OperationId == ActualsOperationModule.OperationId);
        var handler = descriptor.HandlerFactory(LedgerServices.Create(), OperationRegistry.Create());
        var input = JsonSerializer.SerializeToElement(
            new QueryActualsInput(new(GroupBy: grouping), 100),
            ActualsJsonContext.Default.QueryActualsInput);
        var outcome = await handler.HandleAsync(new(input, null, null), CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        return JsonSerializer.Deserialize(outcome.Value, ActualsJsonContext.Default.ActualsQueryResult)!;
    }

    private async Task<ConservationFixture> SeedConservationFixture()
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        if (await Scalar(connection, "SELECT COUNT(*) FROM transaction_fact;") != 0)
        {
            return new(Id(21), Id(22), Id(23), Id(31), Id(108), Id(109), Id(110));
        }

        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO account VALUES ($accountA, 'Bank', 'cheque', 'asset', '1111', 'ZAR', $at);
            INSERT INTO account VALUES ($accountB, 'Bank', 'savings', 'asset', '2222', 'ZAR', $at);
            INSERT INTO catalogue_lifecycle_event VALUES ($accountEventA, 'account', $accountA, 'create', NULL, 'Primary', 'primary', NULL, 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($accountEventB, 'account', $accountB, 'create', NULL, 'Savings', 'savings', NULL, 'test', $at, NULL);

            INSERT INTO spend_category VALUES ($oldParent, $at);
            INSERT INTO spend_category VALUES ($newParent, $at);
            INSERT INTO spend_category VALUES ($child, $at);
            INSERT INTO category_parent_event VALUES ($oldParentEvent, $oldParent, NULL, 'initialize', 'initial', 'test', $at, NULL);
            INSERT INTO category_parent_event VALUES ($newParentEvent, $newParent, NULL, 'initialize', 'initial', 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($oldParentLifecycle, 'category', $oldParent, 'create', NULL, 'Old Parent', 'old parent', NULL, 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($newParentLifecycle, 'category', $newParent, 'create', NULL, 'New Parent', 'new parent', NULL, 'test', $at, NULL);
            INSERT INTO category_parent_event VALUES ($childParentEvent, $child, $oldParent, 'initialize', 'initial', 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($childLifecycle, 'category', $child, 'create', NULL, 'Child', 'child', NULL, 'test', $at, NULL);

            INSERT INTO spend_pool VALUES ($personalPool, $at);
            INSERT INTO spend_pool VALUES ($companyPool, $at);
            INSERT INTO catalogue_lifecycle_event VALUES ($personalPoolLifecycle, 'spend_pool', $personalPool, 'create', NULL, 'Personal', 'personal', NULL, 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($companyPoolLifecycle, 'spend_pool', $companyPool, 'create', NULL, 'Company', 'company', NULL, 'test', $at, NULL);
            """,
            ("$accountA", Id(1)), ("$accountB", Id(2)), ("$accountEventA", Id(11)), ("$accountEventB", Id(12)),
            ("$oldParent", Id(21)), ("$newParent", Id(22)), ("$child", Id(23)),
            ("$oldParentEvent", Id(24)), ("$newParentEvent", Id(25)), ("$childParentEvent", Id(26)),
            ("$oldParentLifecycle", Id(27)), ("$newParentLifecycle", Id(28)), ("$childLifecycle", Id(29)),
            ("$personalPool", Id(31)), ("$companyPool", Id(32)),
            ("$personalPoolLifecycle", Id(33)), ("$companyPoolLifecycle", Id(34)), ("$at", At));

        await SeedTransaction(connection, transaction, 101, Id(1), -1000, Id(23), Id(31), "Purchase");
        await SeedTransaction(connection, transaction, 102, Id(1), -500, null, null, "Cash withdrawal");
        await SeedTransaction(connection, transaction, 103, Id(1), -2000, null, null, "Transfer out");
        await SeedTransaction(connection, transaction, 104, Id(2), 2000, null, null, "Transfer in");
        await SeedTransaction(connection, transaction, 105, Id(1), -100, null, null, "Transfer fee");
        await SeedTransaction(connection, transaction, 106, Id(1), -300, Id(21), Id(32), "Refund original");
        await SeedTransaction(connection, transaction, 107, Id(1), 300, null, null, "Refund credit");
        await SeedTransaction(connection, transaction, 108, Id(1), -400, Id(23), Id(31), "Ordinary prior");
        await SeedTransaction(connection, transaction, 109, Id(1), -450, Id(23), Id(31), "Ordinary replacement");
        await SeedTransaction(connection, transaction, 110, Id(1), -600, Id(23), Id(31), "Statement prior");
        await SeedTransaction(connection, transaction, 111, Id(1), -650, null, null, "Statement replacement", initializeDimensions: false);

        await Execute(connection, transaction, """
            INSERT INTO financial_relationship VALUES ($transferOne, 'transfer', $transferOut, 'transfer_outflow', $transferIn, 'transfer_inflow', 2000, 'active', $at, 'test', NULL);
            INSERT INTO relationship_lifecycle_event VALUES ($transferLifecycle, $transferOne, 'replaced', $transferTwo, NULL, 'owner correction', 'test', $at);
            INSERT INTO financial_relationship VALUES ($transferTwo, 'transfer', $transferOut, 'transfer_outflow', $transferIn, 'transfer_inflow', 2000, 'active', $at, 'test', NULL);
            INSERT INTO financial_relationship VALUES ($refund, 'refund', $refundOriginal, 'refund_original', $refundCredit, 'refund_credit', 300, 'active', $at, 'test', NULL);
            INSERT INTO transaction_lifecycle_event VALUES ($ordinaryLifecycle, $ordinaryPrior, 'superseded', $ordinaryReplacement, NULL, 'owner correction', 'test', $at);

            INSERT INTO evidence_record VALUES ($evidence, 'statement_row', $digest, 'statement:opaque', $fingerprint, 'test', $at);
            INSERT INTO evidence_observation VALUES ($evidence, $accountA, -650, 'ZAR', '2026-07-11', NULL, NULL, NULL, $descriptionFingerprint);
            INSERT INTO reconciliation_decision VALUES ($decision, $evidence, $statementReplacement, 'owner_confirmed', $manualPolicy, $manualVersion, 'owner reviewed exact replacement', 0, 'statement authority', 'test', $at, NULL);
            INSERT INTO reconciliation_decision_authority VALUES ($decision, 'corrected_from_statement', $statementPrior, $statementReplacement, 'owner', 'scope:fixture', 'v2', $at);
            INSERT INTO category_allocation_event VALUES ($statementCategory, $statementReplacement, $child, 'carry_forward', NULL, $statementPrior, $decision, 'statement carry forward', 'test', $at);
            INSERT INTO pool_assignment_event VALUES ($statementPool, $statementReplacement, 'assigned', $personalPool, 'carry_forward', NULL, $statementPrior, $decision, 'statement carry forward', 'test', $at);
            INSERT INTO transaction_attribution_event VALUES ($statementAttribution, $statementReplacement, 'unknown', NULL, 'unknown', NULL, 'carry_forward', NULL, $statementPrior, $decision, 'statement carry forward', 'test', $at);
            INSERT INTO transaction_lifecycle_event VALUES ($statementLifecycle, $statementPrior, 'statement_authoritative_replacement', $statementReplacement, $decision, 'statement authority', 'test', $at);
            INSERT INTO evidence_link_event VALUES ($statementLink, $evidence, $statementReplacement, 'confirming', 'link', $decision, 'statement authority', 'test', $at, NULL);
            INSERT INTO statement_correction VALUES ($correction, $decision, $statementPrior, $statementReplacement, $statementLifecycle, 'carry_forward', $statementCategory, $statementPool, 'carry_forward', $statementAttribution, 'scope:fixture', NULL, 'statement authority', 'test', $at);

            INSERT INTO category_parent_event VALUES ($reparent, $child, $newParent, 'reparent', 'owner move', 'test', $at, $childParentEvent);
            """,
            ("$transferOne", Id(201)), ("$transferTwo", Id(202)), ("$transferLifecycle", Id(203)),
            ("$transferOut", Id(103)), ("$transferIn", Id(104)),
            ("$refund", Id(204)), ("$refundOriginal", Id(106)), ("$refundCredit", Id(107)),
            ("$ordinaryLifecycle", Id(205)), ("$ordinaryPrior", Id(108)), ("$ordinaryReplacement", Id(109)),
            ("$evidence", Id(301)), ("$digest", new string('a', 64)), ("$fingerprint", new string('b', 64)),
            ("$descriptionFingerprint", new string('c', 64)), ("$decision", Id(302)),
            ("$statementPrior", Id(110)), ("$statementReplacement", Id(111)),
            ("$manualPolicy", "manual_review_projection"), ("$manualVersion", "1.0"),
            ("$statementCategory", Id(303)), ("$statementPool", Id(304)), ("$statementAttribution", Id(305)),
            ("$statementLifecycle", Id(306)), ("$statementLink", Id(307)), ("$correction", Id(308)),
            ("$child", Id(23)), ("$personalPool", Id(31)), ("$reparent", Id(309)), ("$newParent", Id(22)),
            ("$childParentEvent", Id(26)), ("$accountA", Id(1)), ("$at", At));
        await transaction.CommitAsync();
        return new(Id(21), Id(22), Id(23), Id(31), Id(108), Id(110), Id(111));
    }

    private static async Task SeedTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sequence,
        string accountId,
        long amountMinor,
        string? categoryId,
        string? poolId,
        string description,
        bool initializeDimensions = true)
    {
        var transactionId = Id(sequence);
        await Execute(connection, transaction, """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transaction, $account, $amount, 'ZAR', $date, NULL, $description, $at, 'test');
            """, ("$transaction", transactionId), ("$account", accountId), ("$amount", amountMinor),
            ("$date", $"2026-07-{sequence - 100:D2}"), ("$description", description), ("$at", At));
        if (!initializeDimensions) return;

        await Execute(connection, transaction, """
            INSERT INTO transaction_attribution_event VALUES ($attribution, $transaction, 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);
            INSERT INTO pool_assignment_event VALUES ($poolRoot, $transaction, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);
            """, ("$attribution", EventId(4, sequence)), ("$poolRoot", EventId(5, sequence)), ("$transaction", transactionId), ("$at", At));
        if (poolId is not null)
        {
            await Execute(connection, transaction, """
                INSERT INTO pool_assignment_event VALUES ($poolEvent, $transaction, 'assigned', $pool, 'assign', $poolRoot, NULL, NULL, 'owner assignment', 'test', $at);
                """, ("$poolEvent", EventId(6, sequence)), ("$transaction", transactionId), ("$pool", poolId),
                ("$poolRoot", EventId(5, sequence)), ("$at", At));
        }
        if (categoryId is not null)
        {
            await Execute(connection, transaction, """
                INSERT INTO category_allocation_event VALUES ($categoryEvent, $transaction, $category, 'assign', NULL, NULL, NULL, 'owner assignment', 'test', $at);
                """, ("$categoryEvent", EventId(7, sequence)), ("$transaction", transactionId), ("$category", categoryId), ("$at", At));
        }
    }

    private static async Task Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Sum(IEnumerable<string> values) =>
        (values.Sum(DecimalMinor) / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static long DecimalMinor(string value) =>
        checked((long)(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * 100m));

    private static void AssertTotals(ActualsTotalsResult totals, string net, string spend, string budget)
    {
        Assert.Equal(net, totals.NetAccountMovement);
        Assert.Equal(spend, totals.ExternalSpend);
        Assert.Equal(budget, totals.BudgetActual);
    }

    private static string Id(int value) => value.ToString("D26", System.Globalization.CultureInfo.InvariantCulture);

    private static string EventId(int prefix, int value) =>
        prefix.ToString(System.Globalization.CultureInfo.InvariantCulture) + value.ToString("D25", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ConservationFixture(
        string OldParentCategoryId,
        string NewParentCategoryId,
        string ChildCategoryId,
        string PersonalPoolId,
        string OrdinaryPriorId,
        string StatementPriorId,
        string StatementReplacementId);
}
