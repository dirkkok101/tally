using System.Security.Cryptography;
using System.Text;
using Tally.Cli;
using Tally.Contracts.Budget.Projection;
using Tally.Features.Budget.Contract;

namespace Tally.Features.Budget.Projection;

/// <summary>
/// Publishes the mutation-free INSIGHTS read capability
/// (DD-BUDGET-INSIGHTS-READ-PROJECTION / DM-BUDGET-INSIGHTS-READ-CONTRACT).
/// Reuses owner operation descriptors and schema fingerprints from
/// <see cref="BudgetOperationModule"/>; never grants draft create, activate,
/// or any other mutation authority.
/// </summary>
public sealed class BudgetReadProjectionModule
{
    /// <summary>
    /// Hard upper bound on <c>budget.insights.evidence.get</c> memberLimit
    /// (personal-scale NFR — one complete snapshot, no silent truncation).
    /// </summary>
    public const int InsightsEvidenceMaxMemberLimit = 100_000;

    /// <summary>Default memberLimit when the caller omits it.</summary>
    public const int InsightsEvidenceDefaultMemberLimit = InsightsEvidenceMaxMemberLimit;

    private readonly BudgetOperationModule operationModule;

    public BudgetReadProjectionModule(BudgetOperationModule? operationModule = null)
    {
        this.operationModule = operationModule ?? BudgetOperationModule.CreateDescriptorTemplates();
    }

    /// <summary>
    /// Build the three-operation read capability with versioned request/result fingerprints,
    /// compatibility ranges, stable error codes, and evidence member limits.
    /// </summary>
    public BudgetReadCapabilityDescriptor CreateCapability()
    {
        var byId = operationModule.Descriptors
            .ToDictionary(d => d.OperationId, StringComparer.Ordinal);

        var allowed = new List<BudgetReadOperationCapability>(BudgetReadCapabilityOperations.All.Count);
        foreach (var operationId in BudgetReadCapabilityOperations.All)
        {
            if (!byId.TryGetValue(operationId, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"BUDGET owner inventory is missing required INSIGHTS read operation '{operationId}'.");
            }

            if (descriptor.RequiresIdempotencyKey
                || !string.Equals(descriptor.Kind, "query", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"INSIGHTS capability refuses non-query or mutating operation '{operationId}'.");
            }

            var schema = descriptor.ToSchema();
            int? defaultLimit = null;
            int? maxLimit = null;
            if (string.Equals(operationId, BudgetOperationIds.InsightsEvidenceGet, StringComparison.Ordinal))
            {
                defaultLimit = InsightsEvidenceDefaultMemberLimit;
                maxLimit = InsightsEvidenceMaxMemberLimit;
            }

            allowed.Add(new BudgetReadOperationCapability(
                OperationId: descriptor.OperationId,
                Kind: descriptor.Kind,
                RequiresIdempotencyKey: false,
                RequestSchemaFingerprint: Fingerprint(schema.RequestSchema),
                ResultSchemaFingerprint: Fingerprint(schema.ResultSchema),
                MinimumContractVersion: descriptor.MinimumContractVersion,
                MaximumContractVersion: descriptor.MaximumContractVersion,
                ErrorCodes: (descriptor.DomainErrors ?? [])
                    .Select(e => e.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToArray(),
                DefaultLimit: defaultLimit,
                MaxLimit: maxLimit));
        }

        return new BudgetReadCapabilityDescriptor(
            ContractVersion: BudgetOperationIds.ContractVersion,
            MinimumContractVersion: BudgetOperationIds.ContractVersion,
            MaximumContractVersion: BudgetOperationIds.ContractVersion,
            AllowedOperations: allowed);
    }

    /// <summary>Template capability without runtime stores (discovery-safe).</summary>
    public static BudgetReadCapabilityDescriptor CreateDescriptorTemplate() =>
        new BudgetReadProjectionModule().CreateCapability();

    /// <summary>
    /// True when <paramref name="operationId"/> is in the INSIGHTS allowed read set.
    /// </summary>
    public static bool IsAllowedReadOperation(string? operationId) =>
        operationId is not null
        && BudgetReadCapabilityOperations.All.Contains(operationId, StringComparer.Ordinal);

    private static string Fingerprint(string schemaText)
    {
        var bytes = Encoding.UTF8.GetBytes(schemaText ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
