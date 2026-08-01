namespace Tally.Domain.Classify.Recovery;

/// <summary>
/// Pure next-action policy for classify.status (FR-CLASSIFY-STATUS-HISTORY).
/// Each durable subject maps to exactly one permitted next-operation enum value
/// from retry, resume, re-evaluate, correct, abandon, cleanup, or none.
/// No I/O — callers supply durable lifecycle and aggregate facts only.
/// </summary>
public static class SafeNextActionPolicy
{
    public const string Retry = "retry";
    public const string Resume = "resume";
    public const string ReEvaluate = "re-evaluate";
    public const string Correct = "correct";
    public const string Abandon = "abandon";
    public const string Cleanup = "cleanup";
    public const string None = "none";

    public const string LifecycleAbandoned = "abandoned";
    public const string LifecycleRetained = "retained";
    public const string LifecycleExpired = "expired";
    public const string LifecycleRecorded = "recorded";
    public const string LifecycleCompleted = "completed";
    public const string LifecycleRunning = "running";
    public const string LifecycleFailed = "failed";
    public const string LifecycleDraft = "draft";
    public const string LifecycleActive = "active";
    public const string LifecycleRetired = "retired";
    public const string LifecycleValidated = "validated";
    public const string LifecycleSuperseded = "superseded";

    /// <summary>
    /// Bounded status decision: durable lifecycle, whether CLASSIFY or LEDGER mutation
    /// may have occurred, and exactly one next-safe operation enum value.
    /// </summary>
    public sealed record Decision(
        string LifecycleState,
        bool MutationMayHaveOccurred,
        string NextSafeOperationId);

    public static Decision ForRuleVersion(
        string lifecycleState,
        bool isTombstoned,
        bool isReferenced)
    {
        if (isTombstoned)
        {
            return new Decision(LifecycleAbandoned, MutationMayHaveOccurred: false, Cleanup);
        }

        var state = NormalizeLifecycle(lifecycleState);
        return state switch
        {
            LifecycleDraft when !isReferenced =>
                new Decision(LifecycleDraft, false, Abandon),
            LifecycleDraft =>
                new Decision(LifecycleDraft, false, None),
            LifecycleValidated =>
                new Decision(LifecycleValidated, false, None),
            LifecycleActive =>
                new Decision(LifecycleActive, false, None),
            LifecycleRetired =>
                new Decision(LifecycleRetired, false, None),
            LifecycleSuperseded =>
                new Decision(LifecycleSuperseded, false, None),
            LifecycleAbandoned =>
                new Decision(LifecycleAbandoned, false, Cleanup),
            _ =>
                new Decision(state, false, None)
        };
    }

    public static Decision ForValidationRun(string lifecycleState)
    {
        var state = NormalizeLifecycle(lifecycleState);
        return state switch
        {
            LifecycleRunning => new Decision(LifecycleRunning, false, Retry),
            LifecycleFailed => new Decision(LifecycleFailed, false, Retry),
            LifecycleAbandoned => new Decision(LifecycleAbandoned, false, Cleanup),
            LifecycleCompleted => new Decision(LifecycleCompleted, false, None),
            _ => new Decision(state, false, None)
        };
    }

    public static Decision ForEvaluationRun(string lifecycleState, int conflictCount)
    {
        var state = NormalizeLifecycle(lifecycleState);
        return state switch
        {
            LifecycleRunning => new Decision(LifecycleRunning, false, ReEvaluate),
            LifecycleFailed => new Decision(LifecycleFailed, false, ReEvaluate),
            LifecycleAbandoned => new Decision(LifecycleAbandoned, false, Cleanup),
            LifecycleCompleted when conflictCount > 0 =>
                new Decision(LifecycleCompleted, false, Correct),
            LifecycleCompleted => new Decision(LifecycleCompleted, false, None),
            _ => new Decision(state, false, None)
        };
    }

    public static Decision ForPreview(
        bool isTombstoned,
        bool isExpired,
        bool hasApplyRun)
    {
        if (isTombstoned)
        {
            return new Decision(LifecycleAbandoned, false, Cleanup);
        }

        if (isExpired)
        {
            return new Decision(LifecycleExpired, false, Abandon);
        }

        if (hasApplyRun)
        {
            // Preview already authorized an apply — further preview work is none.
            return new Decision(LifecycleRetained, false, None);
        }

        return new Decision(LifecycleRetained, false, None);
    }

    /// <summary>
    /// Apply status: terminal totals and unresolved frontier drive resume vs none.
    /// Mutation may have occurred when any item reached applied/already_applied
    /// (Ledger category mutation path) or the run is mid-flight with prior progress.
    /// </summary>
    public static Decision ForApplyRun(
        string lifecycleState,
        int unresolvedFrontier,
        int appliedCount,
        int alreadyAppliedCount,
        int failedCount)
    {
        var state = NormalizeLifecycle(lifecycleState);
        var mutation =
            appliedCount > 0
            || alreadyAppliedCount > 0
            || (state == LifecycleRunning && unresolvedFrontier >= 0 && (appliedCount + alreadyAppliedCount + failedCount) > 0)
            || state is LifecycleCompleted or LifecycleFailed;

        // Tighten: completed/failed always admit mutation possibility only when any ledger-affecting item exists.
        if (state is LifecycleCompleted or LifecycleFailed)
        {
            mutation = appliedCount > 0 || alreadyAppliedCount > 0;
        }

        if (state == LifecycleAbandoned)
        {
            return new Decision(LifecycleAbandoned, mutation, Cleanup);
        }

        if (state == LifecycleRunning || unresolvedFrontier > 0)
        {
            // Resume is safe when frozen request remains and frontier is non-empty.
            return new Decision(
                unresolvedFrontier > 0 ? LifecycleRunning : state,
                mutation || appliedCount > 0 || alreadyAppliedCount > 0,
                Resume);
        }

        if (state == LifecycleFailed || failedCount > 0)
        {
            return new Decision(LifecycleFailed, mutation, Retry);
        }

        return new Decision(LifecycleCompleted, mutation, None);
    }

    public static Decision ForFeedback(string decisionType)
    {
        // Feedback is append-only; CLASSIFY mutation is the feedback row itself (not LEDGER).
        // LEDGER mutation may already have occurred before feedback was recorded.
        _ = decisionType;
        return new Decision(LifecycleRecorded, MutationMayHaveOccurred: true, None);
    }

    public static Decision ForAbandonment(int removedPayloadCount) =>
        new(
            LifecycleAbandoned,
            MutationMayHaveOccurred: removedPayloadCount > 0,
            removedPayloadCount > 0 ? None : Cleanup);

    public static Decision ForCleanup(int removedArtifactCount) =>
        new(
            LifecycleCompleted,
            MutationMayHaveOccurred: removedArtifactCount > 0,
            None);

    public static bool IsKnownNextAction(string? next) =>
        next is Retry or Resume or ReEvaluate or Correct or Abandon or Cleanup or None;

    private static string NormalizeLifecycle(string lifecycleState) =>
        string.IsNullOrWhiteSpace(lifecycleState)
            ? "unknown"
            : lifecycleState.Trim();
}
