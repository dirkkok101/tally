using System.Reflection;
using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Xunit;

namespace Tally.Tests.Process;

/// <summary>
/// Every published INGEST ErrorSchema code must map through TallyProcess.ErrorForHandler
/// to its declared exit/category instead of collapsing to host.unexpected. The theory is
/// driven by the operation registry itself, so adding a code to any module's ErrorSchema
/// without mapping it fails here — the drift bd-2lum was filed for cannot recur silently.
/// </summary>
public sealed class IngestErrorProcessTests
{
    [Fact]
    public void Registry_declares_ingest_domain_errors()
    {
        // Guard the guard: an empty enumeration would turn the theory below into a no-op.
        Assert.True(DeclaredIngestErrors.Count() >= 50);
    }

    [Theory]
    [MemberData(nameof(DeclaredIngestErrors))]
    public void Declared_ingest_errors_map_to_their_public_process_contract(string code, int exitCode, string category)
    {
        var mapper = typeof(TallyProcess).GetMethod("ErrorForHandler", BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<ProcessResult>(mapper!.Invoke(null, [code, null]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
    }

    public static TheoryData<string, int, string> DeclaredIngestErrors
    {
        get
        {
            var data = new TheoryData<string, int, string>();
            var declared = OperationRegistry.Create().Descriptors
                .Where(descriptor => descriptor.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
                .SelectMany(descriptor => descriptor.DomainErrors ?? [])
                .DistinctBy(schema => schema.Code, StringComparer.Ordinal);
            foreach (var schema in declared)
            {
                data.Add(schema.Code, schema.ExitCode, schema.Category);
            }

            return data;
        }
    }
}
