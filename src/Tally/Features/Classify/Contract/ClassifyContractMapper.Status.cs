using System.Globalization;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure classify.status mapping (FR-CLASSIFY-STATUS-HISTORY / TASK-CLASSIFY-RULEBOOK-STATUS-WORKFLOW).
/// Projects durable metadata only — never private corpus rows, Ledger payloads, free-text reasons,
/// paths, amounts, tokens, or serialized request/result bodies.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string StatusStalenessFresh = "fresh";
    public const string StatusStalenessExpired = "expired";
    public const string StatusStalenessUnknown = "unknown";
    public const string StatusStalenessAbandoned = "abandoned";

    public const string StatusReasonDraft = "rule_draft";
    public const string StatusReasonValidated = "rule_validated";
    public const string StatusReasonActive = "rule_active";
    public const string StatusReasonRetired = "rule_retired";
    public const string StatusReasonAbandoned = "abandoned";
    public const string StatusReasonTombstone = "tombstone";
    public const string StatusReasonFeedbackAccept = "feedback_accept";
    public const string StatusReasonFeedbackReject = "feedback_reject";
    public const string StatusReasonFeedbackCorrect = "feedback_correct";
    public const string StatusReasonFeedbackRecorded = "feedback_recorded";

    /// <summary>Documented maximum rule versions returned in one status history projection.</summary>
    public const int MaxStatusRuleVersionHistory = 500;

    public static ClassifyStatusResult ToStatusResult(
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        SafeNextActionPolicy.Decision decision,
        ClassifyRuleStatusDetail? rule = null,
        ClassifyValidationStatusDetail? validation = null,
        ClassifyEvaluationStatusDetail? evaluation = null,
        ClassifyPreviewStatusDetail? preview = null,
        ClassifyApplyStatusDetail? apply = null,
        ClassifyFeedbackStatusDetail? feedback = null,
        ClassifyAbandonmentStatusDetail? abandonment = null,
        ClassifyCleanupStatusDetail? cleanup = null) =>
        new(
            ClassifyOperationIds.ContractVersion,
            subjectType,
            subjectId.Trim(),
            decision.LifecycleState,
            decision.MutationMayHaveOccurred,
            decision.NextSafeOperationId,
            Rule: rule,
            Validation: validation,
            Evaluation: evaluation,
            Preview: preview,
            Apply: apply,
            Feedback: feedback,
            Abandonment: abandonment,
            Cleanup: cleanup);

    public static string ToRuleReasonCode(string lifecycleState, bool isTombstoned) =>
        isTombstoned
            ? StatusReasonAbandoned
            : NormalizeLifecycle(lifecycleState) switch
            {
                "draft" => StatusReasonDraft,
                "validated" => StatusReasonValidated,
                "active" => StatusReasonActive,
                "retired" => StatusReasonRetired,
                "abandoned" => StatusReasonAbandoned,
                _ => "rule_lifecycle"
            };

    public static string ToFeedbackReasonCode(string decisionType, string? proposalLifecycle) =>
        NormalizeLifecycle(decisionType) switch
        {
            "accept" => StatusReasonFeedbackAccept,
            "reject" => StatusReasonFeedbackReject,
            "correct" => string.IsNullOrWhiteSpace(proposalLifecycle)
                ? StatusReasonFeedbackCorrect
                : StatusReasonFeedbackCorrect,
            _ => StatusReasonFeedbackRecorded
        };

    /// <summary>
    /// Closed retained staleness from durable timestamps only — never Ledger/corpus reread.
    /// </summary>
    public static string DeriveDurableStalenessState(
        string lifecycleState,
        string? snapshotExpiresAtUtc,
        DateTimeOffset nowUtc)
    {
        var life = NormalizeLifecycle(lifecycleState);
        if (life is "abandoned")
        {
            return StatusStalenessAbandoned;
        }

        if (string.IsNullOrWhiteSpace(snapshotExpiresAtUtc))
        {
            return life is "completed" or "failed" or "running" or "retained"
                ? StatusStalenessUnknown
                : StatusStalenessUnknown;
        }

        if (!DateTimeOffset.TryParse(
                snapshotExpiresAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expires))
        {
            return StatusStalenessUnknown;
        }

        return expires <= nowUtc ? StatusStalenessExpired : StatusStalenessFresh;
    }

    public static string ComputeEvaluationStatusFingerprint(ClassifyEvaluationRunRow run) =>
        CanonicalClassificationHasher.HashParts(
            run.EvaluationId,
            run.RuleSetVersionId,
            run.NormalizationVersion,
            run.LedgerContractVersion,
            run.ProjectionVersion,
            run.StoreGenerationFingerprint,
            run.SnapshotId,
            run.SnapshotExpiresAt,
            run.CategoryLifecycleFingerprint,
            run.OrderedItemsFingerprint,
            run.InputCount.ToString(CultureInfo.InvariantCulture),
            run.SuggestionCount.ToString(CultureInfo.InvariantCulture),
            run.NoSuggestionCount.ToString(CultureInfo.InvariantCulture),
            run.ConflictCount.ToString(CultureInfo.InvariantCulture),
            run.StaleCount.ToString(CultureInfo.InvariantCulture),
            run.LifecycleState);

    /// <summary>
    /// Terminal apply item totals used for public apply detail and next-action decisions.
    /// Never embeds transaction descriptions, amounts, or private error payloads.
    /// </summary>
    public static ApplyStatusTotals ToApplyStatusTotals(IReadOnlyList<ClassifyApplyItemRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var applied = 0;
        var alreadyApplied = 0;
        var rejected = 0;
        var failed = 0;
        var unresolved = 0;
        foreach (var item in items)
        {
            switch (ApplyReplayPolicy.ToPublicKind(item.ItemState))
            {
                case ClassifyApplyItemResultKind.Applied:
                    applied++;
                    break;
                case ClassifyApplyItemResultKind.AlreadyApplied:
                    alreadyApplied++;
                    break;
                case ClassifyApplyItemResultKind.Rejected:
                    rejected++;
                    break;
                case ClassifyApplyItemResultKind.Failed:
                    failed++;
                    break;
                default:
                    unresolved++;
                    break;
            }
        }

        return new ApplyStatusTotals(applied, alreadyApplied, rejected, failed, unresolved);
    }

    public static (bool ReplaySafe, bool ResumeSafe) ToApplySafetyFlags(
        string lifecycleState,
        int unresolvedFrontier)
    {
        var life = NormalizeLifecycle(lifecycleState);
        var resumeSafe = life == "running" && unresolvedFrontier > 0;
        var replaySafe = life == "completed" && unresolvedFrontier == 0;
        return (replaySafe, resumeSafe);
    }

    public static string DerivePreviewLifecycle(bool isTombstoned, bool isExpired) =>
        isTombstoned
            ? SafeNextActionPolicy.LifecycleAbandoned
            : isExpired
                ? SafeNextActionPolicy.LifecycleExpired
                : SafeNextActionPolicy.LifecycleRetained;

    public static ClassifyRuleStatusDetail ToRuleStatusDetail(
        string ruleId,
        string? activeRuleSetVersionId,
        IReadOnlyList<ClassifyRuleStatusVersion> versions) =>
        new(activeRuleSetVersionId, ruleId.Trim(), versions);

    public static ClassifyRuleStatusVersion ToRuleStatusVersion(
        ClassifyRuleVersionRow version,
        IReadOnlyList<string> ruleSetVersionIds,
        IReadOnlyList<string> successorRuleVersionIds,
        bool isTombstoned) =>
        new(
            version.RuleVersionId,
            ruleSetVersionIds,
            isTombstoned ? SafeNextActionPolicy.LifecycleAbandoned : version.LifecycleState,
            version.CreatedBy,
            ToRuleReasonCode(version.LifecycleState, isTombstoned),
            version.CreatedAt,
            version.PriorVersionId,
            successorRuleVersionIds);

    public static ClassifyValidationStatusDetail ToValidationStatusDetail(
        ClassificationValidationRunRow run,
        ClassificationValidationReportRow? report,
        string stalenessState) =>
        new(
            run.CandidateFingerprint,
            run.CorpusFingerprint,
            run.ExpectedOutcomeFingerprint,
            report?.ReportFingerprint,
            run.LifecycleState,
            report?.TotalRows ?? 0,
            report?.AccountedRows ?? 0,
            report?.SuggestionCount ?? 0,
            report?.NoSuggestionCount ?? 0,
            report?.ConflictCount ?? 0,
            report?.StaleCount ?? 0,
            report?.ActivationEligible,
            run.Actor,
            run.StartedAt,
            run.CompletedAt,
            stalenessState);

    public static ClassifyEvaluationStatusDetail ToEvaluationStatusDetail(
        ClassifyEvaluationRunRow run,
        string evaluationFingerprint,
        string stalenessState) =>
        new(
            evaluationFingerprint,
            run.RuleSetVersionId,
            run.NormalizationVersion,
            run.InputCount,
            run.SuggestionCount,
            run.NoSuggestionCount,
            run.ConflictCount,
            run.StaleCount,
            run.Actor,
            run.CreatedAt,
            stalenessState);

    public static ClassifyPreviewStatusDetail ToPreviewStatusDetail(
        ClassifyApplyPreviewRow preview,
        string lifecycleState) =>
        new(
            preview.PreviewId,
            preview.EvaluationId,
            preview.EvaluationFingerprint,
            preview.SelectionHash,
            preview.SelectedCount,
            preview.ExclusionCount,
            preview.NoSuggestionCount,
            preview.ConflictCount,
            preview.ExpiresAt,
            lifecycleState,
            preview.Actor,
            preview.CreatedAt);

    public static ClassifyApplyStatusDetail ToApplyStatusDetail(
        ClassifyApplyRunRow run,
        ApplyStatusTotals totals,
        int unresolvedFrontier,
        bool replaySafe,
        bool resumeSafe) =>
        new(
            run.ApplyId,
            run.PreviewId,
            run.RequestFingerprint,
            totals.AppliedCount,
            totals.AlreadyAppliedCount,
            totals.RejectedCount,
            totals.FailedCount,
            totals.UnresolvedCount,
            unresolvedFrontier,
            replaySafe,
            resumeSafe,
            run.Actor,
            run.StartedAt,
            run.CompletedAt);

    public static ClassifyFeedbackStatusDetail ToFeedbackStatusDetail(
        ClassifyFeedbackRow feedback,
        ClassifyRuleProposalRow? proposal,
        IReadOnlyList<string> ruleVersionIds) =>
        new(
            feedback.FeedbackId,
            feedback.OutcomeId,
            feedback.DecisionType,
            proposal?.ProposalId,
            proposal?.LifecycleState,
            feedback.Actor,
            ToFeedbackReasonCode(feedback.DecisionType, proposal?.LifecycleState),
            feedback.OccurredAt,
            ruleVersionIds);

    public static ClassifyAbandonmentStatusDetail ToAbandonmentStatusDetail(
        ClassifyAbandonmentTombstoneRow tombstone) =>
        new(
            tombstone.TombstoneId,
            tombstone.SubjectType,
            tombstone.SubjectId,
            tombstone.Actor,
            StatusReasonTombstone,
            tombstone.AbandonedAt,
            tombstone.RemovedPayloadCount);

    public static ClassifyCleanupStatusDetail ToCleanupStatusDetail(
        string cleanupId,
        string policyVersion,
        string actor,
        string occurredAt,
        int removedArtifactCount,
        int retainedArtifactCount,
        int recognizedRemovedCount,
        int expiredPreviewCount,
        int abandonedPayloadCount) =>
        new(
            cleanupId,
            policyVersion,
            actor,
            occurredAt,
            removedArtifactCount,
            retainedArtifactCount,
            recognizedRemovedCount,
            expiredPreviewCount,
            abandonedPayloadCount);

    /// <summary>
    /// Exactly one detail non-null and matching subject type — pure structural check.
    /// </summary>
    public static bool HasExactlyOneMatchingDetail(ClassifyStatusResult result) =>
        result.SubjectType switch
        {
            ClassifyStatusSubjectType.Rule =>
                result.Rule is not null && Only(result, rule: true),
            ClassifyStatusSubjectType.Validation =>
                result.Validation is not null && Only(result, validation: true),
            ClassifyStatusSubjectType.Evaluation =>
                result.Evaluation is not null && Only(result, evaluation: true),
            ClassifyStatusSubjectType.Preview =>
                result.Preview is not null && Only(result, preview: true),
            ClassifyStatusSubjectType.Apply =>
                result.Apply is not null && Only(result, apply: true),
            ClassifyStatusSubjectType.Feedback =>
                result.Feedback is not null && Only(result, feedback: true),
            ClassifyStatusSubjectType.Abandonment =>
                result.Abandonment is not null && Only(result, abandonment: true),
            ClassifyStatusSubjectType.Cleanup =>
                result.Cleanup is not null && Only(result, cleanup: true),
            _ => false
        };

    private static bool Only(
        ClassifyStatusResult result,
        bool rule = false,
        bool validation = false,
        bool evaluation = false,
        bool preview = false,
        bool apply = false,
        bool feedback = false,
        bool abandonment = false,
        bool cleanup = false) =>
        (result.Rule is not null) == rule
        && (result.Validation is not null) == validation
        && (result.Evaluation is not null) == evaluation
        && (result.Preview is not null) == preview
        && (result.Apply is not null) == apply
        && (result.Feedback is not null) == feedback
        && (result.Abandonment is not null) == abandonment
        && (result.Cleanup is not null) == cleanup;

    private static string NormalizeLifecycle(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public sealed record ApplyStatusTotals(
        int AppliedCount,
        int AlreadyAppliedCount,
        int RejectedCount,
        int FailedCount,
        int UnresolvedCount);
}
