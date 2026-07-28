using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Adapters;

[Collection(ProcessMemoryCollection.Name)]
public sealed class AdapterResourceBoundTests
{
    [Fact]
    public async Task One_over_page_limit_fails_before_adapter_selection()
    {
        // NFR-INGEST-BOUNDED-PARSING
        var source = ImmutableArray.Create(CreatePdf("page", pageCount: 2));
        var limits = PdfExtractionLimits.PrivateFixture with { MaxPages = 1 };
        var result = await new PdfStatementTextExtractor().ExtractAsync(source, limits, CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.Equal("INGEST-PDF-RESOURCE-PAGES", result.Error!.Code);
    }

    [Fact]
    public async Task Malformed_source_fails_before_adapter_selection()
    {
        var source = ImmutableArray.Create("not-a-pdf"u8.ToArray());
        var result = await new PdfStatementTextExtractor().ExtractAsync(source, PdfExtractionLimits.PrivateFixture, CancellationToken.None);

        Assert.Null(result.Evidence);
        Assert.Equal("INGEST-PDF-MALFORMED", result.Error!.Code);
    }

    [Fact]
    public async Task Private_fixture_extraction_stays_within_advertised_bounds_when_injected()
    {
        // FR-INGEST-VARIANT-QUALIFICATION resource AC
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var extractor = new PdfStatementTextExtractor();
        var registry = StatementAdapterRegistry.CreateDefault();
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var peakBefore = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
            var timer = Stopwatch.StartNew();
            var extraction = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.Null(extraction.Error);
            var selection = registry.Select(extraction.Evidence!);
            Assert.Equal(AdapterSelectionStatus.ExclusiveMatch, selection.Status);
            timer.Stop();
            var peakGrowth = Math.Max(0, System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 - peakBefore);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Private fixture extraction exceeded the 5-second bound.");
            Assert.True(peakGrowth < 256L * 1024 * 1024, "Private fixture extraction exceeded the 256 MiB peak growth bound.");
        }
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
