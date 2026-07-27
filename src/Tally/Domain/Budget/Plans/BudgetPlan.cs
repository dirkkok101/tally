using Tally.Domain.Budget.Periods;

namespace Tally.Domain.Budget.Plans;

/// <summary>
/// Stable Budget Plan identity for one explicit ZAR calendar month (DM-BUDGET-PERIOD-PLAN).
/// The Active pointer is nullable and is never changed by draft creation.
/// </summary>
public sealed record BudgetPlan(
    string PlanId,
    BudgetPeriod Period,
    string? ActiveRevisionId,
    DateTimeOffset CreatedAtUtc);
