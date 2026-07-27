using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Xunit;

namespace Tally.Tests.Ingest.Review;

// TC-INGEST-MANIFEST-REVIEW-CONTRACT / FR-INGEST-MANIFEST-REVIEW
[SupportedOSPlatform("linux")]
public sealed class ReviewWorkflowTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-review-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("owner", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 14, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Inspect_requires_batch_and_revision()
    {
        var result = await CreateInspectHandler().HandleAsync(new InspectQuery("", ""), CancellationToken.None);
        Assert.Equal(InspectErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Inspect_unknown_revision_fails_closed()
    {
        var result = await CreateInspectHandler().HandleAsync(new InspectQuery("missing", "missing"), CancellationToken.None);
        Assert.Equal(InspectErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Inspect_returns_persisted_manifest_view()
    {
        var preview = await PreviewAsync();
        var inspect = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);

        Assert.True(inspect.IsSuccess, inspect.ErrorCode);
        Assert.Equal(preview.BatchId, inspect.Value!.BatchId);
        Assert.Equal(preview.ManifestRevisionId, inspect.Value.ManifestRevisionId);
        Assert.Equal("acc-1", inspect.Value.SelectedAccountId);
        Assert.NotEmpty(inspect.Value.RecordOutcomes);
        Assert.NotEmpty(inspect.Value.Candidates);
        Assert.False(inspect.Value.ApprovalState.Approved);
        Assert.False(string.IsNullOrWhiteSpace(inspect.Value.CanonicalDigest));
    }

    [Fact]
    public async Task Inspect_is_deterministic_across_repeated_reads()
    {
        var preview = await PreviewAsync();
        var handler = CreateInspectHandler();
        var first = await handler.HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        var second = await handler.HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);

        Assert.Equal(first.Value!.CanonicalDigest, second.Value!.CanonicalDigest);
        Assert.Equal(first.Value.RecordOutcomes.Count, second.Value.RecordOutcomes.Count);
        Assert.Equal(first.Value.Candidates.Select(c => c.CandidateId), second.Value.Candidates.Select(c => c.CandidateId));
    }

    [Fact]
    public async Task Approve_requires_actor_and_digest()
    {
        var preview = await PreviewAsync();
        var result = await CreateApproveHandler().HandleAsync(
            new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, "", actor),
            CancellationToken.None);
        Assert.Equal(ApproveErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Approve_rejects_digest_mismatch()
    {
        var preview = await PreviewAsync();
        var result = await CreateApproveHandler().HandleAsync(
            new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, new string('0', 64), actor),
            CancellationToken.None);
        Assert.Equal(ApproveErrors.DigestMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task Approve_records_active_approval_for_committable_revision()
    {
        var preview = await PreviewAsync();
        var inspect = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);
        var approve = await CreateApproveHandler().HandleAsync(
            new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, inspect.Value!.CanonicalDigest, actor),
            CancellationToken.None);

        Assert.True(approve.IsSuccess, approve.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(approve.Value!.ApprovalId));
        Assert.Equal(preview.BatchId, approve.Value.BatchId);

        var after = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);
        Assert.True(after.Value!.ApprovalState.Approved);
        Assert.Equal(approve.Value.ApprovalId, after.Value.ApprovalState.ApprovalId);
    }

    [Fact]
    public async Task Reapprove_deactivates_prior_approval()
    {
        var preview = await PreviewAsync();
        var inspect = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);
        var first = await CreateApproveHandler().HandleAsync(
            new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, inspect.Value!.CanonicalDigest, actor),
            CancellationToken.None);
        var second = await CreateApproveHandler().HandleAsync(
            new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, inspect.Value.CanonicalDigest, actor),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.ApprovalId, second.Value!.ApprovalId);
        var after = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);
        Assert.Equal(second.Value.ApprovalId, after.Value!.ApprovalState.ApprovalId);
    }

    [Fact]
    public async Task Review_module_handles_inspect_and_approve_operations()
    {
        var preview = await PreviewAsync();
        var inspectHandler = CreateInspectHandler();
        var approveHandler = CreateApproveHandler();
        var module = new ReviewOperationModule(inspectHandler, approveHandler);

        var inspectInput = JsonSerializer.SerializeToElement(
            new InspectManifestInput(preview.BatchId, preview.ManifestRevisionId!),
            IngestJsonContext.Default.InspectManifestInput);
        var inspect = await module.HandleAsync(
            IngestOperationIds.Inspect,
            new OperationRequest(inspectInput, actor, null),
            CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.ErrorCode);

        var digest = inspect.Value!.GetProperty("canonicalDigest").GetString()!;
        var approveInput = JsonSerializer.SerializeToElement(
            new ApproveManifestInput(preview.BatchId, preview.ManifestRevisionId!, digest, actor),
            IngestJsonContext.Default.ApproveManifestInput);
        var approve = await module.HandleAsync(
            IngestOperationIds.Approve,
            new OperationRequest(approveInput, actor, null),
            CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
    }

    [Fact]
    public async Task Inspect_does_not_mutate_store()
    {
        var preview = await PreviewAsync();
        var before = await CountAsync("SELECT COUNT(*) FROM manifest_approval;");
        _ = await CreateInspectHandler().HandleAsync(
            new InspectQuery(preview.BatchId, preview.ManifestRevisionId!),
            CancellationToken.None);
        var after = await CountAsync("SELECT COUNT(*) FROM manifest_approval;");
        Assert.Equal(before, after);
    }

    private async Task<PreviewImportResult> PreviewAsync()
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf("layout-a"));
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var store = new PreviewStateStore(database, new BatchErrorEventStore());
        var account = new AccountDetail(
            "acc-1", "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "actor", "2026-01-01T00:00:00Z", null, []);
        var handler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new StubPdfExtractor(LayoutAEvidence()),
            StatementAdapterRegistry.CreateDefault(),
            store,
            time);
        var result = await handler.HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!;
    }

    private InspectHandler CreateInspectHandler() =>
        new(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())));

    private ApproveHandler CreateApproveHandler() =>
        new(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())), time);

    private async Task<int> CountAsync(string sql)
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static PdfDocumentEvidence LayoutAEvidence(string fingerprint = "synthetic-review")
    {
        string[] lines =
        [
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"
        ];
        var glyphs = new List<PdfGlyphEvidence>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var left = 20d;
            var bottom = 700d - (lineIndex * 20d);
            foreach (var character in string.Concat(lines[lineIndex], " "))
            {
                glyphs.Add(new PdfGlyphEvidence(
                    character.ToString(), left, bottom, left + 5d, bottom + 10d, glyphs.Count, bottom, glyphs.Count));
                left += 5d;
            }
        }

        return new PdfDocumentEvidence(fingerprint, 1, [new PdfPageEvidence(1, 612, 792, glyphs, [])]);
    }

    private static byte[] CreatePdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 100 Td ({text}) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private sealed class StubPdfExtractor(PdfDocumentEvidence evidence) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken)
        {
            var fp = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
            return ValueTask.FromResult(new PdfExtractionResult(evidence with { SourceFingerprint = fp }, null));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
