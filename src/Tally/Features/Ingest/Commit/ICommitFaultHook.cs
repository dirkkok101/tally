namespace Tally.Features.Ingest.Commit;

/// <summary>
/// Internal test-only seam for crash-window injection (DD-INGEST-COMMIT-RECOVERY).
/// Production wiring always uses <see cref="NoopCommitFaultHook"/>.
/// </summary>
public interface ICommitFaultHook
{
    Task BeforeLedgerCallAsync(string batchId, string candidateId, CancellationToken cancellationToken);

    Task AfterLedgerCommitAsync(string batchId, string candidateId, string transactionId, CancellationToken cancellationToken);

    Task BeforeReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken);

    Task AfterReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken);

    Task BetweenCandidatesAsync(string batchId, string completedCandidateId, CancellationToken cancellationToken);
}

public sealed class NoopCommitFaultHook : ICommitFaultHook
{
    public static NoopCommitFaultHook Instance { get; } = new();

    public Task BeforeLedgerCallAsync(string batchId, string candidateId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AfterLedgerCommitAsync(string batchId, string candidateId, string transactionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task BeforeReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task AfterReceiptDurabilityAsync(string batchId, string candidateId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task BetweenCandidatesAsync(string batchId, string completedCandidateId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
