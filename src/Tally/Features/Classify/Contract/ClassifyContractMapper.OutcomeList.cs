using System.Globalization;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Discovery;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure mapping for classify.outcome.list (DM-CLASSIFY-OUTCOME-PAGE / FR-CLASSIFY-OUTCOME-DISCOVERY / bd-vg33).
/// Deterministic ordering; never maps descriptions, amounts, normalized hashes, paths, or authority claims.
/// </summary>
public static partial class ClassifyContractMapper
{
    public static string FormatStoredOutcomeType(ClassifyOutcomeKind kind) => kind switch
    {
        ClassifyOutcomeKind.Suggestion => OutcomeTypeSuggestion,
        ClassifyOutcomeKind.NoSuggestion => OutcomeTypeNoSuggestion,
        ClassifyOutcomeKind.Conflict => OutcomeTypeConflict,
        ClassifyOutcomeKind.Stale => OutcomeTypeStale,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown public outcome kind.")
    };

    /// <summary>
    /// Map one retained outcome + retained evidence + staleness + optional display name into a list item.
    /// </summary>
    public static bool TryMapOutcomeListItem(
        ClassifyOutcomeRow outcome,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        bool isStale,
        IReadOnlyList<string> staleDimensions,
        string? suggestedCategoryDisplayName,
        IReadOnlyDictionary<string, ClassifyRuleVersionRow>? immutableRulesByVersionId,
        out ClassifyOutcomeListItem item,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(staleDimensions);
        item = null!;
        errorCode = null;

        ClassificationOutcomeKind kind;
        try
        {
            kind = ParseStoredOutcomeType(outcome.OutcomeType);
        }
        catch (ArgumentOutOfRangeException)
        {
            errorCode = EvidenceUnavailable;
            return false;
        }

        if (!TryValidateRetainedEvidence(kind, evidence, out errorCode))
        {
            return false;
        }

        IReadOnlyList<ClassifyConflictRuleProposal>? conflictSummary = null;
        if (kind == ClassificationOutcomeKind.Conflict)
        {
            if (immutableRulesByVersionId is null
                || !TryMapConflictProposals(evidence, immutableRulesByVersionId, out var proposals, out errorCode))
            {
                errorCode ??= EvidenceUnavailable;
                return false;
            }

            conflictSummary = proposals;
        }

        var effectiveStale = isStale || kind == ClassificationOutcomeKind.Stale;
        var contributing = kind is ClassificationOutcomeKind.Suggestion or ClassificationOutcomeKind.Conflict
            ? ToContributingRuleVersionIds(evidence)
            : Array.Empty<string>();
        var matchedFields = kind is ClassificationOutcomeKind.Suggestion or ClassificationOutcomeKind.Conflict
            ? ToMatchedFieldKeys(evidence)
            : Array.Empty<string>();

        IReadOnlyList<string> dimensions;
        if (effectiveStale)
        {
            dimensions = staleDimensions.Count > 0
                ? staleDimensions.OrderBy(d => d, StringComparer.Ordinal).ToArray()
                : kind == ClassificationOutcomeKind.Stale
                    ? new[] { outcome.SafeReason }
                    : Array.Empty<string>();
        }
        else
        {
            dimensions = Array.Empty<string>();
        }

        item = new ClassifyOutcomeListItem(
            OutcomeId: outcome.OutcomeId,
            TransactionId: outcome.TransactionId,
            Ordinal: outcome.Ordinal,
            Kind: ToPublicOutcomeKind(kind),
            SafeReason: outcome.SafeReason,
            SuggestedCategoryId: kind == ClassificationOutcomeKind.Suggestion ? outcome.CategoryId : null,
            SuggestedCategoryDisplayName: kind == ClassificationOutcomeKind.Suggestion
                ? suggestedCategoryDisplayName
                : null,
            ContributingRuleVersionIds: contributing,
            MatchedFieldKeys: matchedFields,
            ConflictSummary: conflictSummary,
            StaleDimensions: dimensions,
            PermittedNextOperationId: ResolvePermittedNextOperationId(kind, effectiveStale));
        return true;
    }

    /// <summary>
    /// Build the public page result. Items must already be ordered and bounded.
    /// </summary>
    public static ClassifyOutcomeListResult ToOutcomeListResult(
        string evaluationId,
        string evaluationFingerprint,
        string resultFingerprint,
        string ruleSetFingerprint,
        string categoryLifecycleFingerprint,
        string ledgerGeneration,
        int overallCount,
        int filteredCount,
        IReadOnlyList<ClassifyOutcomeListItem> items,
        string? continuation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryLifecycleFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerGeneration);
        ArgumentNullException.ThrowIfNull(items);

        return new ClassifyOutcomeListResult(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            EvaluationId: evaluationId,
            EvaluationFingerprint: evaluationFingerprint,
            ResultFingerprint: resultFingerprint,
            RuleSetFingerprint: ruleSetFingerprint,
            CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
            LedgerGeneration: ledgerGeneration,
            OverallCount: overallCount,
            FilteredCount: filteredCount,
            ReturnedCount: items.Count,
            Items: items,
            Continuation: continuation);
    }

    /// <summary>Filter fingerprint for cursor binding (delegates to closed discovery policy).</summary>
    public static string OutcomeListFilterFingerprint(
        string evaluationId,
        ClassifyOutcomeKind? outcomeKind,
        string? suggestedCategoryId,
        string? contributingRuleVersionId,
        ClassifyOutcomeStaleFilter? staleState,
        string? transactionId) =>
        ClassifyDiscoveryFilterFingerprint.ForOutcomeList(
            evaluationId,
            outcomeKind,
            suggestedCategoryId,
            contributingRuleVersionId,
            staleState,
            transactionId);

    /// <summary>Rule-set fingerprint for discovery pages (version id, length-framed).</summary>
    public static string RuleSetFingerprint(string ruleSetVersionId) =>
        CanonicalClassificationHasher.HashParts("rule_set", ruleSetVersionId);
}
