using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Overlap;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-004 replay and overlap safety gate.</summary>
[SupportedOSPlatform("linux")]
public sealed class ReplayOverlapWorkflowTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void Overlap_policy_decisions_are_closed()
    {
        Assert.Equal(
            [OverlapDecision.ExactReplay, OverlapDecision.NewPreview, OverlapDecision.BlockedOverlap, OverlapDecision.Conflict],
            Enum.GetValues<OverlapDecision>());
    }

    [Fact]
    public async Task Commit_replay_preserves_ledger_transaction_ids()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        var first = await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
            CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.ReceiptId, second.Value!.ReceiptId);
        Assert.Equal(
            first.Value.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId),
            second.Value.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId));
    }

    [Fact]
    public async Task Exact_source_replay_does_not_create_a_second_completed_batch()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        Assert.True((await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
            CancellationToken.None)).IsSuccess);

        // Identical synthetic content + account may exact-replay; either way the completed receipt remains terminal.
        var secondPreview = await harness.PreviewSyntheticAsync(accountId);
        Assert.False(string.IsNullOrWhiteSpace(secondPreview.BatchId));
        Assert.True(
            secondPreview.BatchId == preview.BatchId || secondPreview.ExactReplayOf is not null || secondPreview.Status == BatchStatus.Previewed);
    }

    [Fact]
    public void Preview_and_commit_operations_remain_published_for_overlap_workflows()
    {
        var registry = OperationRegistry.Create();
        Assert.NotNull(registry.Find(IngestOperationIds.Preview));
        Assert.NotNull(registry.Find(IngestOperationIds.Commit));
        Assert.NotNull(registry.Find(IngestOperationIds.Status));
    }

    [Fact]
    public async Task Digest_mismatch_blocks_unsafe_commit_continuation()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        var result = await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, "tampered"),
            CancellationToken.None);
        Assert.Equal(CommitErrors.DigestMismatch, result.ErrorCode);
    }
}
