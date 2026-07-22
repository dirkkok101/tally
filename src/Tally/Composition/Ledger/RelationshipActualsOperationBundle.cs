using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Features.Ledger.Actuals;
using Tally.Features.Ledger.Relationships;

namespace Tally.Composition.Ledger;

public sealed class RelationshipActualsOperationBundle(
    TransferOperationModule transfers,
    RefundOperationModule refunds,
    RelationshipLifecycleOperationModule relationshipLifecycle,
    ActualsOperationModule actuals,
    IReadOnlyList<OperationDescriptor>? sourceDescriptors = null)
{
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

    private static readonly HashSet<string> ExpectedOperationSet = new(ExpectedOperationIds, StringComparer.Ordinal);
    private static readonly string[] ForbiddenContractTerms =
    [
        "agentmail", "mailbox", "mime", "whatsapp", "recipient", "delivery", "rawpayload", "provider"
    ];

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } = Compose(
        transfers ?? throw new ArgumentNullException(nameof(transfers)),
        refunds ?? throw new ArgumentNullException(nameof(refunds)),
        relationshipLifecycle ?? throw new ArgumentNullException(nameof(relationshipLifecycle)),
        actuals ?? throw new ArgumentNullException(nameof(actuals)),
        sourceDescriptors ?? DefaultSource(actuals));

    private static IReadOnlyList<OperationDescriptor> Compose(
        TransferOperationModule transfers,
        RefundOperationModule refunds,
        RelationshipLifecycleOperationModule relationshipLifecycle,
        ActualsOperationModule actuals,
        IReadOnlyList<OperationDescriptor> sourceDescriptors)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptors);
        var descriptors = sourceDescriptors
            .Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId))
            .ToArray();
        Validate(descriptors, actuals);
        return descriptors
            .Select(descriptor => Bind(descriptor, transfers, refunds, relationshipLifecycle, actuals))
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Validate(IReadOnlyList<OperationDescriptor> descriptors, ActualsOperationModule actuals)
    {
        if (descriptors.GroupBy(descriptor => descriptor.OperationId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Relationship actuals bundle contains a duplicate operation ID.");
        if (descriptors.GroupBy(descriptor => descriptor.CliPath, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Relationship actuals bundle contains a duplicate CLI path.");

        var actualIds = descriptors.Select(descriptor => descriptor.OperationId).Order(StringComparer.Ordinal).ToArray();
        if (!ExpectedOperationIds.SequenceEqual(actualIds, StringComparer.Ordinal))
            throw new InvalidOperationException("Relationship actuals bundle does not contain the exact eight-operation contract.");
        if (descriptors.Any(descriptor => descriptor.MinimumContractVersion != "1.0" || descriptor.MaximumContractVersion != "1.0"))
            throw new InvalidOperationException("Relationship actuals bundle contains an incompatible contract version.");
        if (descriptors.Any(ContainsForbiddenContractTerm))
            throw new InvalidOperationException("Relationship actuals bundle contains provider or transport vocabulary.");

        var query = descriptors.Single(descriptor => descriptor.OperationId == ActualsOperationModule.OperationId);
        var filterProperties = ActualsJsonContext.Default.ActualsFilterInput.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var itemProperties = ActualsJsonContext.Default.ActualsPageItem.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (query.RequestTypeInfo.Type != typeof(QueryActualsInput)
            || query.ResultTypeInfo.Type != typeof(ActualsQueryResult)
            || !RequiredFilterProperties.IsSubsetOf(filterProperties)
            || !RequiredItemProperties.IsSubsetOf(itemProperties))
        {
            throw new InvalidOperationException("Relationship actuals bundle does not retain the dimensional actuals contract.");
        }

        var suppliedSchemas = descriptors.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .Select(descriptor => descriptor.ToSchema()).ToArray();
        var canonicalSchemas = DefaultSource(actuals).Select(descriptor => descriptor.ToSchema()).ToArray();
        if (!string.Equals(
                JsonSerializer.Serialize(suppliedSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                JsonSerializer.Serialize(canonicalSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Relationship actuals bundle contains a rewritten descriptor contract.");
        }
    }

    private static OperationDescriptor Bind(
        OperationDescriptor descriptor,
        TransferOperationModule transfers,
        RefundOperationModule refunds,
        RelationshipLifecycleOperationModule relationshipLifecycle,
        ActualsOperationModule actuals) => descriptor with
        {
            HandlerFactory = descriptor.OperationId switch
            {
                "ledger.transfer.confirm" => (_, _) => new RelationshipActualsBundledOperationHandler(transfers.HandleAsync, descriptor.OperationId),
                "ledger.refund.confirm" => (_, _) => new RelationshipActualsBundledOperationHandler(refunds.HandleAsync, descriptor.OperationId),
                "ledger.transfer.revoke" or "ledger.transfer.replace" or "ledger.refund.revoke" or "ledger.refund.replace" or "ledger.relationship.get" =>
                    (_, _) => new RelationshipActualsBundledOperationHandler(relationshipLifecycle.HandleAsync, descriptor.OperationId),
                ActualsOperationModule.OperationId => (_, _) => new RelationshipActualsBundledOperationHandler(actuals.HandleAsync, descriptor.OperationId),
                _ => throw new InvalidOperationException("Relationship actuals operation is not explicitly composed.")
            }
        };

    private static OperationDescriptor[] DefaultSource(ActualsOperationModule actuals) => OperationRegistry.Create().Descriptors
        .Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId)
            && descriptor.OperationId != ActualsOperationModule.OperationId)
        .Concat(actuals.Descriptors)
        .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
        .ToArray();

    private static bool ContainsForbiddenContractTerm(OperationDescriptor descriptor) =>
        ContractTokens(descriptor).Any(token => ForbiddenContractTerms.Any(forbidden =>
            NormalizeToken(token).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));

    private static string NormalizeToken(string token) => new(token.Where(char.IsLetterOrDigit).ToArray());

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

    private static readonly HashSet<string> RequiredFilterProperties =
    [
        "categoryScope", "categorizationStates", "poolStates", "instrumentStates", "cardholderStates",
        "reconciliationStates", "relationshipStates", "lifecycleStates", "groupBy"
    ];

    private static readonly HashSet<string> RequiredItemProperties =
    [
        "frozenAncestryIds", "categoryState", "poolState", "instrumentState", "cardholderState",
        "reconciliationState", "relationshipState", "contribution"
    ];
}

internal sealed class RelationshipActualsBundledOperationHandler(
    Func<string, OperationRequest, CancellationToken, Task<CommandResult<JsonElement>>> dispatch,
    string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        dispatch(operationId, request, cancellationToken);
}
