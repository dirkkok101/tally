using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Features.Budget.Contract;
using Xunit;

namespace Tally.Tests.Budget.Contract;

/// <summary>
/// TC-BUDGET-CONTRACT-DISCOVERY-CONTRACT / TC-BUDGET-STRUCTURED-INVOCATION-CONTRACT
/// Contract foundation proofs — no BudgetStateStore or Ledger reads.
/// </summary>
public sealed class BudgetOperationContractTests
{
    [Fact]
    public void Inventory_contains_exactly_six_budget_operations_in_canonical_order()
    {
        var descriptors = Module().Descriptors;
        Assert.Equal(6, descriptors.Count);
        Assert.Equal(BudgetOperationIds.All, descriptors.Select(d => d.OperationId));
    }

    [Fact]
    public void All_cli_paths_are_unique_and_budget_prefixed()
    {
        var paths = Module().Descriptors.Select(d => d.CliPath).ToArray();
        Assert.All(paths, path => Assert.StartsWith("tally budget ", path, StringComparison.Ordinal));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(BudgetOperationIds.DraftCreate, true, "command")]
    [InlineData(BudgetOperationIds.RevisionGet, false, "query")]
    [InlineData(BudgetOperationIds.RevisionList, false, "query")]
    [InlineData(BudgetOperationIds.RevisionActivate, true, "command")]
    [InlineData(BudgetOperationIds.PositionGet, false, "query")]
    [InlineData(BudgetOperationIds.InsightsEvidenceGet, false, "query")]
    public void Mutability_and_idempotency_metadata_match_operation_kind(string operationId, bool requiresIdempotency, string kind)
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == operationId);
        Assert.Equal(requiresIdempotency, descriptor.RequiresIdempotencyKey);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
    }

    [Theory]
    [InlineData(BudgetOperationIds.DraftCreate)]
    [InlineData(BudgetOperationIds.RevisionGet)]
    [InlineData(BudgetOperationIds.RevisionList)]
    [InlineData(BudgetOperationIds.RevisionActivate)]
    [InlineData(BudgetOperationIds.PositionGet)]
    [InlineData(BudgetOperationIds.InsightsEvidenceGet)]
    public void Descriptor_publishes_request_result_types_and_domain_errors(string operationId)
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == operationId);
        Assert.NotNull(descriptor.RequestTypeInfo);
        Assert.NotNull(descriptor.ResultTypeInfo);
        Assert.NotNull(descriptor.DomainErrors);
        Assert.NotEmpty(descriptor.DomainErrors!);
        Assert.All(descriptor.DomainErrors!, error =>
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Code));
            Assert.False(string.IsNullOrWhiteSpace(error.Category));
            Assert.InRange(error.ExitCode, 3, 10);
        });
        var schema = descriptor.ToSchema();
        Assert.Equal(operationId, schema.OperationId);
        Assert.DoesNotContain("BudgetStateStore", schema.RequestSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("LedgerDb", schema.ResultSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", schema.RequestSchema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forbidden_alias_operations_are_absent()
    {
        var ids = Module().Descriptors.Select(d => d.OperationId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("budget.save", ids);
        Assert.DoesNotContain("budget.execute", ids);
        Assert.DoesNotContain("budget.plan.delete", ids);
        Assert.DoesNotContain("budget.plan.edit", ids);
        Assert.DoesNotContain("budget.rollover", ids);
        Assert.DoesNotContain("budget.status", ids);
    }

    [Fact]
    public void Closed_enums_serialize_as_snake_or_camel_canonical_names()
    {
        Assert.Equal("draft", JsonSerializer.Serialize(BudgetRevisionStatus.Draft, BudgetJsonContext.Default.BudgetRevisionStatus).Trim('"'));
        Assert.Equal("active", JsonSerializer.Serialize(BudgetRevisionStatus.Active, BudgetJsonContext.Default.BudgetRevisionStatus).Trim('"'));
        Assert.Equal("superseded", JsonSerializer.Serialize(BudgetRevisionStatus.Superseded, BudgetJsonContext.Default.BudgetRevisionStatus).Trim('"'));
        Assert.Equal("budgeted", JsonSerializer.Serialize(BudgetCategoryPositionKind.Budgeted, BudgetJsonContext.Default.BudgetCategoryPositionKind).Trim('"'));
        Assert.Equal("zero_budget", JsonSerializer.Serialize(BudgetCategoryPositionKind.ZeroBudget, BudgetJsonContext.Default.BudgetCategoryPositionKind).Trim('"'));
        Assert.Equal("bound_revision", JsonSerializer.Serialize(BudgetInsightPlanState.BoundRevision, BudgetJsonContext.Default.BudgetInsightPlanState).Trim('"'));
    }

    [Fact]
    public void Unknown_request_fields_are_rejected_by_source_generated_json()
    {
        const string json = """{"contractVersion":"1.0","revisionId":"01TEST","extra":"nope"}""";
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(json, BudgetJsonContext.Default.GetBudgetPlanRevisionInput));
    }

    [Fact]
    public void Draft_create_rejects_unknown_entry_fields()
    {
        const string json = """
            {"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"c1","plannedMinorUnits":100,"name":"leak"}],"reason":"plan"}
            """;
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(json, BudgetJsonContext.Default.CreateDraftBudgetPlanInput));
    }

    [Fact]
    public void Money_wire_uses_integer_minor_units_not_decimal_strings()
    {
        var entry = new BudgetPlanEntryInput("cat-1", 12_345);
        var json = JsonSerializer.Serialize(entry, BudgetJsonContext.Default.BudgetPlanEntryInput);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("plannedMinorUnits").ValueKind);
        Assert.Equal(12345, document.RootElement.GetProperty("plannedMinorUnits").GetInt64());
        Assert.DoesNotContain("123.45", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Pure_mapper_sums_and_orders_without_io()
    {
        var entries = new[]
        {
            new BudgetPlanEntryInput("b", 200),
            new BudgetPlanEntryInput("a", 100)
        };
        Assert.Equal(300, BudgetContractMapper.SumPlannedMinorUnits(entries));
        var details = new[]
        {
            new BudgetPlanEntryDetail("b", 200, null, null),
            new BudgetPlanEntryDetail("a", 100, null, null)
        };
        Assert.Equal(["a", "b"], BudgetContractMapper.OrderEntries(details).Select(e => e.CategoryId));
    }

    [Fact]
    public void Pure_mapper_rejects_negative_planned_amounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BudgetContractMapper.SumPlannedMinorUnits([new BudgetPlanEntryInput("a", -1)]));
    }

    [Fact]
    public void Supported_contract_version_is_exactly_one_dot_zero()
    {
        Assert.True(BudgetContractMapper.IsSupportedContractVersion("1.0"));
        Assert.False(BudgetContractMapper.IsSupportedContractVersion("2.0"));
        Assert.False(BudgetContractMapper.IsSupportedContractVersion(null));
        Assert.False(BudgetContractMapper.IsSupportedContractVersion(""));
    }

    [Fact]
    public async Task Stub_handler_requires_actor_without_opening_storage()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.RevisionGet)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new GetBudgetPlanRevisionInput("1.0", "01REVISION"),
            BudgetJsonContext.Default.GetBudgetPlanRevisionInput);
        var result = await handler.HandleAsync(new OperationRequest(input, null, null), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Mutating_stub_requires_idempotency_key()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.DraftCreate)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new CreateDraftBudgetPlanInput("1.0", new BudgetPeriodInput(2026, 7, "ZAR"), [], "reason"),
            BudgetJsonContext.Default.CreateDraftBudgetPlanInput);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_contract_version_fails_before_storage()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.PositionGet)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new GetBudgetPositionInput("9.9", new BudgetPeriodInput(2026, 7, "ZAR"), null),
            BudgetJsonContext.Default.GetBudgetPositionInput);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.Equal(BudgetErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_json_object_fails_as_invalid_input()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.RevisionList)
            .HandlerFactory(null!, null!);
        using var document = JsonDocument.Parse("[]");
        var result = await handler.HandleAsync(
            new OperationRequest(document.RootElement.Clone(), new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Valid_read_contract_does_not_return_success_without_store_implementation()
    {
        // Foundation stub fails closed with NotFound — proves no silent empty success / no fabricated financial payload.
        var handler = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.RevisionGet)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new GetBudgetPlanRevisionInput("1.0", "01HZXEXAMPLE00000000000000"),
            BudgetJsonContext.Default.GetBudgetPlanRevisionInput);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.NotFound, result.ErrorCode);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public void Descriptor_templates_are_constructible_without_services()
    {
        var templates = BudgetOperationModule.CreateDescriptorTemplates().Descriptors;
        Assert.Equal(6, templates.Count);
        Assert.All(templates, d => Assert.StartsWith("budget.", d.OperationId, StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_error_codes_are_stable_budget_prefixed()
    {
        var codes = Module().Descriptors.SelectMany(d => d.DomainErrors!).Select(e => e.Code).Distinct().ToArray();
        Assert.All(codes, code => Assert.StartsWith("BUDGET-", code, StringComparison.Ordinal));
        Assert.Contains(BudgetErrors.InvalidInput, codes);
        Assert.Contains(BudgetErrors.ResourceLimit, codes);
        Assert.Contains(BudgetErrors.SourceStateChanged, codes);
    }

    [Fact]
    public void Insights_evidence_descriptor_is_read_only_query()
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == BudgetOperationIds.InsightsEvidenceGet);
        Assert.Equal("query", descriptor.Kind);
        Assert.False(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(GetBudgetInsightEvidenceInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(GetBudgetInsightEvidenceResult), descriptor.ResultTypeInfo.Type);
    }

    [Fact]
    public void Activate_and_draft_are_the_only_mutating_operations()
    {
        var mutating = Module().Descriptors.Where(d => d.RequiresIdempotencyKey).Select(d => d.OperationId).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [BudgetOperationIds.DraftCreate, BudgetOperationIds.RevisionActivate];
        Assert.Equal(expected.Order(StringComparer.Ordinal), mutating);
    }

    [Fact]
    public void Example_invocations_use_stdin_input_boundary()
    {
        Assert.All(Module().Descriptors, d => Assert.Contains("--input -", d.Example, StringComparison.Ordinal));
    }

    private static BudgetOperationModule Module() => new();
}
