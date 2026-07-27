using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Recovery;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Tests.Ingest.CommitRecovery;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST failure handling, abandon, and cleanup gate.</summary>
[SupportedOSPlatform("linux")]
public sealed class FailureCleanupWorkflowTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void Abandon_cleanup_and_status_are_published()
    {
        var registry = OperationRegistry.Create();
        Assert.NotNull(registry.Find(IngestOperationIds.Abandon));
        Assert.NotNull(registry.Find(IngestOperationIds.Cleanup));
        Assert.NotNull(registry.Find(IngestOperationIds.Status));
    }

    [Fact]
    public async Task Preview_can_be_abandoned_and_cleaned()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var abandon = await harness.CreateAbandon().HandleAsync(
            new AbandonCommand(preview.BatchId, "owner-stop"),
            CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);
        Assert.Equal(BatchStatus.Abandoned, abandon.Value!.Status);

        var cleanup = await harness.CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Abandoned),
            CancellationToken.None);
        Assert.True(cleanup.IsSuccess, cleanup.ErrorCode);
        Assert.Equal(BatchStatus.Cleaned, cleanup.Value!.Status);
    }

    [Fact]
    public async Task Incomplete_batch_cleanup_is_retained_for_recovery()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var result = await harness.CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.RetainedForRecovery, result.ErrorCode);
    }

    [Fact]
    public async Task Interrupted_commit_can_be_abandoned()
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

        var abandon = await harness.CreateAbandon().HandleAsync(
            new AbandonCommand(approved.BatchId, "stop-after-partial"),
            CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);
        Assert.True(abandon.Value!.PriorLedgerEffectCount >= 1);
    }

    [Fact]
    public async Task Completed_batch_cleanup_removes_manifest_artifacts()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        Assert.True((await harness.CreateSaga().ExecuteAsync(
            new CommitCommand(approved.BatchId, approved.ManifestRevisionId, approved.Digest),
            CancellationToken.None)).IsSuccess);

        var cleanup = await harness.CreateCleanup().HandleAsync(
            new CleanupCommand(approved.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.True(cleanup.IsSuccess, cleanup.ErrorCode);
        Assert.Contains(ArtifactKind.Manifest, cleanup.Value!.RemovedArtifactKinds);
    }

    [Fact]
    public async Task Source_file_survives_abandon_and_cleanup()
    {
        var accountId = await harness.CreateAccountAsync();
        var path = Path.Combine(harness.Root, $"keep-{Guid.NewGuid():N}.pdf");
        var bytes = IngestE2EHarness.CreateLayoutAPdf();
        await File.WriteAllBytesAsync(path, bytes);
        var preview = await harness.PreviewPathAsync(accountId, path);
        Assert.True((await harness.CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        Assert.True((await harness.CreateCleanup().HandleAsync(new CleanupCommand(preview.BatchId, BatchStatus.Abandoned), CancellationToken.None)).IsSuccess);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
}
