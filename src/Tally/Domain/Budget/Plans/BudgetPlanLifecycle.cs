using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;

namespace Tally.Domain.Budget.Plans;

/// <summary>
/// Closed lifecycle policy for Budget Plan Revisions (DD-BUDGET-PLAN-REVISION-LIFECYCLE).
/// Transitions are Draft → Active → Superseded only; Closed periods block activation while preserving reads.
/// </summary>
public static class BudgetPlanLifecycle
{
    public const string EventDraftCreated = "DraftCreated";
    public const string EventRevisionActivated = "RevisionActivated";
    public const string EventRevisionSuperseded = "RevisionSuperseded";

    public const int MaxReasonLength = 1024;

    /// <summary>
    /// Validates that a loaded revision may be activated for its period state.
    /// Returns a stable error code, or null when activation may proceed.
    /// </summary>
    public static string? ValidateActivationEligibility(
        BudgetRevisionStatus? status,
        BudgetPeriodState periodState)
    {
        if (status is null)
        {
            return BudgetErrors.RevisionNotFound;
        }

        if (periodState == BudgetPeriodState.Closed)
        {
            return BudgetErrors.InvalidPeriod;
        }

        if (status != BudgetRevisionStatus.Draft)
        {
            // Active or Superseded cannot be (re)activated; fail closed without mutation.
            return BudgetErrors.Conflict;
        }

        return null;
    }

    /// <summary>True only for Draft — the sole status that may transition to Active.</summary>
    public static bool IsActivatable(BudgetRevisionStatus status) =>
        status == BudgetRevisionStatus.Draft;

    /// <summary>True when period eligibility permits activation (Current or Future).</summary>
    public static bool IsPeriodOpenForActivation(BudgetPeriodState periodState) =>
        periodState is BudgetPeriodState.Current or BudgetPeriodState.Future;

    /// <summary>
    /// Whether activation must supersede a prior Active revision of the same plan
    /// inside the same atomic mutation.
    /// </summary>
    public static bool RequiresSupersession(string? priorActiveRevisionId) =>
        !string.IsNullOrWhiteSpace(priorActiveRevisionId);

    /// <summary>
    /// Builds the ordered lifecycle event id list for an activation outcome:
    /// optional supersession first, then activation (matches store event sequence).
    /// </summary>
    public static IReadOnlyList<string> OrderedActivationEventIds(
        string? supersedeEventId,
        string activateEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activateEventId);
        if (string.IsNullOrWhiteSpace(supersedeEventId))
        {
            return [activateEventId];
        }

        return [supersedeEventId, activateEventId];
    }

    /// <summary>Normalizes a non-blank bounded owner reason (control characters rejected).</summary>
    public static bool TryNormalizeReason(string? reason, out string normalized)
    {
        normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxReasonLength)
        {
            return false;
        }

        foreach (var ch in normalized)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Allowed status transitions under the closed enum policy.
    /// Payload content never transitions — only constrained lifecycle columns.
    /// </summary>
    public static bool IsAllowedTransition(BudgetRevisionStatus from, BudgetRevisionStatus to) =>
        (from, to) switch
        {
            (BudgetRevisionStatus.Draft, BudgetRevisionStatus.Active) => true,
            (BudgetRevisionStatus.Active, BudgetRevisionStatus.Superseded) => true,
            _ => false
        };
}
