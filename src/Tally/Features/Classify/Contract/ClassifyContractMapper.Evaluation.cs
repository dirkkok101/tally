using System.Globalization;
using System.Text.Json;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure evaluation mapping helpers for classify.evaluate
/// (DM-CLASSIFY-EVALUATION-OUTCOME / TASK-CLASSIFY-RULEBOOK-EVALUATION-WORKFLOW).
/// No I/O, no Ledger access, no TimeProvider.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string OutcomeTypeSuggestion = "suggestion";
    public const string OutcomeTypeNoSuggestion = "no_suggestion";
    public const string OutcomeTypeConflict = "conflict";
    public const string OutcomeTypeStale = "stale";

    public const string EvaluationLifecycleCompleted = "completed";
    public const string EvaluationLifecycleRunning = "running";
    public const string EvaluationLifecycleFailed = "failed";

    /// <summary>Canonical request fingerprint element for classify.evaluate (contract version only).</summary>
    public static JsonElement ToEvaluateFingerprintElement(string contractVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static string FormatOutcomeType(ClassificationOutcomeKind kind) => kind switch
    {
        ClassificationOutcomeKind.Suggestion => OutcomeTypeSuggestion,
        ClassificationOutcomeKind.NoSuggestion => OutcomeTypeNoSuggestion,
        ClassificationOutcomeKind.Conflict => OutcomeTypeConflict,
        ClassificationOutcomeKind.Stale => OutcomeTypeStale,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown classification outcome kind.")
    };

    public static ClassifyOutcomeKind ToPublicOutcomeKind(ClassificationOutcomeKind kind) => kind switch
    {
        ClassificationOutcomeKind.Suggestion => ClassifyOutcomeKind.Suggestion,
        ClassificationOutcomeKind.NoSuggestion => ClassifyOutcomeKind.NoSuggestion,
        ClassificationOutcomeKind.Conflict => ClassifyOutcomeKind.Conflict,
        ClassificationOutcomeKind.Stale => ClassifyOutcomeKind.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown classification outcome kind.")
    };

    /// <summary>
    /// Map a complete public projection input into pure engine evaluation items.
    /// Does not re-evaluate Ledger eligibility — membership is already fixed by the loader.
    /// Fails closed when a public amount cannot be mapped (no partial item list).
    /// </summary>
    public static bool TryMapEvaluationItems(
        ClassificationEvaluationInput input,
        out IReadOnlyList<ClassificationEvaluationItem> items,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(input);
        items = Array.Empty<ClassificationEvaluationItem>();
        errorCode = null;

        var mapped = new List<ClassificationEvaluationItem>(input.Items.Count);
        foreach (var projectionItem in input.Items.OrderBy(i => i.Ordinal).ThenBy(i => i.TransactionId, StringComparer.Ordinal))
        {
            if (!TryMapProjectionItem(projectionItem, out var evaluationItem))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }

            mapped.Add(evaluationItem);
        }

        if (mapped.Count != input.TotalCount)
        {
            errorCode = ClassifyErrors.Integrity;
            return false;
        }

        items = mapped;
        return true;
    }

    public static bool TryMapProjectionItem(
        ClassificationProjectionItem item,
        out ClassificationEvaluationItem evaluationItem)
    {
        evaluationItem = null!;
        if (!TryMapPublicAmount(item, out var direction, out var absoluteMinor))
        {
            return false;
        }

        var lifecycle = ComputeItemLifecycleFingerprint(item);
        evaluationItem = new ClassificationEvaluationItem(
            item.Ordinal,
            item.TransactionId,
            item.AccountId,
            item.SourceDescription,
            direction,
            absoluteMinor,
            lifecycle);
        return true;
    }

    /// <summary>Public revision-tuple fingerprint — no raw description retention in storage rows.</summary>
    public static string ComputeItemLifecycleFingerprint(ClassificationProjectionItem item) =>
        CanonicalClassificationHasher.HashParts(
            item.TransactionRevision,
            item.RelationshipRevision,
            item.AllocationRevision);

    public static bool TryMapPublicAmount(
        ClassificationProjectionItem item,
        out string? direction,
        out long absoluteMinor)
    {
        direction = null;
        absoluteMinor = 0;
        if (!Money.TryParse(item.SignedAmount, out var money, out _))
        {
            return false;
        }

        absoluteMinor = money.MinorUnits == long.MinValue
            ? long.MaxValue
            : Math.Abs(money.MinorUnits);
        direction = item.AmountDirection switch
        {
            ClassificationAmountDirection.Expense => ClassificationRuleVocabulary.DirectionOutflow,
            ClassificationAmountDirection.Income => ClassificationRuleVocabulary.DirectionInflow,
            ClassificationAmountDirection.Zero => null,
            _ => null
        };
        return true;
    }

    public static ActiveRuleVersion ToActiveRuleVersion(
        string ruleVersionId,
        string categoryId,
        IReadOnlyList<RuleCondition> conditions) =>
        new(ruleVersionId, categoryId, conditions);

    public static EvaluationFingerprint CreateEvaluationFingerprint(
        ClassificationEvaluationInput input,
        string ruleSetVersionId,
        string? normalizationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        var norm = string.IsNullOrWhiteSpace(normalizationVersion)
            ? NormalizationDescriptor.V1.Version
            : normalizationVersion.Trim();
        return EvaluationFingerprint.Create(
            input.LedgerContractVersion,
            input.ProjectionVersion,
            input.StoreGenerationFingerprint,
            input.SnapshotId,
            input.SnapshotExpiresAt,
            input.CategoryLifecycleFingerprint,
            norm,
            ruleSetVersionId,
            input.OrderedItemsFingerprint);
    }

    public static ClassifyEvaluateResult ToEvaluateResult(
        string evaluationId,
        string ruleSetVersionId,
        string normalizationVersion,
        string projectionFingerprint,
        ClassificationEvaluationResult evaluation) =>
        new(
            ClassifyOperationIds.ContractVersion,
            evaluationId,
            ruleSetVersionId,
            normalizationVersion,
            projectionFingerprint,
            evaluation.InputCount,
            evaluation.SuggestionCount,
            evaluation.NoSuggestionCount,
            evaluation.ConflictCount,
            evaluation.StaleCount);

    public static ClassifyEvaluationRunRow ToEvaluationRunRow(
        string evaluationId,
        string? operationIdempotencyKey,
        string ruleSetVersionId,
        string normalizationVersion,
        ClassificationEvaluationInput input,
        ClassificationEvaluationResult evaluation,
        string actor,
        string createdAtUtc) =>
        new(
            evaluationId,
            operationIdempotencyKey,
            ruleSetVersionId,
            normalizationVersion,
            input.LedgerContractVersion,
            input.ProjectionVersion,
            input.StoreGenerationFingerprint,
            input.SnapshotId,
            input.SnapshotExpiresAt,
            input.CategoryLifecycleFingerprint,
            input.OrderedItemsFingerprint,
            evaluation.InputCount,
            evaluation.SuggestionCount,
            evaluation.NoSuggestionCount,
            evaluation.ConflictCount,
            evaluation.StaleCount,
            EvaluationLifecycleCompleted,
            actor,
            createdAtUtc);

    public static ClassifyOutcomeRow ToOutcomeRow(
        string outcomeId,
        string evaluationId,
        ClassificationOutcome outcome) =>
        new(
            outcomeId,
            evaluationId,
            outcome.Ordinal,
            outcome.TransactionId,
            FormatOutcomeType(outcome.Kind),
            outcome.CategoryId,
            outcome.ItemLifecycleFingerprint,
            outcome.SafeReason);

    /// <summary>
    /// Bound check: total match-evidence rows across all outcomes must stay within published limit.
    /// </summary>
    public static bool IsEvidenceWithinBound(
        ClassificationEvaluationResult evaluation,
        long maxEvidenceRows) =>
        evaluation.Outcomes.Sum(o => o.Evidence.Count) <= maxEvidenceRows;

    public static bool IsRuleCountWithinBound(int ruleCount, long maxRuleCount) =>
        ruleCount >= 0 && ruleCount <= maxRuleCount;

    public static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);
}
