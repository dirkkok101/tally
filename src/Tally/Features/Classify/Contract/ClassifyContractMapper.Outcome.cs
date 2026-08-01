using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;

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
                // Historical no-match / stale partitions do not require match evidence rows.
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

    public static ClassifyOutcomeGetResult ToOutcomeGetResult(
        ClassifyEvaluationRunRow run,
        ClassifyOutcomeRow outcome,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        bool isStale,
        IReadOnlyList<string>? staleDimensions,
        string? suggestedCategoryDisplayName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(evidence);

        var kind = ParseStoredOutcomeType(outcome.OutcomeType);
        var contributing = kind is ClassificationOutcomeKind.Suggestion or ClassificationOutcomeKind.Conflict
            ? ToContributingRuleVersionIds(evidence)
            : null;

        // Public wire contract: bounded fields only. Matched field keys are derivable from retained
        // evidence for tests via ToMatchedFieldKeys; the published result omits hashes/values.
        return new ClassifyOutcomeGetResult(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            EvaluationId: run.EvaluationId,
            OutcomeId: outcome.OutcomeId,
            TransactionId: outcome.TransactionId,
            Ordinal: outcome.Ordinal,
            Kind: ToPublicOutcomeKind(kind),
            SuggestedCategoryId: kind == ClassificationOutcomeKind.Suggestion ? outcome.CategoryId : null,
            SuggestedCategoryDisplayName: kind == ClassificationOutcomeKind.Suggestion
                ? suggestedCategoryDisplayName
                : null,
            ContributingRuleVersionIds: contributing is { Count: > 0 } ? contributing : null,
            IsStale: isStale || kind == ClassificationOutcomeKind.Stale,
            StaleDimensions: isStale || kind == ClassificationOutcomeKind.Stale
                ? (staleDimensions is { Count: > 0 }
                    ? staleDimensions.OrderBy(d => d, StringComparer.Ordinal).ToArray()
                    : kind == ClassificationOutcomeKind.Stale
                        ? new[] { outcome.SafeReason }
                        : Array.Empty<string>())
                : null);
    }

    /// <summary>
    /// When the outcome is not stale but is still unappliable (conflict / no-suggestion),
    /// permitted next operation remains re-evaluate only.
    /// </summary>
    public static string PermittedNextOperationId(
        ClassificationOutcomeKind kind,
        ClassificationStalenessPolicy.Result staleness) =>
        staleness.IsStale || ClassificationStalenessPolicy.IsUnappliableOutcomeKind(kind)
            ? ClassificationStalenessPolicy.NextOperationReEvaluate
            : ClassificationStalenessPolicy.NextOperationReEvaluate;
}
