using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Common;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Contract;

/// <summary>
/// TC-CLASSIFY-PUBLISHED-CONTRACT-MATRIX / bd-rly1 —
/// seventeen registry-bound CLASSIFY operations, 105 global inventory,
/// twelve frozen 0.3.3 fingerprints, discovery without store opens.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyPublishedContractTests
{
    public static TheoryData<string> AllClassifyOperationIds => new(ClassifyOperationIds.All.ToArray());

    [Fact]
    public void Registry_contains_exactly_one_hundred_five_operations_and_seventeen_classify()
    {
        var registry = OperationRegistry.Create().Descriptors;
        Assert.Equal(105, registry.Count);
        Assert.Equal(105, registry.Select(d => d.OperationId).Distinct(StringComparer.Ordinal).Count());

        var classify = registry
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(17, classify.Length);
        Assert.Equal(
            ClassifyOperationIds.All.Order(StringComparer.Ordinal),
            classify.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.Equal(17, classify.Select(d => d.CliPath).Distinct(StringComparer.Ordinal).Count());
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

        Assert.Equal(17, bundle.Count);
        Assert.Equal(
            registry.Select(d => d.OperationId).Order(StringComparer.Ordinal),
            bundle.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.All(bundle, d => Assert.NotNull(d.Limits));
    }

    [Fact]
    public void Mutating_classify_operations_require_idempotency_metadata()
    {
        var registry = OperationRegistry.Create();
        var queryIds = new HashSet<string>(StringComparer.Ordinal)
        {
            ClassifyOperationIds.OutcomeGet,
            ClassifyOperationIds.Status,
            ClassifyOperationIds.OutcomeList,
            ClassifyOperationIds.RuleList,
            ClassifyOperationIds.RuleSetActiveGet,
            ClassifyOperationIds.UnresolvedReport
        };
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var isQuery = queryIds.Contains(operationId);
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
        Assert.Contains("classify.outcome.list", first, StringComparison.Ordinal);
        Assert.Contains("classify.rule.list", first, StringComparison.Ordinal);
        Assert.Contains("classify.rule-set.active.get", first, StringComparison.Ordinal);
        Assert.Contains("classify.corpus.build", first, StringComparison.Ordinal);
        Assert.Contains("classify.unresolved.report", first, StringComparison.Ordinal);
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
    public void Descriptor_limits_match_module_source_exactly()
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
    public void Complete_classify_public_contract_inventory_is_seventeen_ordered()
    {
        Assert.Equal(17, ClassifyOperationIds.All.Count);
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
                "classify.cleanup",
                "classify.outcome.list",
                "classify.rule.list",
                "classify.rule-set.active.get",
                "classify.corpus.build",
                "classify.unresolved.report"
            },
            ClassifyOperationIds.All);
    }

    [Theory]
    [MemberData(nameof(ReleasedOperationFingerprints))]
    public void Released_c12_descriptor_fingerprints_remain_frozen(
        string operationId,
        bool requiresIdempotency,
        string kind,
        string fingerprint)
    {
        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        var descriptor = module.Descriptors.Single(d => d.OperationId == operationId);
        Assert.Equal(requiresIdempotency, descriptor.RequiresIdempotencyKey);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Equal(fingerprint, ComputeDescriptorFingerprint(descriptor));

        // Registry-bound descriptor must preserve the same released fingerprint surface.
        var registryDescriptor = OperationRegistry.Create().Find(operationId)!;
        Assert.Equal(requiresIdempotency, registryDescriptor.RequiresIdempotencyKey);
        Assert.Equal(kind, registryDescriptor.Kind);
        Assert.Equal(fingerprint, ComputeDescriptorFingerprint(registryDescriptor));
    }

    [Fact]
    public void Five_additive_schemas_are_present_without_replacing_released_type_infos()
    {
        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.Evaluate).RequestTypeInfo);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyOutcomeListRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.OutcomeList).RequestTypeInfo);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyRuleListRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.RuleList).RequestTypeInfo);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.RuleSetActiveGet).RequestTypeInfo);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyCorpusBuildRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.CorpusBuild).RequestTypeInfo);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest,
            module.Descriptors.Single(d => d.OperationId == ClassifyOperationIds.UnresolvedReport).RequestTypeInfo);
        Assert.NotSame(
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
    }

    /// <summary>
    /// Golden SHA-256 fingerprints for the twelve 0.3.3 descriptors
    /// (must remain identical after additive ergonomics registration).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (bool Idempotency, string Kind, string Fingerprint)> FrozenC12 =
        new Dictionary<string, (bool, string, string)>(StringComparer.Ordinal)
        {
            ["classify.evaluate"] = (true, "mutation", "bf871fb01329a59bc467468b7bb822ebc4fbe6758678b6d0e3c5b9c7891a0105"),
            ["classify.outcome.get"] = (false, "query", "5745abfccd7962a153d9da7c880efc29c8df70a01f2a590fee870e1235c50ecb"),
            ["classify.apply.preview"] = (true, "mutation", "02172a00efa755391179db79043e9575b8e1f6e42975d5254334f2f2c314a28e"),
            ["classify.apply.run"] = (true, "mutation", "1b959f38d885005b1c61df1b9895a71b07c5e8db9f82d4b3b4c719beaa47a224"),
            ["classify.rule.save"] = (true, "mutation", "5e126c9de1ab6936b9329b08cf6aa80630712e65b681c3e6ee0e70174bfc7b74"),
            ["classify.rule.validate"] = (true, "mutation", "e549f94b5238aa7e506a4b33efc1ab39ee1457fa43a865ee98a4b0e203f1e7cc"),
            ["classify.rule.activate"] = (true, "mutation", "c28507462e2d527ef0547f794000a90d5f498287deabf0beca6a029d66d523fc"),
            ["classify.rule.retire"] = (true, "mutation", "b95326c04ad1d001c9604eb6166b3025e49f7977bde309a9b65a0106e2336a8c"),
            ["classify.feedback.record"] = (true, "mutation", "e7f3d210482a87dfeba9fdeb3a23490d2979259a22fff9ba4f2540c29e2c1e0c"),
            ["classify.status"] = (false, "query", "3f4bd3631585df4b8887e35a05d7e734036d76da1ec4f9c11ab7cb4f5cdea387"),
            ["classify.abandon"] = (true, "mutation", "2ff6d927ea5e70277213642c42c8f4096d6f5cfa5dc09714a53b80bff673410b"),
            ["classify.cleanup"] = (true, "mutation", "af64117a200118433666eda11c41fbca4a0daf018924137ba07c4c7edf8f925e")
        };

    public static TheoryData<string, bool, string, string> ReleasedOperationFingerprints()
    {
        var data = new TheoryData<string, bool, string, string>();
        foreach (var (operationId, frozen) in FrozenC12)
        {
            data.Add(operationId, frozen.Idempotency, frozen.Kind, frozen.Fingerprint);
        }

        return data;
    }

    private static string ComputeDescriptorFingerprint(OperationDescriptor descriptor)
    {
        var requestProps = PropertyNames(descriptor.RequestTypeInfo!).Order(StringComparer.Ordinal);
        var resultProps = PropertyNames(descriptor.ResultTypeInfo!).Order(StringComparer.Ordinal);
        var errors = (descriptor.DomainErrors ?? [])
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .Select(e => $"{e.Code}:{e.Category}:{e.ExitCode}");
        var payload = string.Join(
            "\n",
            [
                descriptor.OperationId,
                descriptor.Kind,
                descriptor.RequiresIdempotencyKey ? "idempotent" : "no-idempotency",
                descriptor.MinimumContractVersion,
                descriptor.MaximumContractVersion,
                "REQ:" + string.Join(",", requestProps),
                "RES:" + string.Join(",", resultProps),
                "ERR:" + string.Join(",", errors)
            ]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static HashSet<string> PropertyNames(JsonTypeInfo typeInfo) =>
        typeInfo.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
}
