using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Domain.Classify.Rules;

/// <summary>
/// Closed lifecycle policy for CLASSIFY rule-set activation and retirement
/// (FR-CLASSIFY-RULE-LIFECYCLE / FR-CLASSIFY-RULE-VALIDATION / TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE).
/// Pure decision surface: no I/O, no Ledger mutation, no in-place version mutation.
/// </summary>
public static class RuleLifecyclePolicy
{
    public const string StateDraft = "draft";
    public const string StateValidated = "validated";
    public const string StateActive = "active";
    public const string StateRetired = "retired";
    public const string StateActiveBroadApply = "active_with_broad_apply";
    public const string StateSuperseded = "superseded";

    /// <summary>Matches validation_run lifecycle_state for a completed aggregate report.</summary>
    public const string ValidationLifecycleCompleted = "completed";

    public const string EventActivated = "RuleSetActivated";
    public const string EventRetired = "RuleVersionRetired";
    public const string EventSuperseded = "RuleSetSuperseded";

    public const int MaxReasonLength = 1024;
    public const int MaxCandidateSearchCount = 20;

    /// <summary>
    /// Activation may proceed only on a completed validation report with zero incorrect
    /// applications, zero unexplained conflicts, zero drift canaries, and exact row accounting.
    /// </summary>
    public static string? ValidateActivationEvidence(
        ClassificationValidationRunRow? run,
        ClassificationValidationReportRow? report)
    {
        if (run is null || report is null)
        {
            return ClassifyErrors.ValidationNotFound;
        }

        if (!string.Equals(run.LifecycleState, ValidationLifecycleCompleted, StringComparison.Ordinal))
        {
            return ClassifyErrors.Lifecycle;
        }

        if (!string.Equals(run.ValidationRunId, report.ValidationRunId, StringComparison.Ordinal))
        {
            return ClassifyErrors.Integrity;
        }

        if (report.IncorrectApplicationCanaryCount != 0
            || report.UnexplainedConflictCount != 0
            || report.DriftCanaryCount != 0)
        {
            return ClassifyErrors.Lifecycle;
        }

        if (report.TotalRows != report.AccountedRows
            || report.TotalRows != report.SuggestionCount
                + report.NoSuggestionCount
                + report.ConflictCount
                + report.StaleCount)
        {
            return ClassifyErrors.Lifecycle;
        }

        if (report.TotalRows <= 0)
        {
            // Empty evidence cannot authorize a new rule set.
            return ClassifyErrors.Lifecycle;
        }

        return null;
    }

    /// <summary>
    /// Broad apply is false by default. True only when the owner explicitly requests it
    /// against current activation-eligible evidence — never from stale or missing reports.
    /// Does not invent a coverage/benefit percentage threshold.
    /// </summary>
    public static bool AuthorizeBroadApply(
        bool requestedBroadApply,
        ClassificationValidationReportRow report,
        string? evidenceError)
    {
        if (!requestedBroadApply)
        {
            return false;
        }

        if (evidenceError is not null)
        {
            return false;
        }

        return report.IncorrectApplicationCanaryCount == 0
            && report.UnexplainedConflictCount == 0
            && report.DriftCanaryCount == 0
            && report.TotalRows == report.AccountedRows
            && report.TotalRows > 0;
    }

    /// <summary>
    /// Reject broad-apply requests that lack authorization so the mutation aborts without
    /// changing the active pointer.
    /// </summary>
    public static string? ValidateBroadApplyRequest(
        bool requestedBroadApply,
        bool authorizedBroadApply)
    {
        if (requestedBroadApply && !authorizedBroadApply)
        {
            return ClassifyErrors.Lifecycle;
        }

        return null;
    }

    /// <summary>
    /// Category identity must remain active for every candidate. Same-ID rename is allowed
    /// (display name is not part of activation identity). Archive / missing → fail closed.
    /// </summary>
    public static string? ValidateActiveCategoryIdentity(
        IReadOnlyList<string> candidateCategoryIds,
        IReadOnlySet<string> activeCategoryIds)
    {
        ArgumentNullException.ThrowIfNull(candidateCategoryIds);
        ArgumentNullException.ThrowIfNull(activeCategoryIds);

        foreach (var categoryId in candidateCategoryIds)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                return ClassifyErrors.InvalidInput;
            }

            if (!activeCategoryIds.Contains(categoryId))
            {
                return ClassifyErrors.Lifecycle;
            }
        }

        return null;
    }

    /// <summary>
    /// Current catalogue fingerprint must still equal the validation-time fingerprint
    /// (identity + active lifecycle only — renames do not drift this fingerprint).
    /// </summary>
    public static string? ValidateCategoryFingerprintCurrency(
        string validationFingerprint,
        string currentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(validationFingerprint)
            || string.IsNullOrWhiteSpace(currentFingerprint))
        {
            return ClassifyErrors.Stale;
        }

        return string.Equals(validationFingerprint, currentFingerprint, StringComparison.Ordinal)
            ? null
            : ClassifyErrors.Stale;
    }

    /// <summary>
    /// A rule version may be retired only when it is a member of the current active rule set.
    /// </summary>
    public static string? ValidateRetirementMembership(
        string ruleVersionId,
        IReadOnlySet<string> activeMemberIds)
    {
        if (string.IsNullOrWhiteSpace(ruleVersionId))
        {
            return ClassifyErrors.InvalidInput;
        }

        if (activeMemberIds.Count == 0)
        {
            return ClassifyErrors.Lifecycle;
        }

        return activeMemberIds.Contains(ruleVersionId)
            ? null
            : ClassifyErrors.Lifecycle;
    }

    /// <summary>
    /// Successor membership after retirement: prior members minus the retired version, ordered.
    /// </summary>
    public static IReadOnlyList<string> SuccessorMembersAfterRetirement(
        IReadOnlyList<string> activeMemberIds,
        string retiredRuleVersionId) =>
        activeMemberIds
            .Where(id => !string.Equals(id, retiredRuleVersionId, StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

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
    /// Resolve the unique candidate rule-version set whose fingerprint equals the validation
    /// candidate fingerprint. Fail closed when zero or multiple subsets match.
    /// </summary>
    public static string? TryResolveCandidatesByFingerprint(
        IReadOnlyList<ClassifyRuleVersionRow> versions,
        string expectedFingerprint,
        out IReadOnlyList<ClassifyRuleVersionRow> resolved)
    {
        resolved = Array.Empty<ClassifyRuleVersionRow>();
        if (string.IsNullOrWhiteSpace(expectedFingerprint) || expectedFingerprint.Length != 64)
        {
            return ClassifyErrors.Stale;
        }

        if (versions.Count == 0)
        {
            return ClassifyErrors.RuleVersionNotFound;
        }

        var pool = versions
            .OrderBy(v => v.RuleVersionId, StringComparer.Ordinal)
            .ToArray();

        if (pool.Length > MaxCandidateSearchCount)
        {
            if (FingerprintMatches(pool, expectedFingerprint))
            {
                resolved = pool;
                return null;
            }

            return ClassifyErrors.Stale;
        }

        ClassifyRuleVersionRow[]? match = null;
        var matchCount = 0;
        var n = pool.Length;
        var maxMask = 1 << n;
        for (var mask = 1; mask < maxMask; mask++)
        {
            var subset = new List<ClassifyRuleVersionRow>(n);
            for (var i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    subset.Add(pool[i]);
                }
            }

            if (!FingerprintMatches(subset, expectedFingerprint))
            {
                continue;
            }

            matchCount++;
            if (matchCount > 1)
            {
                return ClassifyErrors.Integrity;
            }

            match = subset
                .OrderBy(v => v.RuleVersionId, StringComparer.Ordinal)
                .ToArray();
        }

        if (match is null || match.Length == 0)
        {
            return ClassifyErrors.Stale;
        }

        resolved = match;
        return null;
    }

    /// <summary>
    /// Byte-stable fingerprint over immutable candidate rule versions — same framing as
    /// <c>ValidationReportBuilder.ComputeCandidateFingerprint</c>.
    /// </summary>
    public static string ComputeCandidateFingerprint(
        IReadOnlyList<(string RuleVersionId, string CategoryId, string ScopeHash, string NormalizationVersion, string RuleOrigin)> candidates) =>
        CanonicalClassificationHasher.HashOrderedLines(
            candidates
                .OrderBy(c => c.RuleVersionId, StringComparer.Ordinal)
                .Select(c => string.Concat(
                    c.RuleVersionId, '\t',
                    c.CategoryId, '\t',
                    c.ScopeHash, '\t',
                    c.NormalizationVersion, '\t',
                    c.RuleOrigin)));

    private static bool FingerprintMatches(
        IReadOnlyList<ClassifyRuleVersionRow> candidates,
        string expectedFingerprint)
    {
        var actual = ComputeCandidateFingerprint(
            candidates
                .Select(v => (
                    v.RuleVersionId,
                    v.CategoryId,
                    v.ScopeHash,
                    v.NormalizationVersion,
                    v.RuleOrigin))
                .ToArray());
        return string.Equals(actual, expectedFingerprint, StringComparison.Ordinal);
    }

    public static string ActivationResultingState(bool broadApplyAllowed) =>
        broadApplyAllowed ? StateActiveBroadApply : StateActive;
}
