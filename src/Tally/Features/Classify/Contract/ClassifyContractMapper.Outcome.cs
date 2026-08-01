using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure outcome explanation mapping for classify.outcome.get
/// (FR-CLASSIFY-OUTCOME-EXPLANATION / FR-CLASSIFY-OUTCOME-INVALIDATION).
/// No I/O; never reconstructs MatchEvidence; never exposes raw descriptions or normalized values.
/// </summary>
public static partial class ClassifyContractMapper
{
    /// <summary>
    /// Distinct stable status when a known outcome's retained match evidence is missing or incomplete.
    /// Never reconstruct from current Ledger/rule state.
    /// </summary>
    public const string EvidenceUnavailable = ClassifyErrors.Integrity;

    public static ClassificationOutcomeKind ParseStoredOutcomeType(string outcomeType) => outcomeType switch
    {
        OutcomeTypeSuggestion => ClassificationOutcomeKind.Suggestion,
        OutcomeTypeNoSuggestion => ClassificationOutcomeKind.NoSuggestion,
        OutcomeTypeConflict => ClassificationOutcomeKind.Conflict,
        OutcomeTypeStale => ClassificationOutcomeKind.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(outcomeType), outcomeType, "Unknown stored outcome type.")
    };

    /// <summary>
    /// Validate retained evidence completeness for explanation.
    /// Suggestion requires ≥1 evidence row; conflict requires ≥2 distinct contributing rule versions.
    /// No-suggestion and already-stale outcomes require zero reconstructed evidence.
    /// </summary>
    public static bool TryValidateRetainedEvidence(
        ClassificationOutcomeKind kind,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        errorCode = null;
        var contributing = evidence
            .Select(e => e.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        switch (kind)
        {
            case ClassificationOutcomeKind.Suggestion:
                if (evidence.Count == 0 || contributing.Length == 0)
                {
                    errorCode = EvidenceUnavailable;
                    return false;
                }

                return true;
            case ClassificationOutcomeKind.Conflict:
                if (contributing.Length < 2)
                {
                    errorCode = EvidenceUnavailable;
                    return false;
                }

                return true;
            case ClassificationOutcomeKind.NoSuggestion:
            case ClassificationOutcomeKind.Stale:
                return true;
            default:
                errorCode = EvidenceUnavailable;
                return false;
        }
    }

    /// <summary>Ordered contributing rule version ids from retained match evidence only.</summary>
    public static IReadOnlyList<string> ToContributingRuleVersionIds(
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence) =>
        evidence
            .Select(e => e.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Allowed matched field keys from retained evidence (bounded, ordered).
    /// Never returns predicate values or normalized hashes in the public explanation payload.
    /// </summary>
    public static IReadOnlyList<string> ToMatchedFieldKeys(
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence) =>
        evidence
            .Select(e => e.FieldKey)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Build ordered conflict proposals from retained evidence rule ids and immutable rule_version rows.
    /// Fails when any evidence-named rule is missing or category mapping is blank — never invents categories.
    /// </summary>
    public static bool TryMapConflictProposals(
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        IReadOnlyDictionary<string, ClassifyRuleVersionRow> immutableRulesByVersionId,
        out IReadOnlyList<ClassifyConflictRuleProposal> proposals,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(immutableRulesByVersionId);
        proposals = Array.Empty<ClassifyConflictRuleProposal>();
        errorCode = null;

        var ruleIds = ToContributingRuleVersionIds(evidence);
        if (ruleIds.Count < 2)
        {
            errorCode = EvidenceUnavailable;
            return false;
        }

        var list = new List<ClassifyConflictRuleProposal>(ruleIds.Count);
        foreach (var ruleId in ruleIds)
        {
            if (!immutableRulesByVersionId.TryGetValue(ruleId, out var version)
                || string.IsNullOrWhiteSpace(version.CategoryId))
            {
                errorCode = EvidenceUnavailable;
                return false;
            }

            list.Add(new ClassifyConflictRuleProposal(ruleId, version.CategoryId));
        }

        // Deterministic order by ruleVersionId (already ordered), then proposed category id as tie-break.
        proposals = list
            .OrderBy(p => p.RuleVersionId, StringComparer.Ordinal)
            .ThenBy(p => p.ProposedCategoryId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    public static EvaluationFingerprint ToRetainedEvaluationFingerprint(ClassifyEvaluationRunRow run) =>
        EvaluationFingerprint.Create(
            run.LedgerContractVersion,
            run.ProjectionVersion,
            run.StoreGenerationFingerprint,
            run.SnapshotId,
            run.SnapshotExpiresAt,
            run.CategoryLifecycleFingerprint,
            run.NormalizationVersion,
            run.RuleSetVersionId,
            run.OrderedItemsFingerprint);

    /// <summary>
    /// Fresh non-stale suggestion → null next operation.
    /// Stale, conflict, no-suggestion, or stored-stale → classify.evaluate only.
    /// </summary>
    public static string? ResolvePermittedNextOperationId(
        ClassificationOutcomeKind kind,
        bool isStale)
    {
        if (isStale || IsUnappliablePublicKind(kind))
        {
            return ClassificationStalenessPolicy.NextOperationReEvaluate;
        }

        // Fresh suggestion — no forced next operation on this result.
        return null;
    }

    public static bool IsUnappliablePublicKind(ClassificationOutcomeKind kind) =>
        ClassificationStalenessPolicy.IsUnappliableOutcomeKind(kind);

    public static ClassifyOutcomeGetResult ToOutcomeGetResult(
        ClassifyEvaluationRunRow run,
        ClassifyOutcomeRow outcome,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        bool isStale,
        IReadOnlyList<string>? staleDimensions,
        string? suggestedCategoryDisplayName,
        IReadOnlyList<ClassifyConflictRuleProposal>? conflictProposals = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(evidence);

        var kind = ParseStoredOutcomeType(outcome.OutcomeType);
        var effectiveStale = isStale || kind == ClassificationOutcomeKind.Stale;
        var contributing = kind is ClassificationOutcomeKind.Suggestion or ClassificationOutcomeKind.Conflict
            ? ToContributingRuleVersionIds(evidence)
            : null;
        var matchedFields = kind is ClassificationOutcomeKind.Suggestion or ClassificationOutcomeKind.Conflict
            ? ToMatchedFieldKeys(evidence)
            : null;

        IReadOnlyList<string>? dimensions = null;
        if (effectiveStale)
        {
            dimensions = staleDimensions is { Count: > 0 }
                ? staleDimensions.OrderBy(d => d, StringComparer.Ordinal).ToArray()
                : kind == ClassificationOutcomeKind.Stale
                    ? new[] { outcome.SafeReason }
                    : Array.Empty<string>();
        }

        return new ClassifyOutcomeGetResult(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            EvaluationId: run.EvaluationId,
            OutcomeId: outcome.OutcomeId,
            TransactionId: outcome.TransactionId,
            Ordinal: outcome.Ordinal,
            Kind: ToPublicOutcomeKind(kind),
            NormalizationVersion: run.NormalizationVersion,
            RuleSetVersionId: run.RuleSetVersionId,
            SafeReason: outcome.SafeReason,
            SuggestedCategoryId: kind == ClassificationOutcomeKind.Suggestion ? outcome.CategoryId : null,
            SuggestedCategoryDisplayName: kind == ClassificationOutcomeKind.Suggestion
                ? suggestedCategoryDisplayName
                : null,
            ContributingRuleVersionIds: contributing is { Count: > 0 } ? contributing : null,
            MatchedFieldKeys: matchedFields is { Count: > 0 } ? matchedFields : null,
            ConflictProposals: kind == ClassificationOutcomeKind.Conflict
                ? conflictProposals
                : null,
            IsStale: effectiveStale,
            StaleDimensions: dimensions,
            PermittedNextOperationId: ResolvePermittedNextOperationId(kind, effectiveStale));
    }

    /// <summary>Backward-compatible helper used by tests that only need the next-op mapping.</summary>
    public static string PermittedNextOperationId(
        ClassificationOutcomeKind kind,
        ClassificationStalenessPolicy.Result staleness) =>
        ResolvePermittedNextOperationId(kind, staleness.IsStale)
        ?? ClassificationStalenessPolicy.NextOperationReEvaluate;
}
