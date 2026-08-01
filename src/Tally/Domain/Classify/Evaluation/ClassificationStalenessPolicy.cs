namespace Tally.Domain.Classify.Evaluation;

/// <summary>
/// Pure fingerprint and lifecycle comparison for retained classification outcomes
/// (FR-CLASSIFY-OUTCOME-INVALIDATION / DM-CLASSIFY-EVALUATION-OUTCOME).
/// Never reconstructs MatchEvidence; never treats same-ID category rename as identity drift.
/// </summary>
public static class ClassificationStalenessPolicy
{
    /// <summary>Only permitted next public operation when evidence is stale or unappliable.</summary>
    public const string NextOperationReEvaluate = "classify.evaluate";

    /// <summary>Item-level revision tuple drift (void, supersede, allocation, transfer, refund).</summary>
    public const string DimensionItemLifecycle = "item_lifecycle_fingerprint";

    /// <summary>Suggested category is missing, archived, or reactivated after the retained evaluation.</summary>
    public const string DimensionSuggestedCategoryLifecycle = "suggested_category_lifecycle";

    /// <summary>
    /// Inputs required to compare a retained evaluation fingerprint and outcome against current public state.
    /// All values are metadata / fingerprints — no raw descriptions or payloads.
    /// </summary>
    public sealed record Input(
        EvaluationFingerprint RetainedEvaluation,
        string RetainedItemLifecycleFingerprint,
        string? SuggestedCategoryId,
        /// <summary>Current store-generation fingerprint from public Ledger projection, if available.</summary>
        string? CurrentStoreGenerationFingerprint,
        string? CurrentLedgerContractVersion,
        string? CurrentProjectionVersion,
        string? CurrentCategoryLifecycleFingerprint,
        string? CurrentNormalizationVersion,
        string? CurrentRuleSetVersionId,
        string? CurrentOrderedItemsFingerprint,
        /// <summary>Current item lifecycle fingerprint for the retained transaction, if the public projection still exposes it.</summary>
        string? CurrentItemLifecycleFingerprint,
        bool TransactionFoundInLedger,
        /// <summary>Lifecycle of the suggested category when known: "active", "archived", or null when absent.</summary>
        string? SuggestedCategoryLifecycleState,
        /// <summary>
        /// True when public category history shows a Reactivate event after the retained evaluation time.
        /// Display-name rename alone must leave this false.
        /// </summary>
        bool SuggestedCategoryReactivatedAfterEvaluation,
        DateTimeOffset NowUtc,
        DateTimeOffset RetainedSnapshotExpiresAt);

    public sealed record Result(
        bool IsStale,
        IReadOnlyList<string> ChangedDimensions,
        /// <summary><see cref="NextOperationReEvaluate"/> when stale or unappliable; callers map fresh suggestions to null.</summary>
        string PermittedNextOperationId);

    /// <summary>
    /// Compare retained evaluation fingerprints and item/category lifecycle to current public state.
    /// Snapshot identity is not compared directly: a live store re-query always mints a new snapshot id.
    /// Expiry and store-generation capture invalid/expired snapshot drift instead.
    /// Display-name-only rename of the same active category id stays fresh when every semantic dimension
    /// below is unchanged (store-generation-only churn is then suppressed).
    /// </summary>
    public static Result Evaluate(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.RetainedEvaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.RetainedItemLifecycleFingerprint);

        var changed = new List<string>(EvaluationFingerprint.AllDimensions.Count + 2);

        // Snapshot expiry — retained evaluation bound is no longer valid.
        if (input.NowUtc >= input.RetainedSnapshotExpiresAt)
        {
            Add(changed, EvaluationFingerprint.DimensionSnapshotExpiresAt);
        }

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionLedgerContractVersion,
            input.RetainedEvaluation.LedgerContractVersion,
            input.CurrentLedgerContractVersion);

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionProjectionVersion,
            input.RetainedEvaluation.ProjectionVersion,
            input.CurrentProjectionVersion);

        var storeGenerationDrifted = !string.Equals(
            input.RetainedEvaluation.StoreGenerationFingerprint,
            input.CurrentStoreGenerationFingerprint,
            StringComparison.Ordinal)
            || input.CurrentStoreGenerationFingerprint is null;

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionCategoryLifecycle,
            input.RetainedEvaluation.CategoryLifecycleFingerprint,
            input.CurrentCategoryLifecycleFingerprint);

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionNormalizationVersion,
            input.RetainedEvaluation.NormalizationVersion,
            input.CurrentNormalizationVersion);

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionRuleSetVersion,
            input.RetainedEvaluation.RuleSetVersionId,
            input.CurrentRuleSetVersionId);

        CompareOptional(
            changed,
            EvaluationFingerprint.DimensionOrderedItems,
            input.RetainedEvaluation.OrderedItemsFingerprint,
            input.CurrentOrderedItemsFingerprint);

        // Item-level: void, supersede, allocation, transfer, refund relationship drift.
        var itemLifecycleMatches = input.TransactionFoundInLedger
            && !string.IsNullOrWhiteSpace(input.CurrentItemLifecycleFingerprint)
            && string.Equals(
                input.RetainedItemLifecycleFingerprint,
                input.CurrentItemLifecycleFingerprint,
                StringComparison.Ordinal);
        if (!itemLifecycleMatches)
        {
            Add(changed, DimensionItemLifecycle);
        }

        // Suggested category archive / missing identity / reactivation — not display rename of same active id.
        var suggestedCategoryActive = true;
        if (!string.IsNullOrWhiteSpace(input.SuggestedCategoryId))
        {
            var lifecycle = input.SuggestedCategoryLifecycleState?.Trim();
            suggestedCategoryActive = string.Equals(lifecycle, "active", StringComparison.Ordinal);
            if (!suggestedCategoryActive || input.SuggestedCategoryReactivatedAfterEvaluation)
            {
                Add(changed, DimensionSuggestedCategoryLifecycle);
            }
        }

        // Store generation: real drift is stale. Display-name-only rename of the same active
        // suggested category may advance store generation without semantic identity drift —
        // suppress that sole churn only for that rename case.
        if (storeGenerationDrifted)
        {
            var renameOnlyStoreGenerationChurn =
                !string.IsNullOrWhiteSpace(input.SuggestedCategoryId)
                && suggestedCategoryActive
                && !input.SuggestedCategoryReactivatedAfterEvaluation
                && itemLifecycleMatches
                && !changed.Contains(EvaluationFingerprint.DimensionSnapshotExpiresAt, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionLedgerContractVersion, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionProjectionVersion, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionCategoryLifecycle, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionNormalizationVersion, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionRuleSetVersion, StringComparer.Ordinal)
                && !changed.Contains(EvaluationFingerprint.DimensionOrderedItems, StringComparer.Ordinal)
                && !changed.Contains(DimensionSuggestedCategoryLifecycle, StringComparer.Ordinal);

            if (!renameOnlyStoreGenerationChurn)
            {
                Add(changed, EvaluationFingerprint.DimensionStoreGeneration);
            }
        }

        var ordered = changed
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        var isStale = ordered.Length > 0;
        return new Result(
            isStale,
            ordered,
            NextOperationReEvaluate);
    }

    /// <summary>
    /// Whether an outcome kind may ever authorize apply/preview without re-evaluation.
    /// Suggestions may be considered when not stale; all other partitions require re-evaluate.
    /// </summary>
    public static bool IsUnappliableOutcomeKind(ClassificationOutcomeKind kind) =>
        kind is ClassificationOutcomeKind.NoSuggestion
            or ClassificationOutcomeKind.Conflict
            or ClassificationOutcomeKind.Stale;

    private static void CompareOptional(
        List<string> changed,
        string dimension,
        string retained,
        string? current)
    {
        if (current is null)
        {
            // Unavailable current public state fails closed for that dimension.
            Add(changed, dimension);
            return;
        }

        if (!string.Equals(retained, current, StringComparison.Ordinal))
        {
            Add(changed, dimension);
        }
    }

    private static void Add(List<string> changed, string dimension)
    {
        if (!changed.Contains(dimension, StringComparer.Ordinal))
        {
            changed.Add(dimension);
        }
    }
}
