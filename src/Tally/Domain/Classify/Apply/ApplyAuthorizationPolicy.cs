using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Rules;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Domain.Classify.Apply;

/// <summary>
/// Pure selection and authority policy for classify.apply.preview
/// (FR-CLASSIFY-APPLY-AUTHORIZATION / TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW).
/// Never infers owner authority from evaluation alone; never mutates Ledger.
/// </summary>
public static class ApplyAuthorizationPolicy
{
    public const string ModeAssign = "assign";
    public const string ModeCorrect = "correct";

    /// <summary>One authorized preview candidate before Ledger preflight (no revisions yet).</summary>
    public sealed record AuthorizedCandidate(
        string OutcomeId,
        string TransactionId,
        string Mode,
        string TargetCategoryId,
        string? RuleVersionId,
        string? ExpectedCurrentCategoryId,
        string? CorrectionReason,
        string RetainedItemLifecycleFingerprint,
        int SourceOrdinal);

    public sealed record AuthorizationResult(
        bool IsAuthorized,
        string? ErrorCode,
        ClassifyApplySelectionMode Mode,
        IReadOnlyList<AuthorizedCandidate> Candidates,
        int ExclusionCount,
        int ExcludedNoSuggestionCount,
        int ExcludedConflictCount,
        int ExcludedStaleKindCount,
        int ExcludedUnauthorizedCount,
        string? ExactRuleVersionId,
        bool BroadAuthorityGranted);

    /// <summary>
    /// Authorize a selection against retained outcomes for one evaluation.
    /// Broad exact-rule mode requires immutable owner-approved broad-apply evidence
    /// (<see cref="RuleLifecyclePolicy.StateActiveBroadApply"/>) on that rule version.
    /// </summary>
    public static AuthorizationResult Authorize(
        ClassifyApplySelection selection,
        ClassifyEvaluationRunRow run,
        IReadOnlyList<ClassifyOutcomeRow> outcomes,
        IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> evidenceByOutcomeId,
        IReadOnlySet<string> broadApplyAuthorizedRuleVersionIds,
        IReadOnlySet<string> activeRuleVersionIds)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(evidenceByOutcomeId);
        ArgumentNullException.ThrowIfNull(broadApplyAuthorizedRuleVersionIds);
        ArgumentNullException.ThrowIfNull(activeRuleVersionIds);

        if (!TryValidateModeShape(selection, out var shapeError))
        {
            return Fail(selection.Mode, shapeError ?? ClassifyErrors.SelectionInvalid);
        }

        return selection.Mode switch
        {
            ClassifyApplySelectionMode.SelectedOutcomes => AuthorizeSelectedOutcomes(
                selection, run, outcomes, evidenceByOutcomeId),
            ClassifyApplySelectionMode.ExactRule => AuthorizeExactRule(
                selection, run, outcomes, evidenceByOutcomeId,
                broadApplyAuthorizedRuleVersionIds, activeRuleVersionIds),
            ClassifyApplySelectionMode.ExplicitCorrections => AuthorizeExplicitCorrections(
                selection, run, outcomes),
            _ => Fail(selection.Mode, ClassifyErrors.SelectionInvalid)
        };
    }

    /// <summary>
    /// True when lifecycle events for the subject include an activation that granted broad apply
    /// and the subject has not been retired/superseded after that grant.
    /// </summary>
    public static bool HasImmutableBroadApplyAuthority(
        IReadOnlyList<ClassifyRuleLifecycleEventRow> lifecycleEvents)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvents);
        if (lifecycleEvents.Count == 0)
        {
            return false;
        }

        // Chronological: last terminal state wins. Broad apply is only true when the latest
        // activation-like resulting state is active_with_broad_apply (not plain active/retired/superseded).
        var ordered = lifecycleEvents
            .OrderBy(e => e.OccurredAt, StringComparer.Ordinal)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToArray();

        string? latest = null;
        foreach (var evt in ordered)
        {
            if (string.IsNullOrWhiteSpace(evt.ResultingState))
            {
                continue;
            }

            latest = evt.ResultingState;
        }

        return string.Equals(latest, RuleLifecyclePolicy.StateActiveBroadApply, StringComparison.Ordinal);
    }

    public static bool TryValidateModeShape(ClassifyApplySelection selection, out string? errorCode)
    {
        errorCode = null;
        var hasOutcomes = selection.OutcomeIds is { Count: > 0 };
        var hasRule = !string.IsNullOrWhiteSpace(selection.RuleVersionId);
        var hasCorrections = selection.CorrectionItems is { Count: > 0 };
        var modeCount = (hasOutcomes ? 1 : 0) + (hasRule ? 1 : 0) + (hasCorrections ? 1 : 0);
        if (modeCount != 1)
        {
            errorCode = ClassifyErrors.SelectionInvalid;
            return false;
        }

        return selection.Mode switch
        {
            ClassifyApplySelectionMode.SelectedOutcomes when hasOutcomes && !hasRule && !hasCorrections => true,
            ClassifyApplySelectionMode.ExactRule when hasRule && !hasOutcomes && !hasCorrections => true,
            ClassifyApplySelectionMode.ExplicitCorrections when hasCorrections && !hasOutcomes && !hasRule
                && selection.CorrectionItems!.All(IsCompleteCorrection) => true,
            _ => FailShape(out errorCode)
        };

        static bool FailShape(out string? code)
        {
            code = ClassifyErrors.SelectionInvalid;
            return false;
        }
    }

    public static bool IsCompleteCorrection(ClassifyExplicitCorrectionItem item) =>
        !string.IsNullOrWhiteSpace(item.TransactionId)
        && !string.IsNullOrWhiteSpace(item.OutcomeId)
        && !string.IsNullOrWhiteSpace(item.CurrentCategoryId)
        && !string.IsNullOrWhiteSpace(item.TargetCategoryId)
        && !string.IsNullOrWhiteSpace(item.Reason);

    /// <summary>
    /// Exact-rule broad mode may never authorize a correction item.
    /// </summary>
    public static bool IsBroadCorrectionAttempt(ClassifyApplySelection selection) =>
        selection.Mode == ClassifyApplySelectionMode.ExactRule
        && selection.CorrectionItems is { Count: > 0 };

    private static AuthorizationResult AuthorizeSelectedOutcomes(
        ClassifyApplySelection selection,
        ClassifyEvaluationRunRow run,
        IReadOnlyList<ClassifyOutcomeRow> outcomes,
        IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> evidenceByOutcomeId)
    {
        var requested = selection.OutcomeIds!
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0)
        {
            return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.SelectionInvalid);
        }

        var byId = outcomes.ToDictionary(o => o.OutcomeId, StringComparer.Ordinal);
        var candidates = new List<AuthorizedCandidate>();
        var exclusion = 0;
        var noSug = 0;
        var conflict = 0;
        var staleKind = 0;
        var unauthorized = 0;

        foreach (var outcomeId in requested)
        {
            if (!byId.TryGetValue(outcomeId, out var outcome)
                || !string.Equals(outcome.EvaluationId, run.EvaluationId, StringComparison.Ordinal))
            {
                // Missing / wrong evaluation — reject whole selection (mixed-fingerprint / unknown).
                return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.SelectionInvalid);
            }

            if (!TryParseKind(outcome.OutcomeType, out var kind))
            {
                return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.Integrity);
            }

            if (ClassificationStalenessPolicy.IsUnappliableOutcomeKind(kind))
            {
                exclusion++;
                switch (kind)
                {
                    case ClassificationOutcomeKind.NoSuggestion:
                        noSug++;
                        break;
                    case ClassificationOutcomeKind.Conflict:
                        conflict++;
                        break;
                    case ClassificationOutcomeKind.Stale:
                        staleKind++;
                        break;
                }

                continue;
            }

            if (kind != ClassificationOutcomeKind.Suggestion
                || string.IsNullOrWhiteSpace(outcome.CategoryId))
            {
                exclusion++;
                unauthorized++;
                continue;
            }

            if (!evidenceByOutcomeId.TryGetValue(outcome.OutcomeId, out var evidence)
                || evidence.Count == 0)
            {
                return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.Integrity);
            }

            var contributing = evidence
                .Select(e => e.RuleVersionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (contributing.Length == 0)
            {
                return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.Integrity);
            }

            candidates.Add(new AuthorizedCandidate(
                outcome.OutcomeId,
                outcome.TransactionId,
                ModeAssign,
                outcome.CategoryId!,
                contributing[0],
                ExpectedCurrentCategoryId: null,
                CorrectionReason: null,
                outcome.ItemLifecycleFingerprint,
                outcome.Ordinal));
        }

        if (candidates.Count == 0)
        {
            return Fail(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyErrors.SelectionInvalid);
        }

        // Deterministic transaction order.
        var ordered = candidates
            .OrderBy(c => c.TransactionId, StringComparer.Ordinal)
            .ThenBy(c => c.OutcomeId, StringComparer.Ordinal)
            .ToArray();

        return new AuthorizationResult(
            true,
            null,
            ClassifyApplySelectionMode.SelectedOutcomes,
            ordered,
            exclusion,
            noSug,
            conflict,
            staleKind,
            unauthorized,
            ExactRuleVersionId: null,
            BroadAuthorityGranted: false);
    }

    private static AuthorizationResult AuthorizeExactRule(
        ClassifyApplySelection selection,
        ClassifyEvaluationRunRow run,
        IReadOnlyList<ClassifyOutcomeRow> outcomes,
        IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> evidenceByOutcomeId,
        IReadOnlySet<string> broadApplyAuthorizedRuleVersionIds,
        IReadOnlySet<string> activeRuleVersionIds)
    {
        var ruleVersionId = selection.RuleVersionId!.Trim();
        if (string.IsNullOrWhiteSpace(ruleVersionId))
        {
            return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.SelectionInvalid);
        }

        // Broad correction is never authorized via exact-rule mode.
        if (selection.CorrectionItems is { Count: > 0 })
        {
            return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.SelectionInvalid);
        }

        if (!activeRuleVersionIds.Contains(ruleVersionId))
        {
            return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.Lifecycle);
        }

        if (!broadApplyAuthorizedRuleVersionIds.Contains(ruleVersionId))
        {
            return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.Lifecycle);
        }

        var candidates = new List<AuthorizedCandidate>();
        var exclusion = 0;
        var noSug = 0;
        var conflict = 0;
        var staleKind = 0;
        var unauthorized = 0;

        foreach (var outcome in outcomes
                     .Where(o => string.Equals(o.EvaluationId, run.EvaluationId, StringComparison.Ordinal))
                     .OrderBy(o => o.Ordinal)
                     .ThenBy(o => o.TransactionId, StringComparer.Ordinal))
        {
            if (!TryParseKind(outcome.OutcomeType, out var kind))
            {
                return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.Integrity);
            }

            if (kind == ClassificationOutcomeKind.NoSuggestion)
            {
                exclusion++;
                noSug++;
                continue;
            }

            if (kind == ClassificationOutcomeKind.Conflict)
            {
                exclusion++;
                conflict++;
                continue;
            }

            if (kind == ClassificationOutcomeKind.Stale)
            {
                exclusion++;
                staleKind++;
                continue;
            }

            if (kind != ClassificationOutcomeKind.Suggestion
                || string.IsNullOrWhiteSpace(outcome.CategoryId))
            {
                exclusion++;
                unauthorized++;
                continue;
            }

            if (!evidenceByOutcomeId.TryGetValue(outcome.OutcomeId, out var evidence)
                || evidence.Count == 0)
            {
                exclusion++;
                unauthorized++;
                continue;
            }

            var producedByRule = evidence.Any(e =>
                string.Equals(e.RuleVersionId, ruleVersionId, StringComparison.Ordinal));
            if (!producedByRule)
            {
                // Not produced by this exact rule — not an exclusion of a selected item; skip silently.
                continue;
            }

            // Exact-rule broad mode authorizes assignment only (never correction).
            candidates.Add(new AuthorizedCandidate(
                outcome.OutcomeId,
                outcome.TransactionId,
                ModeAssign,
                outcome.CategoryId!,
                ruleVersionId,
                ExpectedCurrentCategoryId: null,
                CorrectionReason: null,
                outcome.ItemLifecycleFingerprint,
                outcome.Ordinal));
        }

        if (candidates.Count == 0)
        {
            return Fail(ClassifyApplySelectionMode.ExactRule, ClassifyErrors.SelectionInvalid);
        }

        var ordered = candidates
            .OrderBy(c => c.TransactionId, StringComparer.Ordinal)
            .ThenBy(c => c.OutcomeId, StringComparer.Ordinal)
            .ToArray();

        return new AuthorizationResult(
            true,
            null,
            ClassifyApplySelectionMode.ExactRule,
            ordered,
            exclusion,
            noSug,
            conflict,
            staleKind,
            unauthorized,
            ExactRuleVersionId: ruleVersionId,
            BroadAuthorityGranted: true);
    }

    private static AuthorizationResult AuthorizeExplicitCorrections(
        ClassifyApplySelection selection,
        ClassifyEvaluationRunRow run,
        IReadOnlyList<ClassifyOutcomeRow> outcomes)
    {
        // Explicit corrections can never mix with broad exact-rule mode (already mode-exclusive).
        if (!string.IsNullOrWhiteSpace(selection.RuleVersionId))
        {
            return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
        }

        var items = selection.CorrectionItems!;
        if (items.Count == 0)
        {
            return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
        }

        var byOutcomeId = outcomes.ToDictionary(o => o.OutcomeId, StringComparer.Ordinal);
        var candidates = new List<AuthorizedCandidate>();
        var seenTx = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!IsCompleteCorrection(item))
            {
                return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
            }

            var outcomeId = item.OutcomeId.Trim();
            var transactionId = item.TransactionId.Trim();
            var currentCategoryId = item.CurrentCategoryId.Trim();
            var targetCategoryId = item.TargetCategoryId.Trim();
            var reason = item.Reason.Trim();

            if (string.Equals(currentCategoryId, targetCategoryId, StringComparison.Ordinal))
            {
                return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
            }

            if (!byOutcomeId.TryGetValue(outcomeId, out var outcome)
                || !string.Equals(outcome.EvaluationId, run.EvaluationId, StringComparison.Ordinal)
                || !string.Equals(outcome.TransactionId, transactionId, StringComparison.Ordinal))
            {
                return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
            }

            if (!seenTx.Add(transactionId))
            {
                return Fail(ClassifyApplySelectionMode.ExplicitCorrections, ClassifyErrors.SelectionInvalid);
            }

            candidates.Add(new AuthorizedCandidate(
                outcome.OutcomeId,
                transactionId,
                ModeCorrect,
                targetCategoryId,
                RuleVersionId: null,
                ExpectedCurrentCategoryId: currentCategoryId,
                CorrectionReason: reason,
                outcome.ItemLifecycleFingerprint,
                outcome.Ordinal));
        }

        var ordered = candidates
            .OrderBy(c => c.TransactionId, StringComparer.Ordinal)
            .ThenBy(c => c.OutcomeId, StringComparer.Ordinal)
            .ToArray();

        return new AuthorizationResult(
            true,
            null,
            ClassifyApplySelectionMode.ExplicitCorrections,
            ordered,
            ExclusionCount: 0,
            ExcludedNoSuggestionCount: 0,
            ExcludedConflictCount: 0,
            ExcludedStaleKindCount: 0,
            ExcludedUnauthorizedCount: 0,
            ExactRuleVersionId: null,
            BroadAuthorityGranted: false);
    }

    private static bool TryParseKind(string outcomeType, out ClassificationOutcomeKind kind)
    {
        kind = default;
        switch (outcomeType)
        {
            case "suggestion":
                kind = ClassificationOutcomeKind.Suggestion;
                return true;
            case "no_suggestion":
                kind = ClassificationOutcomeKind.NoSuggestion;
                return true;
            case "conflict":
                kind = ClassificationOutcomeKind.Conflict;
                return true;
            case "stale":
                kind = ClassificationOutcomeKind.Stale;
                return true;
            default:
                return false;
        }
    }

    private static AuthorizationResult Fail(ClassifyApplySelectionMode mode, string errorCode) =>
        new(
            false,
            errorCode,
            mode,
            Array.Empty<AuthorizedCandidate>(),
            0,
            0,
            0,
            0,
            0,
            null,
            false);
}
