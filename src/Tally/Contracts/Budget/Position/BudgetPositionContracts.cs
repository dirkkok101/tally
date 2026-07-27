using System.Text.Json.Serialization;
using Tally.Contracts.Budget.Plans;

namespace Tally.Contracts.Budget.Position;

[JsonConverter(typeof(JsonStringEnumConverter<BudgetCategoryPositionKind>))]
public enum BudgetCategoryPositionKind
{
    [JsonStringEnumMemberName("budgeted")]
    Budgeted,
    [JsonStringEnumMemberName("zero_budget")]
    ZeroBudget,
    [JsonStringEnumMemberName("unbudgeted")]
    Unbudgeted,
    [JsonStringEnumMemberName("uncategorized")]
    Uncategorized
}

public sealed record GetBudgetPositionInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] BudgetPeriodInput Period,
    string? RevisionId);

public sealed record LedgerSnapshotEvidence(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string SnapshotId,
    [property: JsonRequired] string ExpiresAt,
    [property: JsonRequired] string StoreGenerationFingerprint);

public sealed record CategoryPosition(
    string? CategoryId,
    string? CurrentDisplayName,
    CategoryLifecycleStatus? CurrentLifecycle,
    [property: JsonRequired] BudgetCategoryPositionKind Kind,
    long? PlannedMinorUnits,
    [property: JsonRequired] long ActualMinorUnits,
    long? RemainingMinorUnits,
    long? OverMinorUnits);

public sealed record BudgetPositionTotals(
    [property: JsonRequired] long PlannedMinorUnits,
    [property: JsonRequired] long ActualMinorUnits,
    [property: JsonRequired] long RemainingMinorUnits,
    [property: JsonRequired] long OverMinorUnits,
    [property: JsonRequired] long BudgetedActualMinorUnits,
    [property: JsonRequired] long ZeroBudgetActualMinorUnits,
    [property: JsonRequired] long UnbudgetedActualMinorUnits,
    [property: JsonRequired] long UncategorizedActualMinorUnits);

public sealed record BudgetPosition(
    [property: JsonRequired] string CalculationSchemaVersion,
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] string RevisionId,
    [property: JsonRequired] BudgetRevisionStatus RevisionStatus,
    [property: JsonRequired] BudgetPeriodDetail Period,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string CategoryContractVersion,
    [property: JsonRequired] LedgerSnapshotEvidence Ledger,
    [property: JsonRequired] IReadOnlyList<CategoryPosition> CategoryPositions,
    [property: JsonRequired] CategoryPosition UncategorizedPosition,
    [property: JsonRequired] BudgetPositionTotals Totals);

public sealed record GetBudgetPositionResult(
    BudgetPosition? Position,
    [property: JsonRequired] bool HasActiveBudgetPlanRevision);
