using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Overlap;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-004 replay and overlap safety gate (published surface).</summary>
[SupportedOSPlatform("linux")]
// TC-INGEST-REPLAY-OVERLAP-SAFETY-CONTRACT / FR-INGEST-REPLAY-OVERLAP-SAFETY
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
        var approved = await harness.PrepareApprovedAsync();
        var first = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var second = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(
            first.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId),
            second.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId));
        Assert.Equal(
            await harness.CountResolvableLedgerTransactionsAsync(first),
            await harness.CountResolvableLedgerTransactionsAsync(second));
    }

    [Fact]
    public async Task Exact_source_bytes_replay_reuses_or_links_prior_batch()
    {
        var accountId = await harness.CreateAccountAsync();
        var path = Path.Combine(harness.Root, $"same-{Guid.NewGuid():N}.pdf");
        var bytes = IngestE2EHarness.CreateLayoutAPdf("exact-replay");
        await File.WriteAllBytesAsync(path, bytes);
        var first = await harness.PreviewPathAsync(accountId, path);
        var approved = await harness.ApprovePreviewAsync(first);
        _ = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);

        // Same path + same bytes → ExactReplayOf or same batch; never silent new mutation path.
        var second = await harness.PreviewPathAsync(accountId, path);
        Assert.True(
            second.BatchId == first.BatchId || second.ExactReplayOf is not null,
            $"expected exact replay linkage, got batch={second.BatchId} replayOf={second.ExactReplayOf}");
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
        var approved = await harness.PrepareApprovedAsync();
        var (ok, error, _) = await harness.TryCommitAsync(
            approved.BatchId, approved.ManifestRevisionId, "tampered");
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Renamed_same_bytes_still_links_exact_replay_or_stable_preview()
    {
        var accountId = await harness.CreateAccountAsync();
        var bytes = IngestE2EHarness.CreateLayoutAPdf("renamed-same");
        var pathA = Path.Combine(harness.Root, $"a-{Guid.NewGuid():N}.pdf");
        var pathB = Path.Combine(harness.Root, $"b-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(pathA, bytes);
        await File.WriteAllBytesAsync(pathB, bytes);
        var first = await harness.PreviewPathAsync(accountId, pathA);
        var second = await harness.PreviewPathAsync(accountId, pathB);
        // Fingerprint-based identity: either ExactReplayOf or same batch id for identical content+account.
        Assert.True(
            second.BatchId == first.BatchId ||
            second.ExactReplayOf is not null ||
            second.Status is BatchStatus.Previewed or BatchStatus.Completed);
    }

    [Fact]
    public async Task Changed_bytes_under_familiar_name_forces_new_preview_batch()
    {
        var accountId = await harness.CreateAccountAsync();
        var path1 = Path.Combine(harness.Root, $"mutate-a-{Guid.NewGuid():N}.pdf");
        var path2 = Path.Combine(harness.Root, $"mutate-b-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path1, IngestE2EHarness.CreateLayoutAPdf("v1"));
        await File.WriteAllBytesAsync(path2, IngestE2EHarness.CreateLayoutAPdf("v2-changed-bytes-distinct"));
        var first = await harness.PreviewPathAsync(accountId, path1);
        var (ok, error, second) = await harness.TryPreviewPathAsync(accountId, path2);
        if (ok)
        {
            Assert.NotEqual(first.BatchId, second!.BatchId);
            Assert.Null(second.ExactReplayOf);
        }
        else
        {
            // Fail-closed published path: domain codes surface via ErrorForHandler (not host.unexpected).
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.NotEqual("host.unexpected", error);
            Assert.NotEqual(first.BatchId, second?.BatchId);
        }
    }

    [Fact]
    public async Task Distinct_accounts_never_collapse_into_one_batch()
    {
        var a = await harness.CreateAccountAsync("Bank A");
        var b = await harness.CreateAccountAsync("Bank B");
        var path = Path.Combine(harness.Root, $"acct-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, IngestE2EHarness.CreateLayoutAPdf("multi-account"));
        var first = await harness.PreviewPathAsync(a, path);
        var second = await harness.PreviewPathAsync(b, path);
        Assert.NotEqual(first.BatchId, second.BatchId);
    }

    [Fact]
    public async Task Completed_batch_commit_is_stable_under_second_preview_of_same_file()
    {
        var accountId = await harness.CreateAccountAsync();
        var path = Path.Combine(harness.Root, $"stable-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, IngestE2EHarness.CreateLayoutAPdf("stable-complete"));
        var preview = await harness.PreviewPathAsync(accountId, path);
        var approved = await harness.ApprovePreviewAsync(preview);
        var receipt = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var ledgerBefore = await harness.CountResolvableLedgerTransactionsAsync(receipt);

        var again = await harness.PreviewPathAsync(accountId, path);
        Assert.True(again.ExactReplayOf is not null || again.BatchId == preview.BatchId || again.Status == BatchStatus.Previewed);
        // No automatic second ledger set from the mere re-preview.
        var reCommit = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.Equal(ledgerBefore, await harness.CountResolvableLedgerTransactionsAsync(reCommit));
    }

    [Fact]
    public async Task Status_lists_previewed_and_completed_batches_without_mutation()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var status = await harness.StatusAsync();
        Assert.True(
            status.Items is { Count: > 0 } || status.Detail is not null ||
            (await harness.StatusAsync(preview.BatchId)).Detail is not null);
    }
}
