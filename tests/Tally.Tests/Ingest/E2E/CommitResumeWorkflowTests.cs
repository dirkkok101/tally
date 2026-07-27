using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Tests.Ingest.CommitRecovery;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-003 commit and resume gate (published surface).</summary>
[SupportedOSPlatform("linux")]
// TC-INGEST-DURABLE-RECEIPT-RESUME-CONTRACT / FR-INGEST-DURABLE-RECEIPT-RESUME
public sealed class CommitResumeWorkflowTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void Commit_and_resume_are_published()
    {
        var registry = OperationRegistry.Create();
        Assert.NotNull(registry.Find(IngestOperationIds.Commit));
        Assert.NotNull(registry.Find(IngestOperationIds.Resume));
        Assert.Equal("tally ingest commit", registry.Find(IngestOperationIds.Commit)!.CliPath);
        Assert.Equal("tally ingest resume", registry.Find(IngestOperationIds.Resume)!.CliPath);
    }

    [Fact]
    public async Task Approved_batch_commits_to_complete_receipt()
    {
        var approved = await harness.PrepareApprovedAsync();
        var result = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.Equal(ImportReceiptStatus.Completed, result.Status);
        Assert.True(result.Counts.Accepted >= 1);
        Assert.True(await harness.CountResolvableLedgerTransactionsAsync(result) >= 1);
    }

    [Fact]
    public async Task Interrupted_commit_resumes_to_completion()
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        var status = await harness.StatusAsync(approved.BatchId);
        Assert.NotNull(status.Detail);

        var resume = await harness.ResumeAsync(approved.BatchId);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Status);
        Assert.False(string.IsNullOrWhiteSpace(resume.CreatedAt));
    }

    [Fact]
    public async Task Resume_does_not_require_source_reparse()
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        foreach (var pdf in Directory.GetFiles(harness.Root, "*.pdf", SearchOption.AllDirectories))
        {
            File.Delete(pdf);
        }

        var resume = await harness.ResumeAsync(approved.BatchId);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Status);
    }

    [Fact]
    public async Task Unapproved_batch_cannot_commit()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await harness.TryCommitAsync(
            preview.BatchId, preview.ManifestRevisionId!, inspect.CanonicalDigest);
        Assert.False(ok);
        Assert.Equal(CommitErrors.NotApproved, error);
    }

    [Theory]
    [InlineData(CommitFaultInjector.FaultPoint.BeforeLedgerCall)]
    [InlineData(CommitFaultInjector.FaultPoint.AfterLedgerCommit)]
    [InlineData(CommitFaultInjector.FaultPoint.BeforeReceiptDurability)]
    [InlineData(CommitFaultInjector.FaultPoint.AfterReceiptDurability)]
    [InlineData(CommitFaultInjector.FaultPoint.BetweenCandidates)]
    public async Task Crash_window_is_resumable_without_second_canonical_set(CommitFaultInjector.FaultPoint point)
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(point);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        // The crash must actually happen — a fault that never fires proves nothing about resume.
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        var resume = await harness.ResumeAsync(approved.BatchId);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Status);
        var ledgerCount = await harness.CountResolvableLedgerTransactionsAsync(resume);
        Assert.Equal(resume.Counts.Accepted + resume.Counts.ExactDuplicates, ledgerCount);

        // Idempotent re-commit preserves receipt and ledger ids.
        var again = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.Equal(resume.ReceiptId, again.ReceiptId);
        Assert.Equal(
            resume.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId),
            again.CandidateOutcomes.Where(o => o.LedgerTransactionId is not null).Select(o => o.LedgerTransactionId));
    }

    [Fact]
    public async Task Digest_mismatch_blocks_commit()
    {
        var approved = await harness.PrepareApprovedAsync();
        var (ok, error, _) = await harness.TryCommitAsync(
            approved.BatchId, approved.ManifestRevisionId, "tampered-digest");
        Assert.False(ok);
        Assert.Equal(CommitErrors.DigestMismatch, error);
    }

    [Fact]
    public async Task Wrong_manifest_revision_blocks_commit()
    {
        var approved = await harness.PrepareApprovedAsync();
        var (ok, error, _) = await harness.TryCommitAsync(
            approved.BatchId, "missing-revision", approved.Digest);
        Assert.False(ok);
        Assert.Equal(CommitErrors.NotFound, error);
    }

    [Fact]
    public async Task Resume_unknown_batch_fails_closed()
    {
        var (ok, error, _) = await harness.TryResumeAsync("no-such-batch");
        Assert.False(ok);
        Assert.Equal(ResumeErrors.NotFound, error);
    }

    [Fact]
    public async Task Completed_batch_resume_is_terminal_safe()
    {
        var approved = await harness.PrepareApprovedAsync();
        var first = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        // Resume of a completed batch deterministically returns the completed receipt unchanged.
        var (ok, error, value) = await harness.TryResumeAsync(approved.BatchId);
        Assert.True(ok, error);
        Assert.Equal(ImportReceiptStatus.Completed, value!.Status);
        Assert.Equal(first.ReceiptId, value.ReceiptId);
        Assert.Equal(
            await harness.CountResolvableLedgerTransactionsAsync(first),
            await harness.CountResolvableLedgerTransactionsAsync(value));
    }

    [Fact]
    public async Task Status_after_successful_commit_shows_completed()
    {
        var approved = await harness.PrepareApprovedAsync();
        _ = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var status = await harness.StatusAsync(approved.BatchId);
        Assert.NotNull(status.Detail);
        Assert.Equal(BatchStatus.Completed, status.Detail!.Summary.Status);
    }

    [Fact]
    public async Task Retry_same_commit_key_is_idempotent()
    {
        var approved = await harness.PrepareApprovedAsync();
        var first = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var second = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(first.Counts.Accepted, second.Counts.Accepted);
    }
}
