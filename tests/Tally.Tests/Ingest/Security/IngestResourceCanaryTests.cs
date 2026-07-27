using System.Collections.Immutable;
using System.Text;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Security;

/// <summary>
/// NFR-INGEST-BOUNDED-PARSING / TC-INGEST-PDF-EXTRACTION-AOT resource and malformed canaries.
/// </summary>
public sealed class IngestResourceCanaryTests
{
    [Fact]
    public async Task Malformed_bytes_fail_closed_before_adapter_selection()
    {
        var result = await new PdfStatementTextExtractor().ExtractAsync(
            ImmutableArray.Create("not-a-pdf"u8.ToArray()),
            PdfExtractionLimits.PrivateFixture,
            CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.Equal("INGEST-PDF-MALFORMED", result.Error!.Code);
        Assert.DoesNotContain("not-a-pdf", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_page_limit_fails_with_resource_code()
    {
        var source = ImmutableArray.Create(CreatePdf("page", pageCount: 3));
        var result = await new PdfStatementTextExtractor().ExtractAsync(
            source,
            PdfExtractionLimits.PrivateFixture with { MaxPages = 1 },
            CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.Equal("INGEST-PDF-RESOURCE-PAGES", result.Error!.Code);
    }

    [Fact]
    public async Task Over_byte_limit_fails_with_resource_code()
    {
        var source = ImmutableArray.Create(CreatePdf("large", pageCount: 1));
        var result = await new PdfStatementTextExtractor().ExtractAsync(
            source,
            PdfExtractionLimits.PrivateFixture with { MaxBytes = 32 },
            CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.StartsWith("INGEST-PDF-RESOURCE", result.Error!.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_source_fails_closed()
    {
        var result = await new PdfStatementTextExtractor().ExtractAsync(
            ImmutableArray<byte>.Empty,
            PdfExtractionLimits.PrivateFixture,
            CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.NotNull(result.Error);
        Assert.StartsWith("INGEST-", result.Error!.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_mutation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfStatementTextExtractor().ExtractAsync(
                ImmutableArray.Create(CreatePdf("cancel")),
                PdfExtractionLimits.PrivateFixture,
                cts.Token).AsTask());
    }

    [Fact]
    public async Task Private_fixtures_when_injected_do_not_exceed_resource_bounds()
    {
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var extractor = new PdfStatementTextExtractor();
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var result = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.True(result.Error is null || result.Error.Code.StartsWith("INGEST-", StringComparison.Ordinal));
            // Never surface fixture path/locator in error metadata.
            if (result.Error is not null)
            {
                Assert.DoesNotContain("docs/statements", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Extraction_limits_advertise_positive_bounds()
    {
        var limits = PdfExtractionLimits.PrivateFixture;
        Assert.True(limits.MaxBytes > 0);
        Assert.True(limits.MaxPages > 0);
        Assert.True(limits.MaxDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Password_like_encrypted_marker_fails_without_payload_echo()
    {
        // Minimal non-PDF with "Encrypt" token — extractor must not leak content.
        var payload = "%PDF-1.4\n/Encrypt something-secret\n"u8.ToArray();
        var result = await new PdfStatementTextExtractor().ExtractAsync(
            ImmutableArray.Create(payload),
            PdfExtractionLimits.PrivateFixture,
            CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("something-secret", result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("something-secret", result.Error.Code, StringComparison.Ordinal);
    }

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
            var content = text is null ? string.Empty : $"BT /F1 12 Tf 72 100 Td ({text}) Tj ET";
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
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
