using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Position;
using Tally.Domain.Budget.Position;
using Xunit;

namespace Tally.Tests.Budget.Contract;

/// <summary>
/// Contract-surface proofs for TASK-BUDGET-ENVELOPE-CONTRACTS / FR-BUDGET-POSITION-QUERY
/// (envelope partition + ancestry provenance fields; calculation schema budget-position-v2).
/// </summary>
public sealed class BudgetContractShapeTests
{
    [Fact]
    public void CategoryPosition_carries_direct_descendant_partition_and_absorbed_ids()
    {
        // AC: CategoryPosition exposes DirectActualMinorUnits, DescendantActualMinorUnits, AbsorbedCategoryIds
        var position = new CategoryPosition(
            CategoryId: "cat-a",
            CurrentDisplayName: "A",
            CurrentLifecycle: null,
            Kind: BudgetCategoryPositionKind.Budgeted,
            PlannedMinorUnits: 100,
            ActualMinorUnits: 40,
            RemainingMinorUnits: 60,
            OverMinorUnits: 0,
            DirectActualMinorUnits: 25,
            DescendantActualMinorUnits: 15,
            AbsorbedCategoryIds: ["cat-b"]);

        Assert.Equal(25L, position.DirectActualMinorUnits);
        Assert.Equal(15L, position.DescendantActualMinorUnits);
        Assert.Equal(["cat-b"], position.AbsorbedCategoryIds);
        Assert.Equal(
            position.ActualMinorUnits,
            checked(position.DirectActualMinorUnits + position.DescendantActualMinorUnits));
    }

    [Fact]
    public void BudgetActualMember_carries_ancestry_ids_and_effective_category_id()
    {
        // AC: BudgetActualMember exposes AncestryIds and EffectiveCategoryId
        var member = new BudgetActualMember(
            Ordinal: 1,
            TransactionId: "tx-1",
            EffectiveDate: "2026-07-01",
            CategoryId: "child",
            BudgetActualMinorUnits: -100,
            AncestryIds: ["root", "parent", "child"],
            EffectiveCategoryId: "parent");

        Assert.Equal(["root", "parent", "child"], member.AncestryIds);
        Assert.Equal("parent", member.EffectiveCategoryId);
    }

    [Fact]
    public void Calculation_schema_version_is_budget_position_v2()
    {
        // AC: BudgetPositionCalculator.CalculationSchemaVersion equals budget-position-v2
        Assert.Equal("budget-position-v2", BudgetPositionCalculator.CalculationSchemaVersion);
    }

    [Fact]
    public void Published_position_and_insights_schemas_include_envelope_fields_at_contract_1_0()
    {
        // AC: schema show for budget.position.get and budget.insights.evidence.get exposes new fields;
        // contractVersion remains 1.0 (public contract is not advanced).
        var registry = OperationRegistry.Create();

        var position = Assert.IsType<OperationDescriptor>(registry.Find("budget.position.get"));
        Assert.Equal("1.0", position.MinimumContractVersion);
        Assert.Equal("1.0", position.MaximumContractVersion);
        var positionSchema = JsonDocument.Parse(registry.SchemaShowJson("budget.position.get"))
            .RootElement.GetProperty("resultSchema").GetString()!;
        Assert.Contains("directActualMinorUnits", positionSchema, StringComparison.Ordinal);
        Assert.Contains("descendantActualMinorUnits", positionSchema, StringComparison.Ordinal);
        Assert.Contains("absorbedCategoryIds", positionSchema, StringComparison.Ordinal);

        var insights = Assert.IsType<OperationDescriptor>(registry.Find("budget.insights.evidence.get"));
        Assert.Equal("1.0", insights.MinimumContractVersion);
        Assert.Equal("1.0", insights.MaximumContractVersion);
        var insightsSchema = JsonDocument.Parse(registry.SchemaShowJson("budget.insights.evidence.get"))
            .RootElement.GetProperty("resultSchema").GetString()!;
        // Position partition fields (embedded BudgetPosition) + member provenance.
        Assert.Contains("directActualMinorUnits", insightsSchema, StringComparison.Ordinal);
        Assert.Contains("ancestryIds", insightsSchema, StringComparison.Ordinal);
        Assert.Contains("effectiveCategoryId", insightsSchema, StringComparison.Ordinal);
    }
}
