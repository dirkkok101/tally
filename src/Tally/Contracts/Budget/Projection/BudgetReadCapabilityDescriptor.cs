using System.Text.Json.Serialization;

namespace Tally.Contracts.Budget.Projection;

/// <summary>
/// Versioned read-only INSIGHTS capability set (DM-BUDGET-INSIGHTS-READ-CONTRACT).
/// Exactly three operations; no mutation, idempotency, or private LEDGER authority.
/// </summary>
public sealed record BudgetReadCapabilityDescriptor(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string MinimumContractVersion,
    [property: JsonRequired] string MaximumContractVersion,
    [property: JsonRequired] IReadOnlyList<BudgetReadOperationCapability> AllowedOperations);

/// <summary>
/// One allowed INSIGHTS read operation with versioned schema fingerprints, errors, and limits.
/// </summary>
public sealed record BudgetReadOperationCapability(
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] bool RequiresIdempotencyKey,
    [property: JsonRequired] string RequestSchemaFingerprint,
    [property: JsonRequired] string ResultSchemaFingerprint,
    [property: JsonRequired] string MinimumContractVersion,
    [property: JsonRequired] string MaximumContractVersion,
    [property: JsonRequired] IReadOnlyList<string> ErrorCodes,
    int? DefaultLimit,
    int? MaxLimit);

/// <summary>Stable operation identifiers published through the INSIGHTS read capability.</summary>
public static class BudgetReadCapabilityOperations
{
    public const string PlanRevisionGet = "budget.plan.revision.get";
    public const string PositionGet = "budget.position.get";
    public const string InsightsEvidenceGet = "budget.insights.evidence.get";

    /// <summary>
    /// Canonical allowed set in publish order — mutation operations are never present.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        PlanRevisionGet,
        PositionGet,
        InsightsEvidenceGet
    ];
}
