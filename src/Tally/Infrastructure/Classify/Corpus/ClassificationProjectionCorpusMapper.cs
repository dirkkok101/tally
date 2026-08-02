using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Pure exact-label mapping from released classification_v1 projection rows to the existing
/// private-corpus JSONL dialect (TASK-CLASSIFY-ERGONOMICS-CORPUS-MAPPER / bd-3k1z).
/// No I/O, no label invention, no private Ledger access.
/// </summary>
public static class ClassificationProjectionCorpusMapper
{
    /// <summary>
    /// One explicit owner label. Expected category is required for suggestion and forbidden
    /// for no_suggestion / conflict / stale (FR-CLASSIFY-PRIVATE-CORPUS-BUILDER).
    /// </summary>
    public sealed record ExactLabel(
        string TransactionId,
        ClassifyOutcomeKind ExpectedOutcome,
        string? ExpectedCategoryId = null);

    /// <summary>
    /// Map unique exact labels onto fresh eligible classification_v1 projection rows.
    /// Produces ordered <see cref="PrivateCorpusRow"/> values using public projection fields
    /// and the revision-tuple lifecycle fingerprint. Never invents a label or category.
    /// </summary>
    public static bool TryMapLabelsToPrivateRows(
        IReadOnlyList<ExactLabel> labels,
        IReadOnlyList<ClassificationProjectionItem> projectionItems,
        IReadOnlyList<ClassificationCategoryIdentity>? activeCategories,
        out IReadOnlyList<PrivateCorpusRow> rows,
        out string? errorCode)
    {
        rows = Array.Empty<PrivateCorpusRow>();
        errorCode = null;

        if (labels is null || projectionItems is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (labels.Count is < ClassifyOperatorErgonomicsContracts.MinLabelCount
            or > PrivateCorpusLimits.MaxRowCount)
        {
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        var byTx = new Dictionary<string, ClassificationProjectionItem>(StringComparer.Ordinal);
        foreach (var item in projectionItems)
        {
            if (string.IsNullOrWhiteSpace(item.TransactionId))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }

            if (!byTx.TryAdd(item.TransactionId, item))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }
        }

        var active = BuildActiveCategorySet(activeCategories);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var built = new List<PrivateCorpusRow>(labels.Count);

        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label.TransactionId)
                || !seen.Add(label.TransactionId.Trim()))
            {
                errorCode = ClassifyErrors.LabelInvalid;
                return false;
            }

            var txId = label.TransactionId.Trim();
            if (!byTx.TryGetValue(txId, out var publicItem))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            if (!IsEligibleProjectionItem(publicItem))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            if (!TryValidateOutcomeCategoryTuple(label, active, out errorCode))
            {
                return false;
            }

            if (!TryMapPublicAmount(publicItem, out var direction, out var absoluteMinor))
            {
                errorCode = ClassifyErrors.LedgerIncompatible;
                return false;
            }

            var lifecycle = ComputeItemLifecycleFingerprint(publicItem);
            if (string.IsNullOrWhiteSpace(lifecycle))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }

            var expectedKind = ToExpectedOutcomeKind(label.ExpectedOutcome);
            var expectedCategory = string.IsNullOrWhiteSpace(label.ExpectedCategoryId)
                ? null
                : label.ExpectedCategoryId.Trim();

            built.Add(new PrivateCorpusRow(
                publicItem.Ordinal,
                txId,
                publicItem.AccountId,
                publicItem.SourceDescription,
                direction,
                absoluteMinor,
                lifecycle,
                expectedCategory,
                expectedKind));
        }

        rows = built
            .OrderBy(r => r.Ordinal)
            .ThenBy(r => r.TransactionId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    /// <summary>
    /// Bind each private corpus row exactly once to a frozen public classification_v1 projection member.
    /// Requires matching account, description, direction, absolute amount, and lifecycle fingerprint
    /// derived from the public revision tuple. Failures are metadata-only.
    /// </summary>
    public static bool TryBindPrivateRowsToProjection(
        IReadOnlyList<PrivateCorpusRow> privateRows,
        IReadOnlyList<ClassificationProjectionItem> projectionItems,
        out IReadOnlyList<ClassificationEvaluationItem> boundItems,
        out string? errorCode)
    {
        boundItems = Array.Empty<ClassificationEvaluationItem>();
        errorCode = null;

        if (privateRows.Count == 0)
        {
            boundItems = Array.Empty<ClassificationEvaluationItem>();
            return true;
        }

        var byTx = new Dictionary<string, ClassificationProjectionItem>(StringComparer.Ordinal);
        foreach (var item in projectionItems)
        {
            if (!byTx.TryAdd(item.TransactionId, item))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }
        }

        var seenPrivate = new HashSet<string>(StringComparer.Ordinal);
        var bound = new List<ClassificationEvaluationItem>(privateRows.Count);
        foreach (var row in privateRows.OrderBy(r => r.Ordinal).ThenBy(r => r.TransactionId, StringComparer.Ordinal))
        {
            if (!seenPrivate.Add(row.TransactionId))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }

            if (!byTx.TryGetValue(row.TransactionId, out var publicItem))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            if (!TryMatchPrivateToPublic(row, publicItem, out var matchedItem))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            bound.Add(matchedItem);
        }

        boundItems = bound;
        return true;
    }

    /// <summary>Public revision-tuple fingerprint used without retaining private payloads.</summary>
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

    private static bool TryMatchPrivateToPublic(
        PrivateCorpusRow row,
        ClassificationProjectionItem publicItem,
        out ClassificationEvaluationItem evaluationItem)
    {
        evaluationItem = null!;

        if (!string.Equals(row.AccountId, publicItem.AccountId, StringComparison.Ordinal)
            || !string.Equals(row.SourceDescription, publicItem.SourceDescription, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryMapPublicAmount(publicItem, out var direction, out var absoluteMinor))
        {
            return false;
        }

        if (!string.Equals(row.AmountDirection, direction, StringComparison.Ordinal)
            || row.AmountAbsoluteMinor != absoluteMinor)
        {
            return false;
        }

        var lifecycle = ComputeItemLifecycleFingerprint(publicItem);
        if (!string.Equals(row.ItemLifecycleFingerprint, lifecycle, StringComparison.Ordinal))
        {
            return false;
        }

        evaluationItem = new ClassificationEvaluationItem(
            row.Ordinal,
            row.TransactionId,
            row.AccountId,
            row.SourceDescription,
            row.AmountDirection,
            row.AmountAbsoluteMinor,
            row.ItemLifecycleFingerprint);
        return true;
    }

    private static bool IsEligibleProjectionItem(ClassificationProjectionItem item) =>
        item.CategoryMutationState is CategoryMutationState.Assignable
            or CategoryMutationState.Correctable;

    private static HashSet<string> BuildActiveCategorySet(
        IReadOnlyList<ClassificationCategoryIdentity>? activeCategories)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (activeCategories is null)
        {
            return set;
        }

        foreach (var category in activeCategories)
        {
            if (string.IsNullOrWhiteSpace(category.CategoryId))
            {
                continue;
            }

            if (string.Equals(category.LifecycleState, "active", StringComparison.Ordinal))
            {
                set.Add(category.CategoryId);
            }
        }

        return set;
    }

    private static bool TryValidateOutcomeCategoryTuple(
        ExactLabel label,
        IReadOnlySet<string> activeCategories,
        out string? errorCode)
    {
        errorCode = null;
        var hasCategory = !string.IsNullOrWhiteSpace(label.ExpectedCategoryId);

        switch (label.ExpectedOutcome)
        {
            case ClassifyOutcomeKind.Suggestion:
                if (!hasCategory)
                {
                    errorCode = ClassifyErrors.LabelInvalid;
                    return false;
                }

                var categoryId = label.ExpectedCategoryId!.Trim();
                if (!activeCategories.Contains(categoryId))
                {
                    // Missing from active catalogue or archived — fail closed.
                    errorCode = ClassifyErrors.Stale;
                    return false;
                }

                return true;

            case ClassifyOutcomeKind.NoSuggestion:
            case ClassifyOutcomeKind.Conflict:
            case ClassifyOutcomeKind.Stale:
                if (hasCategory)
                {
                    errorCode = ClassifyErrors.LabelInvalid;
                    return false;
                }

                return true;

            default:
                errorCode = ClassifyErrors.LabelInvalid;
                return false;
        }
    }

    private static string ToExpectedOutcomeKind(ClassifyOutcomeKind kind) => kind switch
    {
        ClassifyOutcomeKind.Suggestion => "suggestion",
        ClassifyOutcomeKind.NoSuggestion => "no_suggestion",
        ClassifyOutcomeKind.Conflict => "conflict",
        ClassifyOutcomeKind.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown outcome kind.")
    };
}
