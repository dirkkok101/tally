using System.Globalization;
using Tally.Domain.Classify.Rules;

namespace Tally.Domain.Classify.Unresolved;

/// <summary>
/// Pure deterministic grouping, ordering, top-N, fingerprint, and disclosure policy for
/// classify.unresolved.report (FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT / bd-elq8).
/// Independent of SQLite, Ledger I/O, persistence, and rule authority.
/// Never logs descriptions, amounts, account IDs, or input rows.
/// </summary>
public static class UnresolvedPatternGroupingPolicy
{
    public const int MinTopN = 1;
    public const int MaxTopN = 500;
    public const int MinMinimumCount = 2;
    public const int MaxMinimumCount = 500;

    /// <summary>Closed amount-direction wire values aligned with classification_v1 Ledger vocabulary.</summary>
    public static class AmountDirections
    {
        public const string Expense = "expense";
        public const string Income = "income";
        public const string Zero = "zero";
    }

    public static class ErrorCodes
    {
        public const string InvalidInput = "CLASSIFY-INPUT-INVALID";
        public const string ResourceLimit = "CLASSIFY-RESOURCE-LIMIT";
        public const string Integrity = "CLASSIFY-INTEGRITY";
    }

    /// <summary>
    /// One already-joined unresolved row. Callers supply normalized description and public
    /// account/direction/amount — the policy never normalizes, loads Ledger, or invents labels.
    /// Contains no transaction ID (disclosure boundary).
    /// </summary>
    public sealed record JoinedRow(
        string NormalizationVersion,
        string NormalizedDescription,
        string AccountId,
        string AmountDirection,
        long SignedAmountMinor);

    /// <summary>
    /// One owner-visible aggregate group. No transaction IDs, paths, rule proposals,
    /// activation, feedback, or durable-artifact instructions.
    /// </summary>
    public sealed record Group(
        int Rank,
        string NormalizationVersion,
        string NormalizedDescription,
        string AccountId,
        string AmountDirection,
        int TransactionCount,
        long CheckedSignedAmountMinorTotal,
        long CheckedAbsoluteAmountMinorTotal,
        string GroupFingerprint);

    /// <summary>Successful pure grouping result with FR accounting totals.</summary>
    public sealed record Success(
        IReadOnlyList<Group> Groups,
        int NoSuggestionOutcomeCount,
        int JoinedRowCount,
        int CandidateRowCount,
        int BelowMinimumRowCount,
        int DistinctGroupCount,
        int ReturnedGroupCount,
        int OmittedGroupCount,
        int BoundedRequestTopN,
        int BoundedRequestMinimumCount,
        string ReportFingerprint,
        string NormalizationVersion);

    /// <summary>
    /// Group joined unresolved rows. On failure returns error code and empty groups
    /// (no partial disclosure).
    /// </summary>
    public static bool TryGroup(
        IReadOnlyList<JoinedRow> rows,
        int topN,
        int minimumCount,
        out Success? result,
        out string? errorCode)
    {
        result = null;
        errorCode = null;

        if (rows is null)
        {
            errorCode = ErrorCodes.InvalidInput;
            return false;
        }

        if (topN is < MinTopN or > MaxTopN || minimumCount is < MinMinimumCount or > MaxMinimumCount)
        {
            errorCode = ErrorCodes.ResourceLimit;
            return false;
        }

        // Aggregate by ordinal key equality.
        var buckets = new Dictionary<GroupKey, Accumulator>(rows.Count, GroupKeyComparer.Instance);
        string? sharedNormalization = null;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is null)
            {
                errorCode = ErrorCodes.InvalidInput;
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.NormalizationVersion)
                || string.IsNullOrWhiteSpace(row.AccountId)
                || row.NormalizedDescription is null)
            {
                errorCode = ErrorCodes.InvalidInput;
                return false;
            }

            if (!IsValidAmountDirection(row.AmountDirection))
            {
                errorCode = ErrorCodes.InvalidInput;
                return false;
            }

            // Preserve raw nonblank NormalizationVersion/AccountId bytes for ordinal key
            // identity, owner-visible output, ordering, and fingerprints (no trim/canonicalize).
            var normalization = row.NormalizationVersion;
            if (sharedNormalization is null)
            {
                sharedNormalization = normalization;
            }
            else if (!string.Equals(sharedNormalization, normalization, StringComparison.Ordinal))
            {
                // Mixed normalization versions are impossible accounting for one evaluation.
                errorCode = ErrorCodes.Integrity;
                return false;
            }

            var key = new GroupKey(
                normalization,
                row.NormalizedDescription,
                row.AccountId,
                row.AmountDirection);

            if (!buckets.TryGetValue(key, out var acc))
            {
                acc = new Accumulator();
                buckets[key] = acc;
            }

            try
            {
                checked
                {
                    acc.Count++;
                    acc.SignedTotal += row.SignedAmountMinor;
                    // Math.Abs(long.MinValue) throws OverflowException — never approximate.
                    acc.AbsoluteTotal += Math.Abs(row.SignedAmountMinor);
                }
            }
            catch (OverflowException)
            {
                errorCode = ErrorCodes.ResourceLimit;
                return false;
            }
        }

        var noSuggestionCount = rows.Count;
        var joinedCount = rows.Count;

        // Partition groups by minimumCount.
        var belowMinimumRowCount = 0;
        var candidateRowCount = 0;
        var candidates = new List<(GroupKey Key, Accumulator Acc, string Fingerprint)>(buckets.Count);

        foreach (var (key, acc) in buckets)
        {
            if (acc.Count < minimumCount)
            {
                try
                {
                    checked { belowMinimumRowCount += acc.Count; }
                }
                catch (OverflowException)
                {
                    errorCode = ErrorCodes.ResourceLimit;
                    return false;
                }

                continue;
            }

            try
            {
                checked { candidateRowCount += acc.Count; }
            }
            catch (OverflowException)
            {
                errorCode = ErrorCodes.ResourceLimit;
                return false;
            }

            var fingerprint = UnresolvedPatternFingerprint.ForGroup(
                key.NormalizationVersion,
                key.NormalizedDescription,
                key.AccountId,
                key.AmountDirection,
                acc.Count,
                acc.SignedTotal,
                acc.AbsoluteTotal);
            candidates.Add((key, acc, fingerprint));
        }

        // Accounting identity: joined == candidate + belowMinimum
        if (joinedCount != candidateRowCount + belowMinimumRowCount)
        {
            errorCode = ErrorCodes.Integrity;
            return false;
        }

        if (joinedCount != noSuggestionCount)
        {
            errorCode = ErrorCodes.Integrity;
            return false;
        }

        // Order candidates: count desc, then description, account, direction, fingerprint (ordinal).
        candidates.Sort(static (a, b) =>
        {
            var cmp = b.Acc.Count.CompareTo(a.Acc.Count);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.Key.NormalizedDescription, b.Key.NormalizedDescription);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.Key.AccountId, b.Key.AccountId);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.Key.AmountDirection, b.Key.AmountDirection);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Fingerprint, b.Fingerprint);
        });

        var distinctGroupCount = candidates.Count;
        var returnedCount = Math.Min(topN, distinctGroupCount);
        var omittedCount = distinctGroupCount - returnedCount;

        var groups = new Group[returnedCount];
        var orderedFingerprints = new string[returnedCount];
        for (var i = 0; i < returnedCount; i++)
        {
            var (key, acc, fingerprint) = candidates[i];
            groups[i] = new Group(
                Rank: i + 1,
                NormalizationVersion: key.NormalizationVersion,
                NormalizedDescription: key.NormalizedDescription,
                AccountId: key.AccountId,
                AmountDirection: key.AmountDirection,
                TransactionCount: acc.Count,
                CheckedSignedAmountMinorTotal: acc.SignedTotal,
                CheckedAbsoluteAmountMinorTotal: acc.AbsoluteTotal,
                GroupFingerprint: fingerprint);
            orderedFingerprints[i] = fingerprint;
        }

        var normalizationVersion = sharedNormalization ?? string.Empty;
        var reportFingerprint = UnresolvedPatternFingerprint.ForReport(
            normalizationVersion,
            noSuggestionCount,
            joinedCount,
            candidateRowCount,
            belowMinimumRowCount,
            distinctGroupCount,
            returnedCount,
            omittedCount,
            topN,
            minimumCount,
            orderedFingerprints);

        result = new Success(
            groups,
            noSuggestionCount,
            joinedCount,
            candidateRowCount,
            belowMinimumRowCount,
            distinctGroupCount,
            returnedCount,
            omittedCount,
            topN,
            minimumCount,
            reportFingerprint,
            normalizationVersion);
        return true;
    }

    public static bool IsValidAmountDirection(string? direction) =>
        direction is AmountDirections.Expense
            or AmountDirections.Income
            or AmountDirections.Zero
            // Accept closed rule vocabulary aliases used after evaluation join mapping.
            or ClassificationRuleVocabulary.DirectionInflow
            or ClassificationRuleVocabulary.DirectionOutflow;

    private readonly record struct GroupKey(
        string NormalizationVersion,
        string NormalizedDescription,
        string AccountId,
        string AmountDirection);

    private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
    {
        public static readonly GroupKeyComparer Instance = new();

        public bool Equals(GroupKey x, GroupKey y) =>
            string.Equals(x.NormalizationVersion, y.NormalizationVersion, StringComparison.Ordinal)
            && string.Equals(x.NormalizedDescription, y.NormalizedDescription, StringComparison.Ordinal)
            && string.Equals(x.AccountId, y.AccountId, StringComparison.Ordinal)
            && string.Equals(x.AmountDirection, y.AmountDirection, StringComparison.Ordinal);

        public int GetHashCode(GroupKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.NormalizationVersion, StringComparer.Ordinal);
            hash.Add(obj.NormalizedDescription, StringComparer.Ordinal);
            hash.Add(obj.AccountId, StringComparer.Ordinal);
            hash.Add(obj.AmountDirection, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class Accumulator
    {
        public int Count;
        public long SignedTotal;
        public long AbsoluteTotal;
    }
}
