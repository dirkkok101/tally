using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Features.Ledger.Accounts;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Dimensions;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Transactions;

namespace Tally.Composition.Ledger;

public sealed class CatalogueTransactionOperationBundle(
    AccountOperationModule accounts,
    CategoryOperationModule categories,
    PaymentIdentityOperationModule paymentIdentities,
    PaymentAttributionOperationModule paymentAttributions,
    SpendPoolOperationModule spendPools,
    PoolAssignmentOperationModule poolAssignments,
    CategoryAllocationOperationModule categoryAllocations,
    TransactionOperationModule transactions,
    EvidenceRegistryOperationModule evidenceRegistry,
    EvidenceLinkOperationModule evidenceLinks,
    IReadOnlyList<OperationDescriptor>? sourceDescriptors = null)
{
    private static readonly string[] ExpectedOperationIds =
    [
        "ledger.account.archive",
        "ledger.account.create",
        "ledger.account.get",
        "ledger.account.list",
        "ledger.account.rename",
        "ledger.cardholder.archive",
        "ledger.cardholder.create",
        "ledger.cardholder.get",
        "ledger.cardholder.list",
        "ledger.cardholder.reactivate",
        "ledger.cardholder.rename",
        "ledger.category.archive",
        "ledger.category.create",
        "ledger.category.get",
        "ledger.category.list",
        "ledger.category.reactivate",
        "ledger.category.rename",
        "ledger.category.reparent",
        "ledger.evidence.get",
        "ledger.evidence.link-supporting",
        "ledger.evidence.register",
        "ledger.instrument.archive",
        "ledger.instrument.create",
        "ledger.instrument.get",
        "ledger.instrument.list",
        "ledger.instrument.reactivate",
        "ledger.instrument.rename",
        "ledger.pool.archive",
        "ledger.pool.create",
        "ledger.pool.get",
        "ledger.pool.list",
        "ledger.pool.reactivate",
        "ledger.pool.rename",
        "ledger.transaction.attribution.assign",
        "ledger.transaction.attribution.correct",
        "ledger.transaction.category.assign",
        "ledger.transaction.category.correct",
        "ledger.transaction.get",
        "ledger.transaction.pool.assign",
        "ledger.transaction.pool.correct",
        "ledger.transaction.record",
        "ledger.transaction.supersede",
        "ledger.transaction.void"
    ];

    private static readonly HashSet<string> ExpectedOperationSet = new(ExpectedOperationIds, StringComparer.Ordinal);
    private static readonly string[] ForbiddenContractTerms =
    [
        "agentmail", "mailbox", "mime", "whatsapp", "recipient", "delivery", "rawpayload", "provider"
    ];

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } = Compose(
        accounts ?? throw new ArgumentNullException(nameof(accounts)),
        categories ?? throw new ArgumentNullException(nameof(categories)),
        paymentIdentities ?? throw new ArgumentNullException(nameof(paymentIdentities)),
        paymentAttributions ?? throw new ArgumentNullException(nameof(paymentAttributions)),
        spendPools ?? throw new ArgumentNullException(nameof(spendPools)),
        poolAssignments ?? throw new ArgumentNullException(nameof(poolAssignments)),
        categoryAllocations ?? throw new ArgumentNullException(nameof(categoryAllocations)),
        transactions ?? throw new ArgumentNullException(nameof(transactions)),
        evidenceRegistry ?? throw new ArgumentNullException(nameof(evidenceRegistry)),
        evidenceLinks ?? throw new ArgumentNullException(nameof(evidenceLinks)),
        sourceDescriptors ?? OperationRegistry.Create().Descriptors);

    private static IReadOnlyList<OperationDescriptor> Compose(
        AccountOperationModule accounts,
        CategoryOperationModule categories,
        PaymentIdentityOperationModule paymentIdentities,
        PaymentAttributionOperationModule paymentAttributions,
        SpendPoolOperationModule spendPools,
        PoolAssignmentOperationModule poolAssignments,
        CategoryAllocationOperationModule categoryAllocations,
        TransactionOperationModule transactions,
        EvidenceRegistryOperationModule evidenceRegistry,
        EvidenceLinkOperationModule evidenceLinks,
        IReadOnlyList<OperationDescriptor> sourceDescriptors)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptors);
        var descriptors = sourceDescriptors
            .Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId))
            .ToArray();
        Validate(descriptors);
        return descriptors
            .Select(descriptor => Bind(
                descriptor,
                accounts,
                categories,
                paymentIdentities,
                paymentAttributions,
                spendPools,
                poolAssignments,
                categoryAllocations,
                transactions,
                evidenceRegistry,
                evidenceLinks))
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Validate(IReadOnlyList<OperationDescriptor> descriptors)
    {
        if (descriptors.GroupBy(descriptor => descriptor.OperationId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Catalogue transaction bundle contains a duplicate operation ID.");
        if (descriptors.GroupBy(descriptor => descriptor.CliPath, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Catalogue transaction bundle contains a duplicate CLI path.");

        var actualIds = descriptors.Select(descriptor => descriptor.OperationId).Order(StringComparer.Ordinal).ToArray();
        if (!ExpectedOperationIds.SequenceEqual(actualIds, StringComparer.Ordinal))
            throw new InvalidOperationException("Catalogue transaction bundle does not contain the exact 43-operation contract.");
        if (descriptors.Any(descriptor =>
                descriptor.MinimumContractVersion != "1.0"
                || descriptor.MaximumContractVersion != "1.0"))
            throw new InvalidOperationException("Catalogue transaction bundle contains an incompatible contract version.");
        if (descriptors.Any(ContainsForbiddenContractTerm))
            throw new InvalidOperationException("Catalogue transaction bundle contains provider or transport vocabulary.");
        if (descriptors.Where(descriptor => descriptor.OperationId.StartsWith("ledger.evidence.", StringComparison.Ordinal))
            .Any(descriptor => IsOpenPayload(descriptor.RequestTypeInfo) || IsOpenPayload(descriptor.ResultTypeInfo)))
            throw new InvalidOperationException("Catalogue transaction bundle contains open evidence metadata.");

        var reparent = descriptors.Single(descriptor => descriptor.OperationId == "ledger.category.reparent");
        if (reparent.RequestTypeInfo.Type != typeof(ReparentCategoryInput)
            || reparent.ResultTypeInfo.Type != typeof(CategoryReparentResult)
            || !reparent.RequestTypeInfo.Properties.Any(property => property.Name == "parentCategoryId"))
            throw new InvalidOperationException("Catalogue transaction bundle does not retain the hierarchical category contract.");
        if (descriptors.Any(descriptor => ContractTokens(descriptor).Any(token =>
                token.Contains("budget", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Catalogue transaction bundle contains Budget Plan semantics.");

        var canonical = OperationRegistry.Create().Descriptors
            .Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId))
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
        var suppliedSchemas = descriptors.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .Select(descriptor => descriptor.ToSchema()).ToArray();
        var canonicalSchemas = canonical.Select(descriptor => descriptor.ToSchema()).ToArray();
        if (!string.Equals(
                JsonSerializer.Serialize(suppliedSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                JsonSerializer.Serialize(canonicalSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Catalogue transaction bundle contains a rewritten descriptor contract.");
    }

    private static OperationDescriptor Bind(
        OperationDescriptor descriptor,
        AccountOperationModule accounts,
        CategoryOperationModule categories,
        PaymentIdentityOperationModule paymentIdentities,
        PaymentAttributionOperationModule paymentAttributions,
        SpendPoolOperationModule spendPools,
        PoolAssignmentOperationModule poolAssignments,
        CategoryAllocationOperationModule categoryAllocations,
        TransactionOperationModule transactions,
        EvidenceRegistryOperationModule evidenceRegistry,
        EvidenceLinkOperationModule evidenceLinks) =>
        descriptor with
        {
            HandlerFactory = descriptor.OperationId switch
            {
                var operationId when operationId.StartsWith("ledger.account.", StringComparison.Ordinal) =>
                    (_, _) => new AccountOperationHandler(accounts, operationId),
                var operationId when operationId.StartsWith("ledger.category.", StringComparison.Ordinal) =>
                    (_, _) => new CategoryOperationHandler(categories, operationId),
                var operationId when operationId.StartsWith("ledger.instrument.", StringComparison.Ordinal)
                    || operationId.StartsWith("ledger.cardholder.", StringComparison.Ordinal) =>
                    (_, _) => new PaymentIdentityOperationHandler(paymentIdentities, operationId),
                var operationId when operationId.StartsWith("ledger.pool.", StringComparison.Ordinal) =>
                    (_, _) => new SpendPoolOperationHandler(spendPools, operationId),
                var operationId when operationId.StartsWith("ledger.transaction.attribution.", StringComparison.Ordinal) =>
                    (_, _) => new PaymentAttributionOperationHandler(paymentAttributions, operationId),
                var operationId when operationId.StartsWith("ledger.transaction.pool.", StringComparison.Ordinal) =>
                    (_, _) => new PoolAssignmentOperationHandler(poolAssignments, operationId),
                var operationId when operationId.StartsWith("ledger.transaction.category.", StringComparison.Ordinal) =>
                    (_, _) => new CategoryAllocationOperationHandler(categoryAllocations, operationId),
                var operationId when operationId.StartsWith("ledger.transaction.", StringComparison.Ordinal) =>
                    (_, _) => new TransactionOperationHandler(transactions, operationId),
                "ledger.evidence.register" or "ledger.evidence.get" =>
                    (_, _) => new EvidenceRegistryOperationHandler(evidenceRegistry, descriptor.OperationId),
                "ledger.evidence.link-supporting" =>
                    (_, _) => new EvidenceLinkOperationHandler(evidenceLinks),
                _ => throw new InvalidOperationException("Catalogue transaction operation is not explicitly composed.")
            }
        };

    private static bool ContainsForbiddenContractTerm(OperationDescriptor descriptor) =>
        ContractTokens(descriptor).Any(token => ForbiddenContractTerms.Any(forbidden =>
            NormalizeToken(token).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));

    private static string NormalizeToken(string token) => new(token.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsOpenPayload(JsonTypeInfo typeInfo) =>
        typeInfo.Type == typeof(JsonElement)
        || typeInfo.Type == typeof(object)
        || typeInfo.Type.FullName?.Contains("Dictionary", StringComparison.Ordinal) == true
        || typeInfo.Properties.Any(property =>
            property.Name.Contains("metadata", StringComparison.OrdinalIgnoreCase)
            || property.PropertyType == typeof(JsonElement)
            || property.PropertyType == typeof(object)
            || property.PropertyType.FullName?.Contains("Dictionary", StringComparison.Ordinal) == true);

    private static IEnumerable<string> ContractTokens(OperationDescriptor descriptor)
    {
        yield return descriptor.OperationId;
        yield return descriptor.CliPath;
        yield return descriptor.HandlerTarget;
        yield return descriptor.Example;
        foreach (var token in TypeTokens(descriptor.RequestTypeInfo)) yield return token;
        foreach (var token in TypeTokens(descriptor.ResultTypeInfo)) yield return token;
    }

    private static IEnumerable<string> TypeTokens(JsonTypeInfo typeInfo)
    {
        yield return typeInfo.Type.FullName ?? typeInfo.Type.Name;
        foreach (var property in typeInfo.Properties)
        {
            yield return property.Name;
            yield return property.PropertyType.FullName ?? property.PropertyType.Name;
        }
    }
}
