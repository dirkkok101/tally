using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Recovery;
using Tally.Tests.Ingest.CommitRecovery;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-005 failure handling, abandon, and cleanup gate (published surface).</summary>
[SupportedOSPlatform("linux")]
// TC-INGEST-ARTIFACT-CLEANUP-CONTRACT / FR-INGEST-ARTIFACT-CLEANUP
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
        var abandon = await harness.AbandonAsync(preview.BatchId, "owner-stop");
        Assert.Equal(BatchStatus.Abandoned, abandon.Status);

        var cleanup = await harness.CleanupAsync(preview.BatchId, BatchStatus.Abandoned);
        Assert.Equal(BatchStatus.Cleaned, cleanup.Status);
    }

    [Fact]
    public async Task Incomplete_batch_cleanup_is_retained_for_recovery()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var (ok, error, _) = await harness.TryCleanupAsync(preview.BatchId, BatchStatus.Completed);
        Assert.False(ok);
        Assert.Equal(CleanupErrors.RetainedForRecovery, error);
    }

    [Fact]
    public async Task Interrupted_commit_can_be_abandoned()
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        var abandon = await harness.AbandonAsync(approved.BatchId, "stop-after-partial");
        Assert.True(abandon.PriorLedgerEffectCount >= 1);
    }

    [Fact]
    public async Task Completed_batch_cleanup_removes_manifest_artifacts()
    {
        var approved = await harness.PrepareApprovedAsync();
        _ = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var cleanup = await harness.CleanupAsync(approved.BatchId, BatchStatus.Completed);
        Assert.Contains(ArtifactKind.Manifest, cleanup.RemovedArtifactKinds);
    }

    [Fact]
    public async Task Source_file_survives_abandon_and_cleanup()
    {
        var accountId = await harness.CreateAccountAsync();
        var path = Path.Combine(harness.Root, $"keep-{Guid.NewGuid():N}.pdf");
        var bytes = IngestE2EHarness.CreateLayoutAPdf();
        await File.WriteAllBytesAsync(path, bytes);
        var preview = await harness.PreviewPathAsync(accountId, path);
        _ = await harness.AbandonAsync(preview.BatchId, "x");
        _ = await harness.CleanupAsync(preview.BatchId, BatchStatus.Abandoned);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Abandon_unknown_batch_returns_stable_error()
    {
        var (ok, error, _) = await harness.TryAbandonAsync("missing", "reason");
        Assert.False(ok);
        Assert.Equal(AbandonErrors.NotFound, error);
    }

    [Fact]
    public async Task Cleanup_unknown_batch_returns_stable_error()
    {
        var (ok, error, _) = await harness.TryCleanupAsync("missing", BatchStatus.Abandoned);
        Assert.False(ok);
        Assert.Equal(CleanupErrors.NotFound, error);
    }

    [Fact]
    public async Task Abandon_requires_reason()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var (ok, error, _) = await harness.TryAbandonAsync(preview.BatchId, "");
        Assert.False(ok);
        Assert.Equal(AbandonErrors.InvalidInput, error);
    }

    [Fact]
    public async Task Status_after_abandon_reports_abandoned()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        _ = await harness.AbandonAsync(preview.BatchId, "status-check");
        var status = await harness.StatusAsync(preview.BatchId);
        Assert.Equal(BatchStatus.Abandoned, status.Detail!.Summary.Status);
    }

    [Fact]
    public async Task Status_after_interrupted_commit_exposes_recovery_frontier()
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        var status = await harness.StatusAsync(approved.BatchId);
        Assert.NotNull(status.Detail);
        Assert.Equal(BatchStatus.Interrupted, status.Detail!.Summary.Status);
        // BetweenCandidates interrupts before the next attempt starts: the durable frontier is
        // the accepted terminal work plus an Interrupted receipt (remaining candidates are pending).
        Assert.Equal(ImportReceiptStatus.Interrupted, status.Detail.ReceiptStatus);
        Assert.True(status.Detail.TerminalCounts.AcceptedCandidates >= 1);
    }

    [Fact]
    public async Task Cleanup_of_abandoned_preserves_ledger_effects()
    {
        var approved = await harness.PrepareApprovedAsync();
        var fault = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates);
        var interrupted = await harness.CommitWithFaultAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest, fault);
        Assert.False(interrupted.Ok);
        Assert.True(fault.FaultsThrown >= 1);

        // BetweenCandidates fires after the first accepted write, so at least one durable
        // ledger effect must be visible to abandon — and must survive cleanup.
        var abandon = await harness.AbandonAsync(approved.BatchId, "preserve-ledger");
        Assert.True(abandon.PriorLedgerEffectCount >= 1);

        var cleanup = await harness.CleanupAsync(approved.BatchId, BatchStatus.Abandoned);
        Assert.Equal(BatchStatus.Cleaned, cleanup.Status);
    }

    [Fact]
    public async Task Double_abandon_is_rejected()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        _ = await harness.AbandonAsync(preview.BatchId, "first");
        var (ok, error, _) = await harness.TryAbandonAsync(preview.BatchId, "second");
        Assert.False(ok);
        Assert.Equal(AbandonErrors.NotAbandonable, error);
    }

    [Fact]
    public async Task Completed_batch_cannot_be_abandoned()
    {
        var approved = await harness.PrepareApprovedAsync();
        _ = await harness.CommitAsync(approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        var (ok, error, _) = await harness.TryAbandonAsync(approved.BatchId, "too-late");
        Assert.False(ok);
        Assert.Equal(AbandonErrors.NotAbandonable, error);
    }

    [Fact]
    public async Task Cleanup_wrong_expected_status_is_retained()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        _ = await harness.AbandonAsync(preview.BatchId, "x");
        var (ok, error, _) = await harness.TryCleanupAsync(preview.BatchId, BatchStatus.Completed);
        Assert.False(ok);
        Assert.Equal(CleanupErrors.RetainedForRecovery, error);
    }
}
