using System.Globalization;
using Tally.Domain.Classify.Evaluation;

namespace Tally.Domain.Classify.Unresolved;

/// <summary>
/// Canonical drift fingerprints for unresolved-pattern groups and reports
/// (FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT / TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-POLICY).
/// Ordinal, culture-invariant, no host/clock/random inputs.
/// </summary>
public static class UnresolvedPatternFingerprint
{
    /// <summary>
    /// Group fingerprint over key + checked aggregates. Identical semantic groups yield
    /// identical fingerprints; any key or total drift changes the hash.
    /// </summary>
    public static string ForGroup(
        string normalizationVersion,
        string normalizedDescription,
        string accountId,
        string amountDirection,
        int transactionCount,
        long checkedSignedAmountMinorTotal,
        long checkedAbsoluteAmountMinorTotal) =>
        CanonicalClassificationHasher.HashParts(
            normalizationVersion,
            normalizedDescription,
            accountId,
            amountDirection,
            transactionCount.ToString(CultureInfo.InvariantCulture),
            checkedSignedAmountMinorTotal.ToString(CultureInfo.InvariantCulture),
            checkedAbsoluteAmountMinorTotal.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Report fingerprint over ordered candidate group fingerprints and accounting totals.
    /// Does not include Ledger snapshot identity (ephemeral re-run stability).
    /// </summary>
    public static string ForReport(
        string normalizationVersion,
        int noSuggestionOutcomeCount,
        int joinedRowCount,
        int candidateRowCount,
        int belowMinimumRowCount,
        int distinctGroupCount,
        int returnedGroupCount,
        int omittedGroupCount,
        int topN,
        int minimumCount,
        IReadOnlyList<string> orderedGroupFingerprints)
    {
        ArgumentNullException.ThrowIfNull(orderedGroupFingerprints);
        var parts = new List<string?>(orderedGroupFingerprints.Count + 10)
        {
            normalizationVersion,
            noSuggestionOutcomeCount.ToString(CultureInfo.InvariantCulture),
            joinedRowCount.ToString(CultureInfo.InvariantCulture),
            candidateRowCount.ToString(CultureInfo.InvariantCulture),
            belowMinimumRowCount.ToString(CultureInfo.InvariantCulture),
            distinctGroupCount.ToString(CultureInfo.InvariantCulture),
            returnedGroupCount.ToString(CultureInfo.InvariantCulture),
            omittedGroupCount.ToString(CultureInfo.InvariantCulture),
            topN.ToString(CultureInfo.InvariantCulture),
            minimumCount.ToString(CultureInfo.InvariantCulture)
        };
        parts.AddRange(orderedGroupFingerprints);
        return CanonicalClassificationHasher.HashParts(parts.ToArray());
    }
}
