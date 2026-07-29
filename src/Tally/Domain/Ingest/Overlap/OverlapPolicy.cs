using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tally.Domain.Ingest.Overlap;

public sealed record ExactReplayKey(string SourceFingerprint, string SelectedAccountId, string AdapterVersion, string LedgerContractVersion);

public sealed record PreviewWindow(ExactReplayKey Key, string ManifestRevisionId, DateOnly StartDate, DateOnly EndDate);

public enum OverlapDecision { ExactReplay, NewPreview, BlockedOverlap, Conflict }

public sealed record OverlapResult(OverlapDecision Decision, string? PriorManifestRevisionId);

public static class OverlapPolicy
{
    /// <summary>
    /// Inclusive statement periods on the same account:
    /// exact replay when the full Exact Replay key matches; interior overlap blocks;
    /// pure endpoint boundary touch (prior.end == next.start or reverse) is allowed so
    /// consecutive bank statements can be imported. Shared boundary rows are handled
    /// separately via economic-fact keys (see <see cref="EconomicFactKey"/>).
    /// </summary>
    public static OverlapResult Evaluate(ExactReplayKey requested, DateOnly startDate, DateOnly endDate, IReadOnlyList<PreviewWindow> existing)
    {
        foreach (var preview in existing)
        {
            if (preview.Key == requested)
            {
                return new(OverlapDecision.ExactReplay, preview.ManifestRevisionId);
            }
        }

        foreach (var preview in existing)
        {
            if (preview.Key.SourceFingerprint != requested.SourceFingerprint
                && preview.Key.SelectedAccountId == requested.SelectedAccountId
                && IsInteriorOverlap(startDate, endDate, preview.StartDate, preview.EndDate))
            {
                return new(OverlapDecision.BlockedOverlap, null);
            }
        }

        return new(OverlapDecision.NewPreview, null);
    }

    public static OverlapDecision EvaluateImmutableFacts(string existingCandidateId, string currentCandidateId) =>
        StringComparer.Ordinal.Equals(existingCandidateId, currentCandidateId)
            ? OverlapDecision.ExactReplay
            : OverlapDecision.Conflict;

    /// <summary>
    /// True when inclusive intervals share more than a single endpoint-boundary day.
    /// Pure boundary touch: prior.end == next.start (or reverse) with no shared interior days.
    /// </summary>
    public static bool IsInteriorOverlap(DateOnly startDate, DateOnly endDate, DateOnly otherStart, DateOnly otherEnd)
    {
        // Disjoint inclusive ranges.
        if (endDate < otherStart || otherEnd < startDate)
        {
            return false;
        }

        // Pure endpoint boundary touch — allowed for consecutive statements.
        if (IsBoundaryTouchOnly(startDate, endDate, otherStart, otherEnd))
        {
            return false;
        }

        return true;
    }

    public static bool IsBoundaryTouchOnly(DateOnly startDate, DateOnly endDate, DateOnly otherStart, DateOnly otherEnd) =>
        (endDate == otherStart && startDate <= endDate && otherStart <= otherEnd)
        || (otherEnd == startDate && otherStart <= otherEnd && startDate <= endDate);

    /// <summary>
    /// Shared boundary calendar days between the requested period and prior same-account windows
    /// (where the only contact is endpoint touch). Used to suppress re-import of shared rows.
    /// </summary>
    public static IReadOnlyList<DateOnly> SharedBoundaryDates(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<PreviewWindow> existing,
        string selectedAccountId)
    {
        var dates = new HashSet<DateOnly>();
        foreach (var preview in existing)
        {
            if (!string.Equals(preview.Key.SelectedAccountId, selectedAccountId, StringComparison.Ordinal))
            {
                continue;
            }

            if (endDate == preview.StartDate)
            {
                dates.Add(endDate);
            }
            else if (startDate == preview.EndDate)
            {
                dates.Add(startDate);
            }
        }

        return dates.OrderBy(static date => date).ToArray();
    }

    /// <summary>
    /// Cross-file economic identity for a statement row (excludes source fingerprint / structural position).
    /// Deterministic: same account + amount + currency + date + FormC description → same key.
    /// </summary>
    public static string EconomicFactKey(
        string accountId,
        long signedAmountMinor,
        string currencyCode,
        string transactionDate,
        string originalDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionDate);
        ArgumentNullException.ThrowIfNull(originalDescription);

        var description = originalDescription.Normalize(NormalizationForm.FormC);
        var payload = string.Join('\n',
            "economic-fact-v1",
            accountId,
            signedAmountMinor.ToString(CultureInfo.InvariantCulture),
            currencyCode,
            transactionDate,
            description);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
