using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Tests.Ingest.CommitRecovery;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-003 commit and resume gate.</summary>
[SupportedOSPlatform("linux")]
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
    }

    [Fact]
    public async Task Approved_batch_commits_to_complete_receipt()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        var result = await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, result.Value!.Status);
        Assert.True(result.Value.Counts.Accepted >= 1);
    }

    [Fact]
    public async Task Interrupted_commit_resumes_to_completion()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        var work = await new CommitStateStore(
                new IngestDatabase(harness.Root, new IngestArtifactProtection()),
                new BatchErrorEventStore())
            .LoadWorkItemsAsync(approved.BatchId, approved.ManifestRevisionId, CancellationToken.None);
        var first = work[0].CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, first);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            harness.CreateSaga(injector).ExecuteAsync(
                new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
                CancellationToken.None));

        var resume = await harness.CreateResume().HandleAsync(new ResumeCommand(approved.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
    }

    [Fact]
    public async Task Resume_does_not_require_source_reparse()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        var work = await new CommitStateStore(
                new IngestDatabase(harness.Root, new IngestArtifactProtection()),
                new BatchErrorEventStore())
            .LoadWorkItemsAsync(approved.BatchId, approved.ManifestRevisionId, CancellationToken.None);
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, work[0].CandidateId);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            harness.CreateSaga(injector).ExecuteAsync(
                new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
                CancellationToken.None));

        foreach (var pdf in Directory.GetFiles(harness.Root, "*.pdf", SearchOption.AllDirectories))
        {
            File.Delete(pdf);
        }

        var resume = await harness.CreateResume().HandleAsync(new ResumeCommand(approved.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
    }

    [Fact]
    public async Task Unapproved_batch_cannot_commit()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var result = await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(preview.BatchId, preview.ManifestRevisionId!, "digest"),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
    }
}
