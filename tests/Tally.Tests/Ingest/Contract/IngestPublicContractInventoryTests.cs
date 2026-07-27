using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest.Contract;

[SupportedOSPlatform("linux")]
public sealed class IngestPublicContractInventoryTests
{
    public static TheoryData<string> IngestOperationIds => new(
        global::Tally.Features.Ingest.Contract.IngestOperationIds.All.ToArray());

    [Fact]
    public void Registry_contains_exactly_eight_unique_ingest_operations()
    {
        var ingest = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(8, ingest.Length);
        Assert.Equal(
            global::Tally.Features.Ingest.Contract.IngestOperationIds.All.Order(StringComparer.Ordinal),
            ingest.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.Equal(8, ingest.Select(d => d.CliPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(IngestOperationIds))]
    public void Every_ingest_operation_has_source_generated_contracts_and_stable_errors(string operationId)
    {
        var descriptor = Assert.Single(OperationRegistry.Create().Descriptors, d => d.OperationId == operationId);
        var schema = descriptor.ToSchema();

        Assert.NotEqual(typeof(JsonElement), descriptor.RequestTypeInfo.Type);
        Assert.NotEqual(typeof(JsonElement), descriptor.ResultTypeInfo.Type);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Example));
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Contains(schema.Errors, error => error.Code == "contract.incompatible" && error.ExitCode == 7);
        Assert.DoesNotContain("FoundationOperationHandler", descriptor.HandlerTarget, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingest_operation_bundle_matches_registry_ingest_prefix()
    {
        var bundle = IngestOperationBundle.CreateDescriptorTemplates().Descriptors;
        var registry = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(8, bundle.Count);
        Assert.Equal(
            registry.Select(d => d.OperationId).Order(StringComparer.Ordinal),
            bundle.Select(d => d.OperationId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Snapshot_lists_exactly_the_eight_ingest_operations()
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "Tally.Tests", "Cli", "Snapshots", "ingest-operations-v1.json");
        var expected = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;
        var actual = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Ledger_prefix_inventory_is_unchanged_at_sixty_eight()
    {
        var ledger = OperationRegistry.Create().Descriptors
            .Count(d => d.OperationId.StartsWith("ledger.", StringComparison.Ordinal));
        Assert.Equal(68, ledger);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
