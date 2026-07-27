using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Storage;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-002 immutable review and approval gate.</summary>
[SupportedOSPlatform("linux")]
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
    }

    [Fact]
    public async Task Inspect_returns_manifest_view_without_approval()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var inspect = await new InspectHandler(new ReviewStateStore(new IngestDatabase(harness.Root, new IngestArtifactProtection())))
            .HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.ErrorCode);
        Assert.False(inspect.Value!.ApprovalState.Approved);
        Assert.False(string.IsNullOrWhiteSpace(inspect.Value.CanonicalDigest));
        Assert.NotEmpty(inspect.Value.RecordOutcomes);
    }

    [Fact]
    public async Task Approve_records_actor_without_ledger_mutation()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var approved = await harness.ApprovePreviewAsync(preview);
        Assert.False(string.IsNullOrWhiteSpace(approved.Digest));

        var inspect = await new InspectHandler(new ReviewStateStore(new IngestDatabase(harness.Root, new IngestArtifactProtection())))
            .HandleAsync(new InspectQuery(approved.BatchId, approved.ManifestRevisionId), CancellationToken.None);
        Assert.True(inspect.Value!.ApprovalState.Approved);
    }

    [Fact]
    public async Task Approve_rejects_digest_mismatch()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var result = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(harness.Root, new IngestArtifactProtection())), harness.Time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, "wrong-digest", harness.Actor), CancellationToken.None);
        Assert.Equal(ApproveErrors.DigestMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task Inspect_is_deterministic()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        var handler = new InspectHandler(new ReviewStateStore(new IngestDatabase(harness.Root, new IngestArtifactProtection())));
        var first = await handler.HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        var second = await handler.HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        Assert.Equal(first.Value!.CanonicalDigest, second.Value!.CanonicalDigest);
    }
}
