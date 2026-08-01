using Tally.Contracts.Classify.Operations;

namespace Tally.Domain.Classify.Recovery;

/// <summary>
/// Pure RESTRICT retention and removable-artifact policy
/// (FR-CLASSIFY-STATE-RETENTION-CLEANUP / DD-CLASSIFY-ARTIFACT-RETENTION / ADR-CORE-0020).
/// Never invents filesystem IO; never authorizes arbitrary paths.
/// </summary>
public static class ClassifyRetentionPolicy
{
    /// <summary>Fixed cleanup policy version accepted by classify.cleanup (no path argument).</summary>
    public const string PolicyVersion = "cleanup_v1";

    public const string SubjectTypeRule = "rule";
    public const string SubjectTypeValidation = "validation";
    public const string SubjectTypeEvaluation = "evaluation";
    public const string SubjectTypePreview = "preview";
    public const string SubjectTypeApply = "apply";
    public const string SubjectTypeFeedback = "feedback";

    /// <summary>Closed recognized temporary file name prefixes under classify/tmp (TopDirectoryOnly).</summary>
    public static IReadOnlyList<string> RecognizedTemporaryPrefixes { get; } =
    [
        "tmp-",
        "eval-",
        "val-",
        "crash-",
        "partial-",
        "scratch-"
    ];

    /// <summary>Closed recognized temporary file suffixes.</summary>
    public static IReadOnlyList<string> RecognizedTemporarySuffixes { get; } =
    [
        ".tmp",
        ".partial",
        ".scratch"
    ];

    [Flags]
    public enum ReferenceFlags
    {
        None = 0,
        ActiveRuleSetMember = 1 << 0,
        MatchEvidence = 1 << 1,
        RuleProposal = 1 << 2,
        ApplyPreviewItem = 1 << 3,
        ApplyRun = 1 << 4,
        ApplyPreviewEvaluation = 1 << 5,
        Feedback = 1 << 6,
        RuleSetValidation = 1 << 7,
        AlreadyTombstoned = 1 << 8,
        NotFound = 1 << 9,
        NotDraft = 1 << 10,
        LedgerProvenance = 1 << 11
    }

    public sealed record AbandonDecision(
        bool Allowed,
        string? ErrorCode,
        string DecisionCode,
        IReadOnlyList<string> BlockerFlags);

    public static bool IsSupportedCleanupPolicyVersion(string? policyVersion) =>
        string.Equals(policyVersion, PolicyVersion, StringComparison.Ordinal);

    public static bool IsAbandonableSubjectType(ClassifyStatusSubjectType subjectType) =>
        subjectType is ClassifyStatusSubjectType.Rule
            or ClassifyStatusSubjectType.Validation
            or ClassifyStatusSubjectType.Evaluation
            or ClassifyStatusSubjectType.Preview;

    public static bool IsAlwaysRestrictedSubjectType(ClassifyStatusSubjectType subjectType) =>
        subjectType is ClassifyStatusSubjectType.Apply
            or ClassifyStatusSubjectType.Feedback
            or ClassifyStatusSubjectType.Abandonment
            or ClassifyStatusSubjectType.Cleanup;

    public static string FormatSubjectType(ClassifyStatusSubjectType subjectType) => subjectType switch
    {
        ClassifyStatusSubjectType.Rule => SubjectTypeRule,
        ClassifyStatusSubjectType.Validation => SubjectTypeValidation,
        ClassifyStatusSubjectType.Evaluation => SubjectTypeEvaluation,
        ClassifyStatusSubjectType.Preview => SubjectTypePreview,
        ClassifyStatusSubjectType.Apply => SubjectTypeApply,
        ClassifyStatusSubjectType.Feedback => SubjectTypeFeedback,
        ClassifyStatusSubjectType.Abandonment => "abandonment",
        ClassifyStatusSubjectType.Cleanup => "cleanup",
        _ => subjectType.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Decide whether abandon may proceed. Referenced history is RESTRICT-retained.
    /// </summary>
    public static AbandonDecision EvaluateAbandon(
        ClassifyStatusSubjectType subjectType,
        ReferenceFlags references)
    {
        if (IsAlwaysRestrictedSubjectType(subjectType))
        {
            return Denied(
                ClassifyErrors.Lifecycle,
                "restricted_subject_type",
                ["restricted_subject_type"]);
        }

        if (!IsAbandonableSubjectType(subjectType))
        {
            return Denied(
                ClassifyErrors.InvalidInput,
                "subject_type_not_abandonable",
                ["subject_type_not_abandonable"]);
        }

        if (references.HasFlag(ReferenceFlags.NotFound))
        {
            return Denied(ClassifyErrors.NotFound, "subject_not_found", ["not_found"]);
        }

        if (references.HasFlag(ReferenceFlags.AlreadyTombstoned))
        {
            return Denied(ClassifyErrors.Lifecycle, "already_abandoned", ["already_tombstoned"]);
        }

        var blockers = new List<string>();
        if (references.HasFlag(ReferenceFlags.ActiveRuleSetMember))
        {
            blockers.Add("active_rule_set_member");
        }

        if (references.HasFlag(ReferenceFlags.MatchEvidence))
        {
            blockers.Add("match_evidence");
        }

        if (references.HasFlag(ReferenceFlags.RuleProposal))
        {
            blockers.Add("rule_proposal");
        }

        if (references.HasFlag(ReferenceFlags.ApplyPreviewItem))
        {
            blockers.Add("apply_preview_item");
        }

        if (references.HasFlag(ReferenceFlags.ApplyRun))
        {
            blockers.Add("apply_run");
        }

        if (references.HasFlag(ReferenceFlags.ApplyPreviewEvaluation))
        {
            blockers.Add("apply_preview");
        }

        if (references.HasFlag(ReferenceFlags.Feedback))
        {
            blockers.Add("feedback");
        }

        if (references.HasFlag(ReferenceFlags.RuleSetValidation))
        {
            blockers.Add("rule_set_validation");
        }

        if (references.HasFlag(ReferenceFlags.LedgerProvenance))
        {
            blockers.Add("ledger_provenance");
        }

        if (subjectType == ClassifyStatusSubjectType.Rule
            && references.HasFlag(ReferenceFlags.NotDraft))
        {
            blockers.Add("not_draft");
        }

        if (blockers.Count > 0)
        {
            return Denied(ClassifyErrors.Lifecycle, "referenced_restrict", blockers);
        }

        return new AbandonDecision(true, null, "abandon_allowed", Array.Empty<string>());
    }

    /// <summary>
    /// Recognized temporary names only — never arbitrary paths or unknown globs.
    /// File name only (no directory separators).
    /// </summary>
    public static bool IsRecognizedTemporaryFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..")
        {
            return false;
        }

        foreach (var prefix in RecognizedTemporaryPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var suffix in RecognizedTemporarySuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsExpired(string expiresAtUtc, DateTimeOffset nowUtc)
    {
        if (!DateTimeOffset.TryParse(
                expiresAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expires))
        {
            // Unparseable expiry fails closed as expired (safe to tombstone candidate only after other checks).
            return true;
        }

        return nowUtc >= expires;
    }

    /// <summary>
    /// Candidate temporary may be removed only when recognized, contained, regular, owner-only, and unlocked.
    /// </summary>
    public static bool MayRemoveTemporaryArtifact(
        bool isRecognizedName,
        bool isContainedInClassifyRoot,
        bool isRegularFile,
        bool isOwnerOnly,
        bool isSymlink,
        bool appearsLocked)
    {
        if (!isRecognizedName || !isContainedInClassifyRoot || !isRegularFile || !isOwnerOnly)
        {
            return false;
        }

        if (isSymlink || appearsLocked)
        {
            return false;
        }

        return true;
    }

    private static AbandonDecision Denied(string error, string code, IReadOnlyList<string> blockers) =>
        new(false, error, code, blockers);
}
