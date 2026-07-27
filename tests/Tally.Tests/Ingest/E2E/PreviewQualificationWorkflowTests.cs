using System.Collections.Immutable;
using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>UC-INGEST-001 black-box preview and qualification gate.</summary>
[SupportedOSPlatform("linux")]
public sealed class PreviewQualificationWorkflowTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void Preview_operation_is_published_with_source_path_only_in_json_body()
    {
        var descriptor = Assert.Single(
            OperationRegistry.Create().Descriptors,
            d => d.OperationId == IngestOperationIds.Preview);
        Assert.Equal("tally ingest preview", descriptor.CliPath);
        Assert.Contains("sourcePath", descriptor.RequestTypeInfo.Properties.Select(p => p.Name), StringComparer.Ordinal);
        Assert.DoesNotContain("--source", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Synthetic_preview_returns_batch_and_manifest()
    {
        var accountId = await harness.CreateAccountAsync();
        var preview = await harness.PreviewSyntheticAsync(accountId);
        Assert.False(string.IsNullOrWhiteSpace(preview.BatchId));
        Assert.False(string.IsNullOrWhiteSpace(preview.ManifestRevisionId));
        Assert.Equal(BatchStatus.Previewed, preview.Status);
    }

    [Fact]
    public async Task Private_fixtures_when_injected_qualify_through_registry()
    {
        var fixtures = harness.TryPrivateFixtures();
        if (fixtures is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var extractor = new PdfStatementTextExtractor();
        var registry = StatementAdapterRegistry.CreateDefault();
        foreach (var fixture in fixtures.Fixtures)
        {
            var extraction = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.Null(extraction.Error);
            var selection = registry.Select(extraction.Evidence!);
            Assert.Equal(AdapterSelectionStatus.ExclusiveMatch, selection.Status);
        }
    }

    [Fact]
    public async Task Unsupported_source_fails_closed_with_stable_code()
    {
        var path = Path.Combine(harness.Root, "bad.pdf");
        await File.WriteAllTextAsync(path, "not-pdf");
        var accountId = await harness.CreateAccountAsync();
        var database = new IngestDatabase(harness.Root, new IngestArtifactProtection());
        var account = new AccountDetail(
            accountId, "i", "d", AccountType.Cheque, AccountClass.Asset,
            "m", "ZAR", AccountStatus.Active, "human:owner", "2026-01-01T00:00:00Z", null, []);
        var handler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new Adapter(new PdfStatementTextExtractor()),
            StatementAdapterRegistry.CreateDefault(),
            new PreviewStateStore(database, new BatchErrorEventStore()),
            harness.Time);
        var result = await handler.HandleAsync(
            new PreviewCommand("1.0", path, accountId, harness.Actor),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.StartsWith("INGEST-", result.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_qualified_adapters_are_registered()
    {
        var registry = StatementAdapterRegistry.CreateDefault();
        Assert.Equal(2, registry.Adapters.Count);
    }

    private sealed class Adapter(PdfStatementTextExtractor inner) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken) =>
            inner.ExtractAsync(source, limits, cancellationToken);
    }
}
