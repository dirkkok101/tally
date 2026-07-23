using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;
using Xunit;

namespace Tally.Tests.Domain.Ledger;

// Covers TC-LEDGER-DIMENSIONAL-ACTUALS-PROPERTY.
public sealed class ActualsCalculatorTests
{
    [Fact]
    public void Empty_membership_returns_empty_groups_and_exact_zero_totals()
    {
        var result = ActualsCalculator.Calculate([], ActualsGroupKind.None);

        Assert.Empty(result.Items);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.Totals.NetAccountMovement.MinorUnits);
        Assert.Equal(0, result.Totals.ExternalSpend.MinorUnits);
        Assert.Equal(0, result.Totals.BudgetActual.MinorUnits);
    }

    [Fact]
    public void Ordinary_outflow_is_positive_external_spend_and_budget_actual()
    {
        var result = Calculate(Item(-12_345));

        AssertTotals(result.Totals, -12_345, 12_345, 12_345);
    }

    [Fact]
    public void Cash_withdrawal_is_an_ordinary_immediate_spend_without_a_cash_inference()
    {
        var result = Calculate(Item(-20_000, description: "ATM cash withdrawal"));

        AssertTotals(result.Totals, -20_000, 20_000, 20_000);
    }

    [Fact]
    public void Unrelated_inflow_changes_account_movement_but_not_spend()
    {
        var result = Calculate(Item(50_000));

        AssertTotals(result.Totals, 50_000, 0, 0);
    }

    [Fact]
    public void Owned_transfer_principal_changes_movement_but_contributes_zero_spend()
    {
        var result = ActualsCalculator.Calculate(
            [Item(-10_000, relationship: ActualsRelationshipState.TransferOutflow), Item(10_000, relationship: ActualsRelationshipState.TransferInflow)],
            ActualsGroupKind.None);

        AssertTotals(result.Totals, 0, 0, 0);
    }

    [Fact]
    public void Separately_recorded_fee_remains_spend_beside_owned_transfer_principal()
    {
        var result = ActualsCalculator.Calculate(
            [
                Item(-10_000, relationship: ActualsRelationshipState.TransferOutflow),
                Item(10_000, relationship: ActualsRelationshipState.TransferInflow),
                Item(-250, description: "Transfer fee")
            ],
            ActualsGroupKind.None);

        AssertTotals(result.Totals, -250, 250, 250);
    }

    [Fact]
    public void Full_refund_offsets_original_spend_in_its_own_effective_date()
    {
        var original = Item(-8_000, date: "2026-07-01", relationship: ActualsRelationshipState.RefundOriginal);
        var refund = Item(8_000, date: "2026-07-15", relationship: ActualsRelationshipState.RefundCredit);

        var result = ActualsCalculator.Calculate([original, refund], ActualsGroupKind.None);

        AssertTotals(result.Totals, 0, 0, 0);
        Assert.Equal(-8_000, result.Items.Single(item => item.Item.TransactionId == refund.TransactionId).Contribution.ExternalSpend.MinorUnits);
    }

    [Theory]
    [InlineData(TransactionLifecycleStatus.Voided)]
    [InlineData(TransactionLifecycleStatus.Superseded)]
    public void Inactive_history_can_be_returned_but_never_contributes_to_totals(TransactionLifecycleStatus status)
    {
        var result = Calculate(Item(-1_000, lifecycle: status));

        AssertTotals(result.Totals, 0, 0, 0);
        Assert.Single(result.Items);
    }

    [Fact]
    public void Statement_replacement_contributes_once_while_the_superseded_capture_contributes_zero()
    {
        var prior = Item(-1_000, lifecycle: TransactionLifecycleStatus.Superseded);
        var replacement = Item(-1_100);

        var result = ActualsCalculator.Calculate([prior, replacement], ActualsGroupKind.None);

        AssertTotals(result.Totals, -1_100, 1_100, 1_100);
    }

    [Fact]
    public void Duplicate_transaction_membership_is_rejected_before_totals_are_computed()
    {
        var item = Item(-100);

        var error = Assert.Throws<InvalidOperationException>(() => ActualsCalculator.Calculate([item, item], ActualsGroupKind.None));

        Assert.Contains(ActualsCalculator.InvariantError, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_addition_overflow_is_rejected_instead_of_wrapping()
    {
        var first = Item(long.MaxValue);
        var second = Item(1);

        Assert.Throws<OverflowException>(() => ActualsCalculator.Calculate([first, second], ActualsGroupKind.None));
    }

    [Fact]
    public void Direct_category_groups_form_an_exact_partition_with_uncategorized_explicit()
    {
        var category = LedgerId.New().ToString();
        var result = ActualsCalculator.Calculate(
            [Item(-500, categoryId: category), Item(-200)],
            ActualsGroupKind.CategoryDirect);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(500, result.Groups.Single(group => group.CategoryId == category).Totals.BudgetActual.MinorUnits);
        Assert.Equal(200, result.Groups.Single(group => group.CategoryState == TransactionCategoryState.Uncategorized).Totals.BudgetActual.MinorUnits);
        Assert.Equal(result.Totals.BudgetActual.MinorUnits, result.Groups.Sum(group => group.Totals.BudgetActual.MinorUnits));
    }

    [Fact]
    public void Subtree_groups_include_each_item_once_per_current_ancestor()
    {
        var root = LedgerId.New().ToString();
        var child = LedgerId.New().ToString();
        var result = ActualsCalculator.Calculate(
            [Item(-500, categoryId: child, ancestry: [root, child])],
            ActualsGroupKind.CategorySubtree);

        Assert.Equal(500, result.Groups.Single(group => group.CategoryId == root).Totals.BudgetActual.MinorUnits);
        Assert.Equal(500, result.Groups.Single(group => group.CategoryId == child).Totals.BudgetActual.MinorUnits);
        Assert.All(result.Groups, group => Assert.Single(group.TransactionIds));
    }

    [Fact]
    public void Pool_groups_partition_assigned_and_unassigned_actuals()
    {
        var pool = LedgerId.New().ToString();
        var result = ActualsCalculator.Calculate(
            [Item(-700, poolId: pool), Item(-300)],
            ActualsGroupKind.Pool);

        Assert.Equal(700, result.Groups.Single(group => group.PoolId == pool).Totals.BudgetActual.MinorUnits);
        Assert.Equal(300, result.Groups.Single(group => group.PoolState == TransactionPoolState.Unassigned).Totals.BudgetActual.MinorUnits);
        Assert.Equal(result.Totals.BudgetActual.MinorUnits, result.Groups.Sum(group => group.Totals.BudgetActual.MinorUnits));
    }

    [Fact]
    public void Pool_category_matrix_is_an_exact_direct_membership_partition()
    {
        var pool = LedgerId.New().ToString();
        var category = LedgerId.New().ToString();
        var result = ActualsCalculator.Calculate(
            [Item(-900, categoryId: category, poolId: pool), Item(-100)],
            ActualsGroupKind.PoolCategory);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(900, result.Groups.Single(group => group.PoolId == pool && group.CategoryId == category).Totals.BudgetActual.MinorUnits);
        Assert.Equal(100, result.Groups.Single(group => group.PoolState == TransactionPoolState.Unassigned && group.CategoryState == TransactionCategoryState.Uncategorized).Totals.BudgetActual.MinorUnits);
        Assert.Equal(result.Totals.BudgetActual.MinorUnits, result.Groups.Sum(group => group.Totals.BudgetActual.MinorUnits));
    }

    [Fact]
    public void Results_have_stable_effective_date_then_transaction_descending_order()
    {
        var older = Item(-100, date: "2026-07-01");
        var newer = Item(-200, date: "2026-07-02");

        var result = ActualsCalculator.Calculate([older, newer], ActualsGroupKind.None);

        Assert.Equal([newer.TransactionId, older.TransactionId], result.Items.Select(item => item.Item.TransactionId));
    }

    [Fact]
    public void One_hundred_thousand_items_retain_exact_totals_without_precision_loss()
    {
        var accountId = LedgerId.New().ToString();
        var items = Enumerable.Range(0, 100_000)
            .Select(_ => Item(-1) with { AccountId = accountId })
            .ToArray();

        var result = ActualsCalculator.Calculate(items, ActualsGroupKind.None);

        Assert.Equal(100_000, result.Items.Count);
        AssertTotals(result.Totals, -100_000, 100_000, 100_000);
    }

    [Theory]
    [InlineData(ActualsRelationshipState.TransferOutflow, 100)]
    [InlineData(ActualsRelationshipState.TransferInflow, -100)]
    [InlineData(ActualsRelationshipState.RefundOriginal, 100)]
    [InlineData(ActualsRelationshipState.RefundCredit, -100)]
    public void Relationship_role_with_impossible_amount_direction_is_rejected(ActualsRelationshipState relationship, long amount)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Calculate(Item(amount, relationship: relationship)));

        Assert.Contains(ActualsCalculator.InvariantError, error.Message, StringComparison.Ordinal);
    }

    private static ActualsCalculation Calculate(ActualsItem item) => ActualsCalculator.Calculate([item], ActualsGroupKind.None);

    private static ActualsItem Item(
        long amountMinor,
        string date = "2026-07-01",
        string? categoryId = null,
        IReadOnlyList<string>? ancestry = null,
        string? poolId = null,
        ActualsRelationshipState relationship = ActualsRelationshipState.None,
        TransactionLifecycleStatus lifecycle = TransactionLifecycleStatus.Active,
        string description = "Bank transaction")
    {
        Assert.True(EffectiveDate.TryParse(date, out var effectiveDate, out _));
        return new(
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            Money.FromMinorUnits(amountMinor),
            effectiveDate,
            description,
            lifecycle,
            categoryId is null ? TransactionCategoryState.Uncategorized : TransactionCategoryState.Categorized,
            categoryId,
            ancestry ?? (categoryId is null ? [] : [categoryId]),
            poolId is null ? TransactionPoolState.Unassigned : TransactionPoolState.Assigned,
            poolId,
            TransactionKnowledgeState.Unknown,
            null,
            TransactionKnowledgeState.Unknown,
            null,
            [EvidenceKind.AgentCapture],
            TransactionReconciliationState.RecordedUnreconciled,
            relationship);
    }

    private static void AssertTotals(ActualsTotals totals, long net, long spend, long budget)
    {
        Assert.Equal(net, totals.NetAccountMovement.MinorUnits);
        Assert.Equal(spend, totals.ExternalSpend.MinorUnits);
        Assert.Equal(budget, totals.BudgetActual.MinorUnits);
    }
}
