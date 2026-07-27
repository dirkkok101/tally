using System.Text.Json.Serialization;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;

namespace Tally.Contracts.Budget.Insights;

[JsonConverter(typeof(JsonStringEnumConverter<BudgetInsightPlanState>))]
public enum BudgetInsightPlanState
{
    [JsonStringEnumMemberName("bound_revision")]
    BoundRevision,
    [JsonStringEnumMemberName("no_budget_plan")]
    NoBudgetPlan,
    [JsonStringEnumMemberName("no_active_budget_plan_revision")]
    NoActiveBudgetPlanRevision
}

public sealed record GetBudgetInsightEvidenceInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] BudgetPeriodInput BudgetPeriod,
    string? RevisionId,
    int? MemberLimit);

public sealed record BudgetActualMember(
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string EffectiveDate,
    string? CategoryId,
    [property: JsonRequired] long BudgetActualMinorUnits);

public sealed record BudgetInsightEvidence(
    [property: JsonRequired] BudgetInsightPlanState PlanState,
    BudgetPlanRevisionDetail? Revision,
    BudgetPosition? Position,
    [property: JsonRequired] IReadOnlyList<BudgetActualMember> ActualMembers,
    [property: JsonRequired] long BudgetActualTotalMinorUnits,
    [property: JsonRequired] LedgerSnapshotEvidence Ledger,
    string? CalculationSchemaVersion,
    [property: JsonRequired] string BindingFingerprint);

public sealed record GetBudgetInsightEvidenceResult(
    [property: JsonRequired] BudgetInsightEvidence Evidence);
