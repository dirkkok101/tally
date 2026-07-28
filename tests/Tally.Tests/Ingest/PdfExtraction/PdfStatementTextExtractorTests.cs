using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.PdfExtraction;

[Collection(ProcessMemoryCollection.Name)]
public sealed class PdfStatementTextExtractorTests
{
    private static readonly PdfExtractionLimits TestLimits = new(
        MaxBytes: 1_000_000,
        MaxPages: 4,
        MaxGlyphs: 10_000,
        MaxDuration: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Extracts_ordered_page_glyph_text_and_coordinate_evidence()
    {
        var source = ImmutableArray.Create(CreatePdf("ordered evidence"));

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, CancellationToken.None);

        Assert.Null(result.Error);
        var evidence = Assert.IsType<PdfDocumentEvidence>(result.Evidence);
        var page = Assert.Single(evidence.Pages);
        Assert.NotEmpty(page.OrderedGlyphs);
        Assert.Equal(Enumerable.Range(0, page.OrderedGlyphs.Count), page.OrderedGlyphs.Select(glyph => glyph.ContentOrder));
        Assert.All(page.OrderedGlyphs, glyph =>
        {
            Assert.NotEmpty(glyph.Value);
            Assert.True(double.IsFinite(glyph.Left));
            Assert.True(double.IsFinite(glyph.Bottom));
            Assert.True(double.IsFinite(glyph.Right));
            Assert.True(double.IsFinite(glyph.Top));
            Assert.True(double.IsFinite(glyph.BaselineY));
        });
        Assert.NotEmpty(page.ManagedLines);
        Assert.All(page.ManagedLines, line =>
        {
            Assert.True(double.IsFinite(line.Left));
            Assert.True(double.IsFinite(line.Bottom));
            Assert.True(double.IsFinite(line.Right));
            Assert.True(double.IsFinite(line.Top));
        });
    }

    [Fact]
    public async Task Repeated_extraction_is_deterministic()
    {
        var source = ImmutableArray.Create(CreatePdf("repeatable"));
        var extractor = new PdfStatementTextExtractor();

        var first = await extractor.ExtractAsync(source, TestLimits, CancellationToken.None);
        var second = await extractor.ExtractAsync(source, TestLimits, CancellationToken.None);

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(EvidenceDigest(first.Evidence!), EvidenceDigest(second.Evidence!));
    }

    [Fact]
    public async Task Source_fingerprint_is_the_sha256_of_the_caller_bytes()
    {
        var source = ImmutableArray.Create(CreatePdf("fingerprint"));

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(source.AsSpan())), result.Evidence!.SourceFingerprint);
    }

    [Fact]
    public async Task Does_not_modify_the_caller_owned_source()
    {
        var source = ImmutableArray.Create(CreatePdf("unchanged"));
        var before = source.ToArray();

        _ = await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, CancellationToken.None);

        Assert.Equal(before, source);
    }

    [Fact]
    public async Task Rejects_the_byte_bound_before_parsing()
    {
        var source = ImmutableArray.Create(CreatePdf("too large"));
        var limits = TestLimits with { MaxBytes = source.Length - 1 };

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, limits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-RESOURCE-BYTES", IngestErrorCategory.Resource);
    }

    [Fact]
    public async Task Rejects_the_page_bound_before_page_extraction()
    {
        var source = ImmutableArray.Create(CreatePdf("two pages", pageCount: 2));
        var limits = TestLimits with { MaxPages = 1 };

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, limits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-RESOURCE-PAGES", IngestErrorCategory.Resource);
    }

    [Fact]
    public async Task Rejects_the_glyph_bound_during_extraction()
    {
        var source = ImmutableArray.Create(CreatePdf("more than one glyph"));
        var limits = TestLimits with { MaxGlyphs = 1 };

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, limits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-RESOURCE-GLYPHS", IngestErrorCategory.Resource);
    }

    [Fact]
    public async Task Rejects_an_elapsed_time_bound_before_parsing()
    {
        var source = ImmutableArray.Create(CreatePdf("time bound"));
        var limits = TestLimits with { MaxDuration = TimeSpan.Zero };

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, limits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-RESOURCE-TIME", IngestErrorCategory.Resource);
    }

    [Fact]
    public async Task Propagates_cancellation()
    {
        var source = ImmutableArray.Create(CreatePdf("cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, cancellation.Token));
    }

    [Fact]
    public async Task Malformed_input_returns_a_safe_pre_mutation_error()
    {
        var source = ImmutableArray.Create("not a pdf"u8.ToArray());

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-MALFORMED", IngestErrorCategory.UnsafeSource);
    }

    [Fact]
    public async Task Scan_only_content_returns_a_safe_unsupported_error()
    {
        var source = ImmutableArray.Create(CreatePdf(text: null));

        var result = await new PdfStatementTextExtractor().ExtractAsync(source, TestLimits, CancellationToken.None);

        AssertStableError(result, "INGEST-PDF-UNSUPPORTED-SCAN", IngestErrorCategory.Unsupported);
    }

    [Fact]
    public void Public_surface_does_not_expose_pdfpig_types()
    {
        var publicTypes = typeof(PdfStatementTextExtractor).Assembly.GetTypes()
            .Where(type => type.IsPublic && type.Namespace == "Tally.Infrastructure.Ingest.Pdf")
            .ToArray();

        Assert.NotEmpty(publicTypes);
        Assert.DoesNotContain(publicTypes.SelectMany(PublicSurfaceTypes), type =>
            type.Namespace?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extractor_source_contains_no_external_process_network_or_disk_output_path()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Tally", "Infrastructure", "Ingest", "Pdf", "PdfStatementTextExtractor.cs"));

        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebRequest", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorized_private_fixture_set_is_deterministic_and_bounded_when_injected()
    {
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        Assert.Equal(3, fixtureSet.Fixtures.Count);
        Assert.Equal(2, fixtureSet.Fixtures.Select(fixture => fixture.VariantId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(fixtureSet.Fixtures, fixture => fixture.PermissionEncrypted);

        var extractor = new PdfStatementTextExtractor();
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var peakBefore = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
            var timer = Stopwatch.StartNew();
            var first = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            var second = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            timer.Stop();
            var peakGrowth = Math.Max(0, System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 - peakBefore);

            Assert.Null(first.Error);
            Assert.Null(second.Error);
            var firstEvidence = Assert.IsType<PdfDocumentEvidence>(first.Evidence);
            var secondEvidence = Assert.IsType<PdfDocumentEvidence>(second.Evidence);
            Assert.NotEmpty(firstEvidence.Pages.SelectMany(page => page.OrderedGlyphs));
            Assert.Equal(EvidenceDigest(firstEvidence), EvidenceDigest(secondEvidence));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Private extraction exceeded its two-run time bound.");
            Assert.True(peakGrowth < 256L * 1024 * 1024, "Private extraction exceeded its memory-growth bound.");
        }
    }

    private static void AssertStableError(PdfExtractionResult result, string code, IngestErrorCategory category)
    {
        Assert.Null(result.Evidence);
        var error = Assert.IsType<IngestError>(result.Error);
        Assert.Equal(code, error.Code);
        Assert.Equal(category, error.Category);
        Assert.Equal(MutationPossibility.None, error.MutationPossibility);
        Assert.Equal(IngestRetryAction.CorrectSource, error.RetryAction);
        Assert.Null(error.BatchId);
        Assert.Null(error.CandidateId);
        Assert.Null(error.DurableState);
    }

    private static byte[] EvidenceDigest(PdfDocumentEvidence evidence)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, evidence.SourceFingerprint);
        Append(hash, evidence.ByteLength.ToString(CultureInfo.InvariantCulture));
        foreach (var page in evidence.Pages)
        {
            Append(hash, page.PageNumber.ToString(CultureInfo.InvariantCulture));
            Append(hash, page.Width.ToString("R", CultureInfo.InvariantCulture));
            Append(hash, page.Height.ToString("R", CultureInfo.InvariantCulture));
            foreach (var glyph in page.OrderedGlyphs)
            {
                Append(hash, glyph.Value);
                Append(hash, glyph.Left.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, glyph.Bottom.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, glyph.Right.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, glyph.Top.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, glyph.ContentOrder.ToString(CultureInfo.InvariantCulture));
                Append(hash, glyph.BaselineY.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, glyph.TextSequence.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var line in page.ManagedLines)
            {
                Append(hash, line.BlockOrder.ToString(CultureInfo.InvariantCulture));
                Append(hash, line.LineOrder.ToString(CultureInfo.InvariantCulture));
                Append(hash, line.Text);
                Append(hash, line.Left.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, line.Bottom.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, line.Right.ToString("R", CultureInfo.InvariantCulture));
                Append(hash, line.Top.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static IEnumerable<Type> PublicSurfaceTypes(Type type) =>
        type.GetConstructors().SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(type.GetProperties().Select(property => property.PropertyType))
            .Concat(type.GetMethods().Where(method => !method.IsSpecialName).Select(method => method.ReturnType))
            .Concat(type.GetMethods().Where(method => !method.IsSpecialName).SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tally.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static byte[] CreatePdf(string? text, int pageCount = 1)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageCount).Select(index => $"{3 + index} 0 R"))}] /Count {pageCount} >>"
        };

        var fontObject = 3 + pageCount;
        var firstContentObject = fontObject + 1;
        for (var index = 0; index < pageCount; index++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {firstContentObject + index} 0 R >>");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var index = 0; index < pageCount; index++)
        {
            var content = text is null ? string.Empty : $"BT /F1 12 Tf 72 100 Td ({EscapePdfString(text)}) Tj ET";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string EscapePdfString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);
}
