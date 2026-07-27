using Tally.Features.Ingest.Commit;

namespace Tally.Tests.Ingest.CommitRecovery;

/// <summary>
/// Test-only crash-point harness for DD-INGEST-COMMIT-RECOVERY recovery matrix.
/// Throws <see cref="CommitFaultException"/> once when the configured boundary is hit.
/// </summary>
public sealed class CommitFaultInjector : ICommitFaultHook
{
    public enum FaultPoint
    {
        None,
        BeforeLedgerCall,
        AfterLedgerCommit,
        BeforeReceiptDurability,
        AfterReceiptDurability,
        BetweenCandidates
    }

    private int hits;

    public CommitFaultInjector(FaultPoint point, string? candidateId = null, int fireOnOccurrence = 1)
    {
        Point = point;
        CandidateId = candidateId;
        FireOnOccurrence = fireOnOccurrence;
    }

    public FaultPoint Point { get; }
    public string? CandidateId { get; }
    public int FireOnOccurrence { get; }
    public int LedgerCallCount { get; private set; }
    public int FaultsThrown { get; private set; }
    public IList<string> ObservedCandidates { get; } = new List<string>();

    public Task BeforeLedgerCallAsync(string batchId, string candidateId, CancellationToken cancellationToken)
    {
        LedgerCallCount++;
        return MaybeFault(FaultPoint.BeforeLedgerCall, candidateId);
    }

    public Task AfterLedgerCommitAsync(string batchId, string candidateId, string transactionId, CancellationToken cancellationToken) =>
        MaybeFault(FaultPoint.AfterLedgerCommit, candidateId);

    public Task BeforeReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken) =>
        MaybeFault(FaultPoint.BeforeReceiptDurability, candidateId);

    public Task AfterReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken) =>
        MaybeFault(FaultPoint.AfterReceiptDurability, candidateId);

    public Task BetweenCandidatesAsync(string batchId, string completedCandidateId, CancellationToken cancellationToken) =>
        MaybeFault(FaultPoint.BetweenCandidates, completedCandidateId);

    private Task MaybeFault(FaultPoint point, string candidateId)
    {
        ObservedCandidates.Add($"{point}:{candidateId}");
        if (Point != point)
        {
            return Task.CompletedTask;
        }

        if (CandidateId is not null && !string.Equals(CandidateId, candidateId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        hits++;
        if (hits != FireOnOccurrence)
        {
            return Task.CompletedTask;
        }

        FaultsThrown++;
        throw new CommitFaultException(point, candidateId);
    }
}

public sealed class CommitFaultException(CommitFaultInjector.FaultPoint point, string candidateId)
    : Exception($"Injected commit fault at {point} for candidate {candidateId}.")
{
    public CommitFaultInjector.FaultPoint Point { get; } = point;
    public string CandidateId { get; } = candidateId;
}
