using System.Text.Json.Serialization;

namespace Tally.Contracts.Budget.Plans;

[JsonConverter(typeof(JsonStringEnumConverter<BudgetPeriodState>))]
public enum BudgetPeriodState
{
    [JsonStringEnumMemberName("current")]
    Current,
    [JsonStringEnumMemberName("future")]
    Future,
    [JsonStringEnumMemberName("closed")]
    Closed
}

[JsonConverter(typeof(JsonStringEnumConverter<BudgetRevisionStatus>))]
public enum BudgetRevisionStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft,
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("superseded")]
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter<CategoryLifecycleStatus>))]
public enum CategoryLifecycleStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("archived")]
    Archived,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

/// <summary>Explicit ZAR calendar month period (DM-BUDGET-PERIOD-PLAN).</summary>
public sealed record BudgetPeriodInput(
    [property: JsonRequired] int Year,
    [property: JsonRequired] int Month,
    [property: JsonRequired] string CurrencyCode);

public sealed record BudgetPeriodDetail(
    [property: JsonRequired] int Year,
    [property: JsonRequired] int Month,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string StartInclusive,
    [property: JsonRequired] string EndExclusive,
    [property: JsonRequired] BudgetPeriodState State);

public sealed record BudgetPlanEntryInput(
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] long PlannedMinorUnits);

public sealed record BudgetPlanEntryDetail(
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] long PlannedMinorUnits,
    string? CurrentDisplayName,
    CategoryLifecycleStatus? CurrentLifecycle);

public sealed record CreateDraftBudgetPlanInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] BudgetPeriodInput Period,
    [property: JsonRequired] IReadOnlyList<BudgetPlanEntryInput> Entries,
    [property: JsonRequired] string Reason);

public sealed record GetBudgetPlanRevisionInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RevisionId);

public sealed record ListBudgetPlanRevisionsInput(
    [property: JsonRequired] string ContractVersion,
    BudgetPeriodInput? Period,
    BudgetRevisionStatus? Status,
    int? Limit);

public sealed record ActivateBudgetPlanRevisionInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RevisionId,
    [property: JsonRequired] string Reason);

public sealed record CategoryLifecycleEvidence(
    [property: JsonRequired] string CategoryId,
    string? CurrentDisplayName,
    [property: JsonRequired] CategoryLifecycleStatus Lifecycle,
    [property: JsonRequired] string CategoryContractVersion);

public sealed record BudgetPlanRevisionDetail(
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] string RevisionId,
    [property: JsonRequired] int RevisionNumber,
    [property: JsonRequired] BudgetRevisionStatus Status,
    [property: JsonRequired] BudgetPeriodDetail Period,
    [property: JsonRequired] string ActorKind,
    [property: JsonRequired] string ActorLabel,
    string? ActorRunId,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] string CategoryContractVersion,
    [property: JsonRequired] string PayloadHash,
    string? ActivatedAt,
    string? SupersededAt,
    string? SupersededByRevisionId,
    [property: JsonRequired] IReadOnlyList<BudgetPlanEntryDetail> Entries,
    [property: JsonRequired] long PlannedTotalMinorUnits,
    [property: JsonRequired] IReadOnlyList<CategoryLifecycleEvidence> CategoryLifecycle);

public sealed record BudgetPlanRevisionSummary(
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] string RevisionId,
    [property: JsonRequired] int RevisionNumber,
    [property: JsonRequired] BudgetRevisionStatus Status,
    [property: JsonRequired] BudgetPeriodDetail Period,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] long PlannedTotalMinorUnits,
    [property: JsonRequired] int EntryCount);

public sealed record CreateDraftBudgetPlanResult(
    [property: JsonRequired] BudgetPlanRevisionDetail Revision);

public sealed record ActivateBudgetPlanRevisionResult(
    [property: JsonRequired] BudgetPlanRevisionDetail Activated,
    BudgetPlanRevisionSummary? Superseded);

public sealed record ListBudgetPlanRevisionsResult(
    [property: JsonRequired] IReadOnlyList<BudgetPlanRevisionSummary> Items,
    string? NextCursor);
