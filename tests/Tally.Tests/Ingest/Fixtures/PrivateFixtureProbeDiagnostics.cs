using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Infrastructure.Ingest.Pdf;
using Xunit;

namespace Tally.Tests.Ingest.Fixtures;

[SupportedOSPlatform("linux")]
public sealed class PrivateFixtureProbeDiagnostics
{
    public const string Env = "TALLY_INGEST_PRIVATE_PROBE_DIAG";

    [Fact]
    public async Task Report_probe_stages_structurally()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Env), "1", StringComparison.Ordinal))
            return;

        var root = FindRepositoryRoot();
        var inv = Path.Combine(root, "docs", "statements", ".fixture-inventory.json");
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(inv));
        var extractor = new PdfStatementTextExtractor();
        var a = new PdfTextLayoutAStatementAdapter();
        var b = new PdfTextLayoutBStatementAdapter();
        var ok = 0;
        foreach (var item in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var role = item.GetProperty("accountRole").GetString()!;
            var sha = item.GetProperty("sourceSha256").GetString()!;
            var path = item.GetProperty("sourcePath").GetString()!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, path));
            var extraction = await extractor.ExtractAsync(System.Collections.Immutable.ImmutableArray.Create(bytes), PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            var evidence = extraction.Evidence!;
            var pa = a.Probe(evidence).Outcome;
            var pb = b.Probe(evidence).Outcome;
            var matched = pa == VariantProbeOutcome.ExactMatch || pb == VariantProbeOutcome.ExactMatch;
            if (matched) ok++;
            else Console.WriteLine($"FAIL role={role} sha={sha[..12]} A={pa} B={pb}");
        }
        Console.WriteLine($"SUMMARY ok={ok}/27");
        Assert.Equal(27, ok);
    }

    private static string FindRepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Tally.slnx"))) return d.FullName;
        throw new InvalidOperationException("root");
    }
}
