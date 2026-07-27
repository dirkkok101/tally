using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Cli;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>NFR agent operability — discovery and invocation of the eight ingest operations.</summary>
[SupportedOSPlatform("linux")]
public sealed class AgentContractWorkflowTests
{
    [Fact]
    public void Schema_list_exposes_exactly_eight_ingest_operations()
    {
        var ingest = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(IngestOperationIds.All.Order(StringComparer.Ordinal), ingest);
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
    public void Schema_show_returns_versioned_request_and_result(string operationId)
    {
        var descriptor = OperationRegistry.Create().Find(operationId);
        Assert.NotNull(descriptor);
        var schema = descriptor!.ToSchema();
        Assert.Equal("1.0", schema.MinimumContractVersion);
        Assert.Equal("1.0", schema.MaximumContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(schema.RequestSchema));
        Assert.False(string.IsNullOrWhiteSpace(schema.ResultSchema));
        Assert.False(string.IsNullOrWhiteSpace(schema.Example));
    }

    [Fact]
    public void Every_ingest_operation_has_stable_error_catalog()
    {
        foreach (var operationId in IngestOperationIds.All)
        {
            var schema = OperationRegistry.Create().Find(operationId)!.ToSchema();
            Assert.Contains(schema.Errors, e => e.Code == "contract.incompatible" && e.ExitCode == 7);
            Assert.Contains(schema.Errors, e => e.Code == "validation.invalid_input" && e.ExitCode == 3);
        }
    }

    [Fact]
    public void No_generic_import_or_run_operation_exists()
    {
        var ids = OperationRegistry.Create().Descriptors.Select(d => d.OperationId).ToArray();
        Assert.DoesNotContain(ids, id => id is "ingest.import" or "ingest.run" or "ingest.invoke");
    }

    [Fact]
    public void Schema_list_json_is_byte_stable()
    {
        var first = OperationRegistry.Create().SchemaListJson();
        var second = OperationRegistry.Create().SchemaListJson();
        Assert.Equal(first, second);
        Assert.Contains("ingest.preview", first, StringComparison.Ordinal);
        Assert.Contains("ingest.cleanup", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_paths_are_discoverable_from_operation_ids()
    {
        var registry = OperationRegistry.Create();
        foreach (var operationId in IngestOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.Equal(operationId, registry.FindByArguments(args)!.OperationId);
        }
    }
}
