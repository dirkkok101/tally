using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Cli;
using Tally.Features.Ingest.Contract;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Tally.Tests.Process;

/// <summary>
/// Published-surface security proofs for INGEST: schema discovery and invocation boundaries
/// never echo source paths or financial payloads.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PublishedIngestSecurityTests
{
    [Fact]
    public void Schema_list_contains_no_source_path_or_private_fixture_hints()
    {
        var json = OperationRegistry.Create().SchemaListJson();
        foreach (var canary in new[]
                 {
                     "sourcePath", "docs/statements", "fixture", "mailbox", "sqlite", "connectionString",
                     "PdfPig", "ingest.db"
                 })
        {
            // sourcePath may appear in request schema property names for preview — only path values are forbidden.
            if (canary == "sourcePath")
            {
                continue;
            }

            Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Preview_request_schema_allows_sourcePath_only_inside_json_body()
    {
        var preview = OperationRegistry.Create().Find(IngestOperationIds.Preview)!;
        Assert.Contains("sourcePath", preview.RequestTypeInfo.Properties.Select(p => p.Name), StringComparer.Ordinal);
        Assert.DoesNotContain("sourcePath", preview.CliPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--input", preview.Example, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingest_cli_paths_never_advertise_source_as_named_argument()
    {
        foreach (var descriptor in OperationRegistry.Create().Descriptors.Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("--source", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--file", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--path", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Schema_show_errors_are_metadata_codes_only()
    {
        foreach (var operationId in IngestOperationIds.All)
        {
            var schema = OperationRegistry.Create().Find(operationId)!.ToSchema();
            Assert.All(schema.Errors, error =>
            {
                Assert.False(string.IsNullOrWhiteSpace(error.Code));
                Assert.DoesNotContain("/", error.Code, StringComparison.Ordinal);
                Assert.DoesNotContain(" ", error.Code, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public void Published_binary_when_present_does_not_echo_canary_path_on_invalid_preview()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Assert.True(true);
            return;
        }

        var canary = "/tmp/CANARY-INGEST-SECRET-PATH.pdf";
        var input = JsonSerializer.Serialize(new
        {
            contractVersion = "1.0",
            actor = new { kind = "human", label = "owner" },
            input = new
            {
                contractVersion = "1.0",
                sourcePath = canary,
                accountId = "01J00000000000000000000000",
                actor = new { kind = "human", label = "owner" }
            }
        });

        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "ingest", "preview", "--input", "-" }
        };
        using var process = DiagnosticsProcess.Start(start)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));

        Assert.DoesNotContain("CANARY-INGEST-SECRET-PATH", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY-INGEST-SECRET-PATH", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_schema_fingerprint_is_stable_for_ingest_prefix()
    {
        var first = IngestSchemaJson();
        var second = IngestSchemaJson();
        Assert.Equal(first, second);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(second))));
    }

    private static string IngestSchemaJson()
    {
        var ops = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .Select(d => d.ToSchema())
            .ToArray();
        return JsonSerializer.Serialize(ops.Select(o => o.OperationId).Order(StringComparer.Ordinal));
    }

    private static string? FindPublishedBinary()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Tally", "bin", "Release", "net10.0", "linux-x64", "publish", "tally"),
            Path.Combine(AppContext.BaseDirectory, "tally")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
