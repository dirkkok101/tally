using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Review;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-002 immutable review and approval gate (published surface).</summary>
[SupportedOSPlatform("linux")]
// TC-INGEST-MANIFEST-REVIEW-CONTRACT / FR-INGEST-MANIFEST-REVIEW
public sealed class ReviewApprovalWorkflowTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void Inspect_and_approve_are_published()
    {
        var registry = OperationRegistry.Create();
        Assert.NotNull(registry.Find(IngestOperationIds.Inspect));
        Assert.NotNull(registry.Find(IngestOperationIds.Approve));
        Assert.Equal("tally ingest inspect", registry.Find(IngestOperationIds.Inspect)!.CliPath);
        Assert.Equal("tally ingest approve", registry.Find(IngestOperationIds.Approve)!.CliPath);
    }

    [Fact]
    public async Task Inspect_returns_manifest_view_without_approval()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        Assert.False(inspect.ApprovalState.Approved);
        Assert.False(string.IsNullOrWhiteSpace(inspect.CanonicalDigest));
        Assert.NotEmpty(inspect.RecordOutcomes);
    }

    [Fact]
    public async Task Approve_records_actor_without_ledger_mutation()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        Assert.False(string.IsNullOrWhiteSpace(approved.Digest));

        var inspect = await harness.InspectAsync(approved.BatchId, approved.ManifestRevisionId);
        Assert.True(inspect.ApprovalState.Approved);
    }

    [Fact]
    public async Task Approve_rejects_digest_mismatch()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var (ok, error, _) = await harness.TryApproveAsync(
            preview.BatchId, preview.ManifestRevisionId!, "wrong-digest");
        Assert.False(ok);
        Assert.Equal(ApproveErrors.DigestMismatch, error);
    }

    [Fact]
    public async Task Inspect_is_deterministic()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var first = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var second = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        Assert.Equal(first.CanonicalDigest, second.CanonicalDigest);
    }

    [Fact]
    public async Task Approve_rejects_unknown_revision()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await harness.TryApproveAsync(
            preview.BatchId, "missing-revision", inspect.CanonicalDigest);
        Assert.False(ok);
        Assert.Equal(ApproveErrors.NotFound, error);
    }

    [Fact]
    public async Task Approve_rejects_wrong_batch_for_revision()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await harness.TryApproveAsync(
            "not-a-batch", preview.ManifestRevisionId!, inspect.CanonicalDigest);
        Assert.False(ok);
        Assert.Equal(ApproveErrors.NotFound, error);
    }

    [Fact]
    public async Task Inspect_rejects_absent_revision()
    {
        var (ok, error, _) = await harness.TryInspectAsync("missing-batch", "missing-revision");
        Assert.False(ok);
        Assert.Equal(InspectErrors.NotFound, error);
    }

    [Fact]
    public async Task Double_approve_succeeds_as_stable_re_approval()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        // Re-approve of the identical frozen digest deactivates the prior approval and records a
        // fresh active one — deterministic success, never a second live approval.
        var (ok, error, value) = await harness.TryApproveAsync(
            approved.BatchId, approved.ManifestRevisionId, approved.Digest);
        Assert.True(ok, error);
        Assert.Equal(approved.BatchId, value!.BatchId);
        Assert.Equal(approved.ManifestRevisionId, value.ManifestRevisionId);
        var inspect = await harness.InspectAsync(approved.BatchId, approved.ManifestRevisionId);
        Assert.True(inspect.ApprovalState.Approved);
        Assert.Equal(approved.Digest, inspect.CanonicalDigest);
    }

    [Fact]
    public async Task Commit_rejected_when_revision_absent_after_preview()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await harness.TryCommitAsync(
            preview.BatchId, "wrong-revision", inspect.CanonicalDigest);
        Assert.False(ok);
        Assert.Equal(CommitErrors.NotFound, error);
    }

    [Fact]
    public async Task Unapproved_manifest_cannot_commit()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await harness.TryCommitAsync(
            preview.BatchId, preview.ManifestRevisionId!, inspect.CanonicalDigest);
        Assert.False(ok);
        Assert.Equal(CommitErrors.NotApproved, error);
    }
}
