namespace Tally.Domain.Ingest.Overlap;

public sealed record ExactReplayKey(string SourceFingerprint, string SelectedAccountId, string AdapterVersion, string LedgerContractVersion);

public sealed record PreviewWindow(ExactReplayKey Key, string ManifestRevisionId, DateOnly StartDate, DateOnly EndDate);

public enum OverlapDecision { ExactReplay, NewPreview, BlockedOverlap, Conflict }

public sealed record OverlapResult(OverlapDecision Decision, string? PriorManifestRevisionId);

public static class OverlapPolicy
{
    public static OverlapResult Evaluate(ExactReplayKey requested, DateOnly startDate, DateOnly endDate, IReadOnlyList<PreviewWindow> existing)
    {
        foreach (var preview in existing)
            if (preview.Key == requested) return new(OverlapDecision.ExactReplay, preview.ManifestRevisionId);
        foreach (var preview in existing)
            if (preview.Key.SourceFingerprint != requested.SourceFingerprint
                && preview.Key.SelectedAccountId == requested.SelectedAccountId
                && startDate <= preview.EndDate
                && endDate >= preview.StartDate) return new(OverlapDecision.BlockedOverlap, null);
        return new(OverlapDecision.NewPreview, null);
    }

    public static OverlapDecision EvaluateImmutableFacts(string existingCandidateId, string currentCandidateId) => StringComparer.Ordinal.Equals(existingCandidateId, currentCandidateId) ? OverlapDecision.ExactReplay : OverlapDecision.Conflict;
}
