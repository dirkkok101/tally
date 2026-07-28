using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Projection;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Projection;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Tally.Tests.Budget.Contract;

/// <summary>
/// TC-BUDGET-CONTRACT-DISCOVERY-CONTRACT / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY
/// CompleteBudgetPublicContract: six registry-bound BUDGET operations + shared schema discovery
/// + three-op INSIGHTS capability, without opening BudgetStateStore during discovery.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetPublishedContractTests
{
    public static TheoryData<string> AllBudgetOperationIds => new(BudgetOperationIdsAll());

    private static string[] BudgetOperationIdsAll() =>
        global::Tally.Features.Budget.Contract.BudgetOperationIds.All.ToArray();

    [Fact]
    public void Registry_contains_exactly_six_unique_budget_operations()
    {
        var budget = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("budget.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(6, budget.Length);
        Assert.Equal(
            global::Tally.Features.Budget.Contract.BudgetOperationIds.All.Order(StringComparer.Ordinal),
            budget.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.Equal(6, budget.Select(d => d.CliPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(AllBudgetOperationIds))]
    public void Every_budget_operation_has_source_generated_contracts_and_stable_errors(string operationId)
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
        Assert.DoesNotContain("BudgetStateStore", schema.RequestSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("LedgerDb", schema.ResultSchema, StringComparison.Ordinal);
        Assert.StartsWith("tally budget ", descriptor.CliPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_operation_bundle_matches_registry_budget_prefix()
    {
        var bundle = BudgetOperationBundle.CreateDescriptorTemplates().Descriptors;
        var registry = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("budget.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(6, bundle.Count);
        Assert.Equal(
            registry.Select(d => d.OperationId).Order(StringComparer.Ordinal),
            bundle.Select(d => d.OperationId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Insights_capability_publishes_exactly_three_read_operations()
    {
        var capability = BudgetOperationBundle.CreateDescriptorTemplates().ReadCapability;
        Assert.Equal(3, capability.AllowedOperations.Count);
        Assert.Equal(
            BudgetReadCapabilityOperations.All,
            capability.AllowedOperations.Select(o => o.OperationId));
        Assert.All(capability.AllowedOperations, op =>
        {
            Assert.Equal("query", op.Kind);
            Assert.False(op.RequiresIdempotencyKey);
            Assert.Equal(64, op.RequestSchemaFingerprint.Length);
            Assert.Equal(64, op.ResultSchemaFingerprint.Length);
        });
        Assert.DoesNotContain(
            BudgetOperationIds.DraftCreate,
            capability.AllowedOperations.Select(o => o.OperationId));
        Assert.DoesNotContain(
            BudgetOperationIds.RevisionActivate,
            capability.AllowedOperations.Select(o => o.OperationId));
    }

    [Fact]
    public void Mutating_budget_operations_require_idempotency_metadata()
    {
        var registry = OperationRegistry.Create();
        var draft = registry.Find(BudgetOperationIds.DraftCreate)!;
        var activate = registry.Find(BudgetOperationIds.RevisionActivate)!;
        Assert.True(draft.RequiresIdempotencyKey);
        Assert.True(activate.RequiresIdempotencyKey);
        Assert.Contains(draft.ToSchema().Errors, e => e.Code == "LEDGER-IDEMPOTENCY-001" && e.ExitCode == 5);
        Assert.Contains(activate.ToSchema().Errors, e => e.Code == BudgetErrors.IdempotencyRequired);

        foreach (var operationId in new[]
                 {
                     BudgetOperationIds.RevisionGet,
                     BudgetOperationIds.RevisionList,
                     BudgetOperationIds.PositionGet,
                     BudgetOperationIds.InsightsEvidenceGet
                 })
        {
            var descriptor = registry.Find(operationId)!;
            Assert.False(descriptor.RequiresIdempotencyKey);
            Assert.Equal("query", descriptor.Kind);
        }
    }

    [Fact]
    public void Forbidden_alias_and_background_operations_are_absent()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("budget.save", ids);
        Assert.DoesNotContain("budget.execute", ids);
        Assert.DoesNotContain("budget.plan.delete", ids);
        Assert.DoesNotContain("budget.plan.edit", ids);
        Assert.DoesNotContain("budget.rollover", ids);
        Assert.DoesNotContain("budget.status", ids);
        Assert.DoesNotContain("budget.invoke", ids);
        Assert.Null(OperationRegistry.Create().Find("budget.import"));
    }

    [Fact]
    public void Schema_list_and_show_are_byte_stable_and_discovery_safe()
    {
        var first = OperationRegistry.Create().SchemaListJson();
        var second = OperationRegistry.Create().SchemaListJson();
        Assert.Equal(first, second);
        Assert.Contains("budget.plan.draft.create", first, StringComparison.Ordinal);
        Assert.Contains("budget.insights.evidence.get", first, StringComparison.Ordinal);
        Assert.DoesNotContain("BudgetStateStore", first, StringComparison.Ordinal);
        Assert.DoesNotContain("budget.db", first, StringComparison.OrdinalIgnoreCase);

        foreach (var operationId in BudgetOperationIdsAll())
        {
            var show = OperationRegistry.Create().SchemaShowJson(operationId);
            using var document = JsonDocument.Parse(show);
            Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
            Assert.DoesNotContain("SELECT ", show, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BudgetStateStore", show, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Template_handlers_bind_without_data_root()
    {
        var registry = OperationRegistry.Create();
        var services = Tally.Bootstrap.LedgerServices.Create();
        foreach (var operationId in BudgetOperationIdsAll())
        {
            var descriptor = registry.Find(operationId)!;
            var handler = descriptor.HandlerFactory(services, registry);
            Assert.EndsWith("OperationHandler", handler.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void Shared_system_schema_list_and_show_remain_present()
    {
        var registry = OperationRegistry.Create();
        Assert.NotNull(registry.Find("system.schema.list"));
        Assert.NotNull(registry.Find("system.schema.show"));
        Assert.Contains("budget.position.get", registry.SchemaListJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Published_binary_schema_list_includes_budget_when_available()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            // Inventory still proves the contract; AOT publish is verified by verify-budget-contract.sh.
            // xunit 2.9.3 has no dynamic Assert.Skip/[SkippableFact] in this repo; warn loudly
            // instead of silently passing.
            Console.Error.WriteLine(
                "SKIPPED: TALLY_PUBLISHED_BINARY not set; published-binary case runs under the gate scripts.");
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
        Assert.Contains("budget.plan.draft.create", stdout, StringComparison.Ordinal);
        Assert.Contains("budget.insights.evidence.get", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("budget.db", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPublishedBinary()
    {
        var env = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tally"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Tally", "bin", "Release", "net10.0", "linux-x64", "publish", "tally"),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tally")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
