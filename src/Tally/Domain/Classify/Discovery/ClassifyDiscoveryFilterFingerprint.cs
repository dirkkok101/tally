using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;

namespace Tally.Domain.Classify.Discovery;

/// <summary>
/// Closed, ordinal, length-framed filter fingerprints for discovery pages
/// (DD-CLASSIFY-PAGINATED-DISCOVERY / FR-CLASSIFY-OUTCOME-DISCOVERY /
/// FR-CLASSIFY-RULEBOOK-DISCOVERY / bd-29ch).
/// Explicit null framing; no reflection JSON; no description/amount/path content.
/// </summary>
public static class ClassifyDiscoveryFilterFingerprint
{
    /// <summary>
    /// AND-filter fingerprint for classify.outcome.list. Field order is fixed and
    /// culture-invariant. Null optional filters are framed distinctly from empty strings.
    /// </summary>
    public static string ForOutcomeList(
        string evaluationId,
        ClassifyOutcomeKind? outcomeKind = null,
        string? suggestedCategoryId = null,
        string? contributingRuleVersionId = null,
        ClassifyOutcomeStaleFilter? staleState = null,
        string? transactionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        return CanonicalClassificationHasher.HashParts(
            "outcome.list",
            evaluationId,
            outcomeKind is null ? null : OutcomeKindWire(outcomeKind.Value),
            suggestedCategoryId,
            contributingRuleVersionId,
            staleState is null ? null : StaleFilterWire(staleState.Value),
            transactionId);
    }

    /// <summary>
    /// AND-filter fingerprint for classify.rule.list. Field order is fixed.
    /// </summary>
    public static string ForRuleList(
        string? logicalRuleId = null,
        ClassifyRuleLifecycleFilter? lifecycle = null,
        string? categoryId = null,
        bool? activeMembership = null)
    {
        return CanonicalClassificationHasher.HashParts(
            "rule.list",
            logicalRuleId,
            lifecycle is null ? null : LifecycleWire(lifecycle.Value),
            categoryId,
            activeMembership is null
                ? null
                : activeMembership.Value
                    ? "true"
                    : "false");
    }

    public static string OutcomeKindWire(ClassifyOutcomeKind kind) => kind switch
    {
        ClassifyOutcomeKind.Suggestion => "suggestion",
        ClassifyOutcomeKind.NoSuggestion => "no_suggestion",
        ClassifyOutcomeKind.Conflict => "conflict",
        ClassifyOutcomeKind.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown outcome kind.")
    };

    public static string StaleFilterWire(ClassifyOutcomeStaleFilter filter) => filter switch
    {
        ClassifyOutcomeStaleFilter.Any => "any",
        ClassifyOutcomeStaleFilter.Fresh => "fresh",
        ClassifyOutcomeStaleFilter.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown stale filter.")
    };

    public static string LifecycleWire(ClassifyRuleLifecycleFilter lifecycle) => lifecycle switch
    {
        ClassifyRuleLifecycleFilter.Draft => "draft",
        ClassifyRuleLifecycleFilter.Active => "active",
        ClassifyRuleLifecycleFilter.Retired => "retired",
        ClassifyRuleLifecycleFilter.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown lifecycle.")
    };
}
