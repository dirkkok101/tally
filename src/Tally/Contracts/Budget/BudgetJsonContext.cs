using System.Text.Json.Serialization;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;

namespace Tally.Contracts.Budget;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CreateDraftBudgetPlanInput))]
[JsonSerializable(typeof(CreateDraftBudgetPlanResult))]
[JsonSerializable(typeof(GetBudgetPlanRevisionInput))]
[JsonSerializable(typeof(BudgetPlanRevisionDetail))]
[JsonSerializable(typeof(ListBudgetPlanRevisionsInput))]
[JsonSerializable(typeof(ListBudgetPlanRevisionsResult))]
[JsonSerializable(typeof(ActivateBudgetPlanRevisionInput))]
[JsonSerializable(typeof(ActivateBudgetPlanRevisionResult))]
[JsonSerializable(typeof(GetBudgetPositionInput))]
[JsonSerializable(typeof(GetBudgetPositionResult))]
[JsonSerializable(typeof(GetBudgetInsightEvidenceInput))]
[JsonSerializable(typeof(GetBudgetInsightEvidenceResult))]
[JsonSerializable(typeof(BudgetPeriodInput))]
[JsonSerializable(typeof(BudgetPlanEntryInput))]
[JsonSerializable(typeof(BudgetPlanEntryDetail))]
[JsonSerializable(typeof(BudgetPosition))]
[JsonSerializable(typeof(BudgetInsightEvidence))]
[JsonSerializable(typeof(CategoryPosition))]
[JsonSerializable(typeof(BudgetActualMember))]
public partial class BudgetJsonContext : JsonSerializerContext;
