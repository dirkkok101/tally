using System.Text.Json;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Features.Ledger.Accounts;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Dimensions;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Transactions;
using Xunit;

namespace Tally.Tests.Composition.Ledger;

public sealed class CatalogueTransactionOperationBundleTests
{
    private static readonly string[] ExpectedOperationIds =
    [
        "ledger.account.archive", "ledger.account.create", "ledger.account.get", "ledger.account.list", "ledger.account.rename",
        "ledger.cardholder.archive", "ledger.cardholder.create", "ledger.cardholder.get", "ledger.cardholder.list", "ledger.cardholder.reactivate", "ledger.cardholder.rename",
        "ledger.category.archive", "ledger.category.create", "ledger.category.get", "ledger.category.list", "ledger.category.reactivate", "ledger.category.rename", "ledger.category.reparent",
        "ledger.evidence.get", "ledger.evidence.link-supporting", "ledger.evidence.register",
        "ledger.instrument.archive", "ledger.instrument.create", "ledger.instrument.get", "ledger.instrument.list", "ledger.instrument.reactivate", "ledger.instrument.rename",
        "ledger.pool.archive", "ledger.pool.create", "ledger.pool.get", "ledger.pool.list", "ledger.pool.reactivate", "ledger.pool.rename",
        "ledger.transaction.attribution.assign", "ledger.transaction.attribution.correct",
        "ledger.transaction.category.assign", "ledger.transaction.category.correct",
        "ledger.transaction.get", "ledger.transaction.pool.assign", "ledger.transaction.pool.correct", "ledger.transaction.record", "ledger.transaction.supersede", "ledger.transaction.void"
    ];

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_composes_exactly_the_catalogue_transaction_and_evidence_set()
    {
        var bundle = CreateBundle();

        Assert.Equal(ExpectedOperationIds, bundle.Descriptors.Select(descriptor => descriptor.OperationId));
        Assert.Equal(43, bundle.Descriptors.Select(descriptor => descriptor.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(43, bundle.Descriptors.Select(descriptor => descriptor.CliPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("account", 5)]
    [InlineData("category", 7)]
    [InlineData("instrument", 6)]
    [InlineData("cardholder", 6)]
    [InlineData("pool", 6)]
    [InlineData("transaction", 4)]
    [InlineData("category_assignment", 2)]
    [InlineData("payment_attribution", 2)]
    [InlineData("pool_assignment", 2)]
    [InlineData("evidence", 3)]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_retains_each_module_subtotal(string group, int expected)
    {
        var descriptors = CreateBundle().Descriptors;
        var count = group switch
        {
            "account" => Count(descriptors, "ledger.account."),
            "category" => Count(descriptors, "ledger.category."),
            "instrument" => Count(descriptors, "ledger.instrument."),
            "cardholder" => Count(descriptors, "ledger.cardholder."),
            "pool" => Count(descriptors, "ledger.pool."),
            "transaction" => descriptors.Count(descriptor => descriptor.OperationId is
                "ledger.transaction.record" or "ledger.transaction.get" or "ledger.transaction.void" or "ledger.transaction.supersede"),
            "category_assignment" => Count(descriptors, "ledger.transaction.category."),
            "payment_attribution" => Count(descriptors, "ledger.transaction.attribution."),
            "pool_assignment" => Count(descriptors, "ledger.transaction.pool."),
            "evidence" => Count(descriptors, "ledger.evidence."),
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };

        Assert.Equal(expected, count);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_retains_every_descriptor_schema_unchanged()
    {
        var source = CatalogueSource();
        var bundle = CreateBundle(source);
        var sourceSchemas = source.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .Select(descriptor => descriptor.ToSchema()).ToArray();
        var bundleSchemas = bundle.Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray();

        Assert.Equal(
            JsonSerializer.Serialize(sourceSchemas, LedgerJsonContext.Default.OperationSchemaArray),
            JsonSerializer.Serialize(bundleSchemas, LedgerJsonContext.Default.OperationSchemaArray));
        Assert.All(bundle.Descriptors, descriptor =>
        {
            Assert.NotEqual(typeof(JsonElement), descriptor.RequestTypeInfo.Type);
            Assert.NotEqual(typeof(JsonElement), descriptor.ResultTypeInfo.Type);
            Assert.Equal("1.0", descriptor.MinimumContractVersion);
            Assert.Equal("1.0", descriptor.MaximumContractVersion);
        });
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_binds_every_descriptor_to_an_explicit_module_handler()
    {
        var registry = OperationRegistry.Create();
        foreach (var descriptor in CreateBundle().Descriptors)
        {
            var handler = descriptor.HandlerFactory(Tally.Bootstrap.LedgerServices.Create(), registry);

            Assert.EndsWith("OperationHandler", handler.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void DD_LEDGER_CATEGORY_HIERARCHY_retains_create_reparent_and_hierarchy_results()
    {
        var descriptors = CreateBundle().Descriptors;
        var create = Assert.Single(descriptors, descriptor => descriptor.OperationId == "ledger.category.create");
        var reparent = Assert.Single(descriptors, descriptor => descriptor.OperationId == "ledger.category.reparent");

        Assert.Equal(typeof(CreateCategoryInput), create.RequestTypeInfo.Type);
        Assert.Contains(create.RequestTypeInfo.Properties, property => property.Name == "parentCategoryId");
        Assert.Equal(typeof(ReparentCategoryInput), reparent.RequestTypeInfo.Type);
        Assert.Contains(reparent.RequestTypeInfo.Properties, property => property.Name == "parentCategoryId");
        Assert.Equal(typeof(CategoryReparentResult), reparent.ResultTypeInfo.Type);
    }

    [Theory]
    [InlineData("agentmail")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("whatsapp")]
    [InlineData("recipient")]
    [InlineData("delivery")]
    [InlineData("rawPayload")]
    [InlineData("budgetPlan")]
    public void NFR_LEDGER_LOCAL_PRIVACY_bundle_contract_is_provider_neutral(string forbidden)
    {
        var json = JsonSerializer.Serialize(
            CreateBundle().Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);

        Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("categories")]
    [InlineData("paymentIdentities")]
    [InlineData("paymentAttributions")]
    [InlineData("spendPools")]
    [InlineData("poolAssignments")]
    [InlineData("categoryAllocations")]
    [InlineData("transactions")]
    [InlineData("evidenceRegistry")]
    [InlineData("evidenceLinks")]
    public void DD_LEDGER_APPLICATION_ARCHITECTURE_rejects_a_missing_module(string missing)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => CreateBundle(missing: missing));

        Assert.Equal(missing, exception.ParamName);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_rejects_a_missing_descriptor()
    {
        var source = CatalogueSource().Where(descriptor => descriptor.OperationId != "ledger.category.reparent").ToArray();

        Assert.Contains("exact 43-operation", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_rejects_a_duplicate_operation_id()
    {
        var source = CatalogueSource().ToList();
        source.Add(source[0]);

        Assert.Contains("duplicate operation ID", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_rejects_a_duplicate_cli_path()
    {
        var source = CatalogueSource();
        var duplicatePath = source[0].CliPath;
        source[1] = source[1] with { CliPath = duplicatePath };

        Assert.Contains("duplicate CLI path", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_rejects_an_incompatible_version(string bound)
    {
        var source = CatalogueSource();
        source[0] = bound == "minimum"
            ? source[0] with { MinimumContractVersion = "2.0" }
            : source[0] with { MaximumContractVersion = "2.0" };

        Assert.Contains("incompatible contract version", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("mailbox")]
    public void NFR_LEDGER_LOCAL_PRIVACY_rejects_provider_or_transport_vocabulary(string forbidden)
    {
        var source = CatalogueSource();
        source[0] = source[0] with { Example = source[0].Example + " " + forbidden };

        Assert.Contains("provider or transport", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_LEDGER_EVIDENCE_REGISTRATION_rejects_open_evidence_metadata()
    {
        var source = CatalogueSource();
        var index = Array.FindIndex(source, descriptor => descriptor.OperationId == "ledger.evidence.register");
        source[index] = source[index] with { RequestTypeInfo = LedgerJsonContext.Default.RequestEnvelope };

        Assert.Contains("open evidence metadata", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DD_LEDGER_CATEGORY_HIERARCHY_rejects_a_flat_reparent_contract()
    {
        var source = CatalogueSource();
        var index = Array.FindIndex(source, descriptor => descriptor.OperationId == "ledger.category.reparent");
        source[index] = source[index] with { RequestTypeInfo = LedgerJsonContext.Default.CreateCategoryInput };

        Assert.Contains("hierarchical category contract", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DD_LEDGER_CLI_OPERATION_CONTRACT_rejects_any_rewritten_descriptor_schema()
    {
        var source = CatalogueSource();
        var index = Array.FindIndex(source, descriptor => descriptor.OperationId == "ledger.account.get");
        source[index] = source[index] with { RequestTypeInfo = LedgerJsonContext.Default.EmptyInput };

        Assert.Contains("rewritten descriptor contract", Assert.Throws<InvalidOperationException>(() => CreateBundle(source)).Message, StringComparison.Ordinal);
    }

    private static CatalogueTransactionOperationBundle CreateBundle(
        IReadOnlyList<OperationDescriptor>? descriptors = null,
        string? missing = null) => new(
        missing == "accounts" ? null! : new AccountOperationModule(null!, null!, null!, null!, null!),
        missing == "categories" ? null! : new CategoryOperationModule(null!, null!, null!, null!, null!, null!, null!),
        missing == "paymentIdentities" ? null! : new PaymentIdentityOperationModule(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!),
        missing == "paymentAttributions" ? null! : new PaymentAttributionOperationModule(null!, null!),
        missing == "spendPools" ? null! : new SpendPoolOperationModule(null!, null!, null!, null!, null!, null!),
        missing == "poolAssignments" ? null! : new PoolAssignmentOperationModule(null!, null!),
        missing == "categoryAllocations" ? null! : new CategoryAllocationOperationModule(null!, null!),
        missing == "transactions" ? null! : new TransactionOperationModule(null!, null!, null!),
        missing == "evidenceRegistry" ? null! : new EvidenceRegistryOperationModule(null!, null!),
        missing == "evidenceLinks" ? null! : new EvidenceLinkOperationModule(null!),
        descriptors);

    private static OperationDescriptor[] CatalogueSource() => OperationRegistry.Create().Descriptors
        .Where(descriptor => ExpectedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal))
        .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
        .ToArray();

    private static int Count(IEnumerable<OperationDescriptor> descriptors, string prefix) =>
        descriptors.Count(descriptor => descriptor.OperationId.StartsWith(prefix, StringComparison.Ordinal));
}
