using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;

namespace Tally.Domain.Classify.Feedback;

/// <summary>
/// Deterministic bounded proposal policy for classify.feedback.record
/// (FR-CLASSIFY-CORRECTION-FEEDBACK / DM-CLASSIFY-FEEDBACK-PROPOSAL).
/// Emits no proposal or exactly one smallest-scope retire/narrow/replace draft.
/// Never broadens, activates, reconstructs missing MatchEvidence, or infers a general rule
/// from one correction without retained evidence.
/// </summary>
public static class FeedbackProposalBuilder
{
    public const string ProposalTypeNone = "none";
    public const string ProposalTypeRetire = "retire";
    public const string ProposalTypeNarrow = "narrow";
    public const string ProposalTypeReplace = "replace";

    public const string RuleOriginFeedbackDerived = "feedback_derived";
    public const string LifecycleDraft = "draft";

    public enum ProposalKind
    {
        None,
        Retire,
        Narrow,
        Replace
    }

    /// <summary>Inputs are retained CLASSIFY evidence only — never live Ledger descriptions or tokens.</summary>
    public sealed record Input(
        ClassifyFeedbackDecision Decision,
        ClassificationOutcomeKind OutcomeKind,
        bool EvidenceAvailable,
        IReadOnlyList<ClassifyMatchEvidenceRow> RetainedEvidence,
        /// <summary>Immutable rule_version rows named by retained evidence only (may be empty).</summary>
        IReadOnlyDictionary<string, ClassifyRuleVersionRow> SourceRulesByVersionId,
        /// <summary>Resulting Ledger category id for a completed correction, when known.</summary>
        string? ResultingCategoryId,
        /// <summary>True when both prior and resulting allocation identities were supplied or resolved.</summary>
        bool CorrectionAllocationsComplete);

    public sealed record Result(
        ProposalKind Kind,
        string ProposalTypeWire,
        string? SourceRuleVersionId,
        string ProposedScopeFingerprint,
        string? ProposedCategoryId,
        string DecisionCode);

    /// <summary>
    /// Build at most one non-active proposal. Missing evidence → feedback only (None).
    /// Accept/reject → None. Multi-rule attribution → None (never generalize).
    /// </summary>
    public static Result Build(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.RetainedEvidence);
        ArgumentNullException.ThrowIfNull(input.SourceRulesByVersionId);

        var noneFingerprint = CanonicalNoneScopeFingerprint(input.Decision);

        // Accept / reject never invent a rule change from a single owner decision.
        if (input.Decision is ClassifyFeedbackDecision.Accepted or ClassifyFeedbackDecision.Rejected)
        {
            return None("accept_or_reject_no_proposal", noneFingerprint);
        }

        if (input.Decision != ClassifyFeedbackDecision.Corrected)
        {
            return None("unknown_decision", noneFingerprint);
        }

        // A caller-supplied pair is not correction authority. Proposals require the
        // complete outcome-scoped pair rebound from retained apply provenance.
        if (!input.CorrectionAllocationsComplete)
        {
            return None("correction_authority_unavailable", noneFingerprint);
        }

        // Unavailable prior MatchEvidence → store owner decision only; never reconstruct.
        if (!input.EvidenceAvailable || input.RetainedEvidence.Count == 0)
        {
            return None("missing_match_evidence", noneFingerprint);
        }

        if (input.OutcomeKind is ClassificationOutcomeKind.NoSuggestion
            or ClassificationOutcomeKind.Stale)
        {
            return None("outcome_not_suggestion", noneFingerprint);
        }

        var contributing = input.RetainedEvidence
            .Select(e => e.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (contributing.Length == 0)
        {
            return None("no_contributing_rules", noneFingerprint);
        }

        // Multi-rule outcomes are not generalized from one correction (smallest safe scope = none).
        if (contributing.Length != 1)
        {
            return None("multi_rule_not_generalized", noneFingerprint);
        }

        var sourceRuleId = contributing[0];
        if (!input.SourceRulesByVersionId.TryGetValue(sourceRuleId, out var sourceRule)
            || string.IsNullOrWhiteSpace(sourceRule.ScopeHash)
            || sourceRule.ScopeHash.Length != 64
            || string.IsNullOrWhiteSpace(sourceRule.CategoryId))
        {
            // Never invent source identity from current catalogue.
            return None("source_rule_unavailable", noneFingerprint);
        }

        var conditionIds = input.RetainedEvidence
            .Where(e => string.Equals(e.RuleVersionId, sourceRuleId, StringComparison.Ordinal))
            .Select(e => e.ConditionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (conditionIds.Length == 0)
        {
            return None("missing_condition_evidence", noneFingerprint);
        }

        var resulting = string.IsNullOrWhiteSpace(input.ResultingCategoryId)
            ? null
            : input.ResultingCategoryId.Trim();

        // Retire: correction without a resulting category identity (cannot form a replacement).
        if (resulting is null)
        {
            return new Result(
                ProposalKind.Retire,
                ProposalTypeRetire,
                sourceRuleId,
                sourceRule.ScopeHash,
                ProposedCategoryId: null,
                DecisionCode: "retire_single_rule");
        }

        // Replace: different category — keep exact retained scope hash; never broaden.
        if (!string.Equals(resulting, sourceRule.CategoryId, StringComparison.Ordinal))
        {
            return new Result(
                ProposalKind.Replace,
                ProposalTypeReplace,
                sourceRuleId,
                sourceRule.ScopeHash,
                resulting,
                DecisionCode: "replace_category_same_scope");
        }

        // An AND rule becomes broader, not narrower, when a condition is removed.
        // Match evidence from one corrected row cannot identify a safe additional
        // predicate, so same-category corrections remain transaction-specific.
        if (conditionIds.Length >= 2)
        {
            return None("narrowing_not_proven", noneFingerprint);
        }

        // Single-condition, same category correction — transaction-specific; no reusable draft.
        return None("transaction_specific_same_category", noneFingerprint);
    }

    public static string FormatProposalType(ProposalKind kind) => kind switch
    {
        ProposalKind.None => ProposalTypeNone,
        ProposalKind.Retire => ProposalTypeRetire,
        ProposalKind.Narrow => ProposalTypeNarrow,
        ProposalKind.Replace => ProposalTypeReplace,
        _ => ProposalTypeNone
    };

    public static bool IsActiveProposal(ProposalKind kind) => kind != ProposalKind.None;

    /// <summary>
    /// Evidence is available only when suggestion/conflict rows have complete retained match evidence.
    /// </summary>
    public static bool IsEvidenceAvailable(
        ClassificationOutcomeKind kind,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var contributing = evidence
            .Select(e => e.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return kind switch
        {
            ClassificationOutcomeKind.Suggestion => evidence.Count > 0 && contributing.Length >= 1,
            ClassificationOutcomeKind.Conflict => contributing.Length >= 2,
            _ => false
        };
    }

    private static Result None(string code, string fingerprint) =>
        new(ProposalKind.None, ProposalTypeNone, null, fingerprint, null, code);

    private static string CanonicalNoneScopeFingerprint(ClassifyFeedbackDecision decision) =>
        CanonicalClassificationHasher.HashParts("proposal_none", decision.ToString());
}
