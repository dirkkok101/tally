using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Contract;

/// <summary>
/// TC-CLASSIFY-PUBLISHED-CONTRACT-MATRIX / bd-3g6y —
/// twelve registry-bound CLASSIFY operations, limits seam, discovery without store opens.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyPublishedContractTests
{
    public static TheoryData<string> AllClassifyOperationIds => new(ClassifyOperationIds.All.ToArray());

    [Fact]
    public void Registry_contains_exactly_twelve_unique_classify_operations()
    {
        var classify = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(12, classify.Length);
        Assert.Equal(
            ClassifyOperationIds.All.Order(StringComparer.Ordinal),
            classify.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.Equal(12, classify.Select(d => d.CliPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(AllClassifyOperationIds))]
    public void Every_classify_descriptor_carries_non_null_limits_from_module(string operationId)
    {
        var descriptor = Assert.Single(
            OperationRegistry.Create().Descriptors,
            d => d.OperationId == operationId);
        var schema = descriptor.ToSchema();
        var expected = ClassifyOperationModule.CreateDescriptorTemplates().LimitsFor(operationId);

        Assert.NotNull(descriptor.Limits);
        Assert.Equal(expected, descriptor.Limits);
        Assert.NotNull(schema.Limits);
        Assert.Equal(expected, schema.Limits);
        Assert.DoesNotContain("FoundationOperationHandler", descriptor.HandlerTarget, StringComparison.Ordinal);
        Assert.StartsWith("tally classify ", descriptor.CliPath, StringComparison.Ordinal);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
    }

    [Fact]
    public void Classify_limits_appear_in_schema_json_with_stable_wire_names()
    {
        var show = OperationRegistry.Create().SchemaShowJson(ClassifyOperationIds.Evaluate);
        Assert.Contains("\"limits\"", show, StringComparison.Ordinal);
        Assert.Contains("\"max_transaction_count\"", show, StringComparison.Ordinal);
        Assert.Contains("\"max_rule_count\"", show, StringComparison.Ordinal);
        Assert.Contains("\"max_processing_time_ms\"", show, StringComparison.Ordinal);
        Assert.DoesNotContain("maxTransactionCount", show, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", show, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClassifyStateStore", show, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_operation_schemas_omit_limits_property()
    {
        var account = OperationRegistry.Create().SchemaShowJson("ledger.account.create");
        var version = OperationRegistry.Create().SchemaShowJson("system.version");
        Assert.DoesNotContain("\"limits\"", account, StringComparison.Ordinal);
        Assert.DoesNotContain("\"limits\"", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_templates_match_registry_classify_prefix()
    {
        var bundle = ClassifyOperationBundle.CreateDescriptorTemplates().Descriptors;
        var registry = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(12, bundle.Count);
        Assert.Equal(
            registry.Select(d => d.OperationId).Order(StringComparer.Ordinal),
            bundle.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.All(bundle, d => Assert.NotNull(d.Limits));
    }

    [Fact]
    public void Mutating_classify_operations_require_idempotency_metadata()
    {
        var registry = OperationRegistry.Create();
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var isQuery = operationId is ClassifyOperationIds.OutcomeGet or ClassifyOperationIds.Status;
            Assert.Equal(!isQuery, descriptor.RequiresIdempotencyKey);
            Assert.Equal(isQuery ? "query" : "mutation", descriptor.Kind);
        }
    }

    [Fact]
    public void Forbidden_alias_and_hidden_operations_are_absent()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("classify.invoke", ids);
        Assert.DoesNotContain("classify.run", ids);
        Assert.DoesNotContain("classify.save", ids);
        Assert.DoesNotContain("classify.execute", ids);
        Assert.DoesNotContain("classify.manage", ids);
        Assert.Null(OperationRegistry.Create().Find("classify.delete"));
    }

    [Fact]
    public void Schema_list_and_show_are_byte_stable_and_discovery_safe()
    {
        var first = OperationRegistry.Create().SchemaListJson();
        var second = OperationRegistry.Create().SchemaListJson();
        Assert.Equal(first, second);
        Assert.Contains("classify.evaluate", first, StringComparison.Ordinal);
        Assert.Contains("classify.cleanup", first, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassifyStateStore", first, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", first, StringComparison.OrdinalIgnoreCase);

        foreach (var operationId in ClassifyOperationIds.All)
        {
            var show = OperationRegistry.Create().SchemaShowJson(operationId);
            using var document = JsonDocument.Parse(show);
            Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
            Assert.True(document.RootElement.TryGetProperty("limits", out _));
        }
    }

    [Fact]
    public void Template_handlers_bind_without_data_root_or_foundation_fallback()
    {
        var registry = OperationRegistry.Create();
        var services = LedgerServices.Create();
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var handler = descriptor.HandlerFactory(services, registry);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void Descriptor_limits_match_bd3kex_module_source_exactly()
    {
        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        var registry = OperationRegistry.Create();
        foreach (var published in module.Operations)
        {
            var descriptor = registry.Find(published.Descriptor.OperationId)!;
            Assert.Equal(published.Limits, descriptor.Limits);
            Assert.Equal(published.Limits, descriptor.ToSchema().Limits);
        }
    }

    [Fact]
    public void Complete_classify_public_contract_inventory_is_c12_ordered()
    {
        Assert.Equal(12, ClassifyOperationIds.All.Count);
        Assert.Equal(
            new[]
            {
                "classify.evaluate",
                "classify.outcome.get",
                "classify.apply.preview",
                "classify.apply.run",
                "classify.rule.save",
                "classify.rule.validate",
                "classify.rule.activate",
                "classify.rule.retire",
                "classify.feedback.record",
                "classify.status",
                "classify.abandon",
                "classify.cleanup"
            },
            ClassifyOperationIds.All);
    }
}
