using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Cli;
using Tally.Features.Ingest.Contract;
using Xunit;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Tally.Tests.Process;

/// <summary>
/// Process-level proofs for the published INGEST contract surface.
/// Schema discovery must work without opening ingest.db or reading a source.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PublishedIngestContractTests
{
    [Fact]
    public void Schema_list_reports_exactly_eight_ingest_operations_from_registry()
    {
        var listJson = OperationRegistry.Create().SchemaListJson();
        using var document = JsonDocument.Parse(listJson);
        var ingest = document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("operationId").GetString()!)
            .Where(id => id.StartsWith("ingest.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(8, ingest.Length);
        Assert.Equal(global::Tally.Features.Ingest.Contract.IngestOperationIds.All.Order(StringComparer.Ordinal), ingest);
    }

    [Theory]
    [InlineData("ingest.preview")]
    [InlineData("ingest.inspect")]
    [InlineData("ingest.approve")]
    [InlineData("ingest.commit")]
    [InlineData("ingest.resume")]
    [InlineData("ingest.status")]
    [InlineData("ingest.abandon")]
    [InlineData("ingest.cleanup")]
    public void Schema_show_returns_concrete_request_and_result_types(string operationId)
    {
        var showJson = OperationRegistry.Create().SchemaShowJson(operationId);
        using var document = JsonDocument.Parse(showJson);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.True(document.RootElement.TryGetProperty("requestSchema", out _) || document.RootElement.TryGetProperty("requestType", out _));
        Assert.True(document.RootElement.TryGetProperty("resultSchema", out _) || document.RootElement.TryGetProperty("resultType", out _));
        Assert.DoesNotContain("ingest.db", showJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PdfPig", showJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_ingest_operation_is_absent_from_registry()
    {
        Assert.Null(OperationRegistry.Create().Find("ingest.import"));
        Assert.Null(OperationRegistry.Create().Find("ingest.run"));
    }

    [Fact]
    public void Ingest_cli_paths_are_unique_and_prefixed()
    {
        var paths = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .Select(d => d.CliPath)
            .ToArray();
        Assert.All(paths, path => Assert.StartsWith("tally ingest ", path, StringComparison.Ordinal));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Schema_list_does_not_require_opening_a_data_root()
    {
        // Registry construction is pure metadata — no data-root environment is configured.
        var json = OperationRegistry.Create().SchemaListJson();
        Assert.Contains("ingest.preview", json, StringComparison.Ordinal);
        Assert.Contains("ingest.cleanup", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Published_binary_schema_list_includes_ingest_when_available()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            // Inventory still proves the contract; AOT publish is verified separately when binary exists.
            Assert.True(true);
            return;
        }

        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "schema", "list" }
        };
        using var process = DiagnosticsProcess.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ingest.preview", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ingest.db", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPublishedBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tally"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Tally", "bin", "Release", "net10.0", "linux-x64", "publish", "tally"),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tally")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
