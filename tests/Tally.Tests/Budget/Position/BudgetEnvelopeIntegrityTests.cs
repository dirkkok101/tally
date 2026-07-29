using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget.Position;
using Xunit;

namespace Tally.Tests.Budget.Position;

/// <summary>
/// Envelope integrity: ancestry validation, overflow, refund sign, archived ancestors
/// (DD-BUDGET-CATEGORY-ENVELOPE-RESOLUTION / TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY /
/// TC-BUDGET-ENVELOPE-OVERFLOW-INTEGRITY / TC-BUDGET-ENVELOPE-REFUND-SIGN-PRESERVED /
/// TC-BUDGET-ENVELOPE-ARCHIVED-ANCESTOR-CAPTURES).
/// </summary>
public sealed class BudgetEnvelopeIntegrityTests
{
    private const string Root = "cat_root";
    private const string Child = "cat_child";
    private const string Grand = "cat_grand";

    // ── TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY ────────────────────────────────

    [Fact]
    public void Categorized_member_with_empty_ancestry_is_integrity_failure()
    {
        // TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 100)],
                members: [Member(0, "tx-1", Root, 10, [])],
                known: [Known(Root)]));

        AssertIntegrity(ex);
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(BudgetPositionCalculator.TryGetIntegrityReason(ex));
        // Amount-free: the member amount must not appear in the diagnostic.
        Assert.DoesNotContain("10", BudgetPositionCalculator.TryGetIntegrityReason(ex)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Categorized_member_ancestry_not_ending_with_assigned_category_is_integrity_failure()
    {
        // TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 100)],
                members: [Member(0, "tx-1", Child, 10, [Root])],
                known: [Known(Root), Known(Child)]));

        AssertIntegrity(ex);
        Assert.Contains("end", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(BudgetPositionCalculator.TryGetIntegrityReason(ex));
    }

    [Fact]
    public void Categorized_member_with_repeated_ancestry_identifier_is_integrity_failure()
    {
        // TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 100)],
                members: [Member(0, "tx-1", Child, 10, [Root, Root, Child])],
                known: [Known(Root), Known(Child)]));

        AssertIntegrity(ex);
        Assert.Contains("repeated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(BudgetPositionCalculator.TryGetIntegrityReason(ex));
    }

    [Fact]
    public void Categorized_member_with_unknown_ancestry_element_is_integrity_failure()
    {
        // TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 100)],
                members: [Member(0, "tx-1", Child, 10, [Root, "cat_missing", Child])],
                known: [Known(Root), Known(Child)]));

        AssertIntegrity(ex);
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(BudgetPositionCalculator.TryGetIntegrityReason(ex));
    }

    [Fact]
    public void Uncategorized_member_with_non_empty_ancestry_is_integrity_failure()
    {
        // TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 100)],
                members: [Member(0, "tx-1", categoryId: null, amount: 10, ancestry: [Root])],
                known: [Known(Root)]));

        AssertIntegrity(ex);
        Assert.Contains("Uncategorized", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(BudgetPositionCalculator.TryGetIntegrityReason(ex));
    }

    // ── TC-BUDGET-ENVELOPE-OVERFLOW-INTEGRITY ─────────────────────────────────

    [Fact]
    public void Descendant_absorbed_sum_overflow_is_integrity_failure_with_no_amount_in_message()
    {
        // TC-BUDGET-ENVELOPE-OVERFLOW-INTEGRITY
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(Root, 1)],
                members:
                [
                    Member(0, "tx-1", Child, long.MaxValue, [Root, Child]),
                    Member(1, "tx-2", Grand, 1, [Root, Child, Grand])
                ],
                known: [Known(Root), Known(Child), Known(Grand)]));

        AssertIntegrity(ex);
        Assert.Contains("overflow", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Amount-free diagnostics: no monetary magnitude (e.g. long.MaxValue) in message or token.
        Assert.DoesNotContain(long.MaxValue.ToString(), ex.Message, StringComparison.Ordinal);
        var reason = BudgetPositionCalculator.TryGetIntegrityReason(ex);
        Assert.NotNull(reason);
        Assert.DoesNotContain(long.MaxValue.ToString(), reason, StringComparison.Ordinal);
        Assert.DoesNotContain("9223372036854775807", reason, StringComparison.Ordinal);
    }

    // ── TC-BUDGET-ENVELOPE-REFUND-SIGN-PRESERVED ──────────────────────────────

    [Fact]
    public void Refund_heavy_descendant_actuals_stay_signed_and_budgeted()
    {
        // TC-BUDGET-ENVELOPE-REFUND-SIGN-PRESERVED
        var result = Calculate(
            entries: [Entry(Root, 1_000)],
            members:
            [
                Member(0, "tx-spend", Child, 100, [Root, Child]),
                Member(1, "tx-refund", Grand, -350, [Root, Child, Grand])
            ],
            known: [Known(Root), Known(Child), Known(Grand)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, position.Kind);
        Assert.Equal(-250, position.ActualMinorUnits);
        Assert.Equal(0, position.DirectActualMinorUnits);
        Assert.Equal(-250, position.DescendantActualMinorUnits);
        Assert.Equal([Child, Grand], position.AbsorbedCategoryIds);
        Assert.Equal(1_250, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
        Assert.Equal(-250, result.Totals.ActualMinorUnits);
        Assert.Equal(-250, result.Totals.BudgetedActualMinorUnits);
    }

    // ── TC-BUDGET-ENVELOPE-ARCHIVED-ANCESTOR-CAPTURES ─────────────────────────

    [Fact]
    public void Archived_ancestor_entry_still_absorbs_active_descendant_actuals()
    {
        // TC-BUDGET-ENVELOPE-ARCHIVED-ANCESTOR-CAPTURES
        var result = Calculate(
            entries: [Entry(Root, 500)],
            members:
            [
                Member(0, "tx-child", Child, 80, [Root, Child]),
                Member(1, "tx-grand", Grand, 40, [Root, Child, Grand])
            ],
            known:
            [
                Known(Root, lifecycle: CategoryLifecycleStatus.Archived),
                Known(Child),
                Known(Grand)
            ]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, position.Kind);
        Assert.Equal(Root, position.CategoryId);
        Assert.Equal(CategoryLifecycleStatus.Archived, position.CurrentLifecycle);
        Assert.Equal(120, position.ActualMinorUnits);
        Assert.Equal(0, position.DirectActualMinorUnits);
        Assert.Equal(120, position.DescendantActualMinorUnits);
        Assert.Equal([Child, Grand], position.AbsorbedCategoryIds);
        Assert.Equal(0, result.Totals.UnbudgetedActualMinorUnits);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertIntegrity(InvalidOperationException ex)
    {
        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Equal(BudgetErrors.Integrity, BudgetPositionCalculator.IntegrityErrorCode);
    }

    private static BudgetPositionCalculation Calculate(
        IReadOnlyList<BudgetPlanEntry> entries,
        IReadOnlyList<BudgetActualMember> members,
        IReadOnlyList<CategoryLifecycleEvidence> known) =>
        BudgetPositionCalculator.Calculate(entries, members, known);

    private static BudgetPlanEntry Entry(string categoryId, long planned) =>
        new(categoryId, planned);

    private static BudgetActualMember Member(
        int ordinal,
        string transactionId,
        string? categoryId,
        long amount,
        IReadOnlyList<string> ancestry) =>
        new(ordinal, transactionId, "2026-07-15", categoryId, amount, AncestryIds: ancestry, EffectiveCategoryId: null);

    private static CategoryLifecycleEvidence Known(
        string categoryId,
        string? displayName = null,
        CategoryLifecycleStatus lifecycle = CategoryLifecycleStatus.Active) =>
        new(categoryId, displayName ?? categoryId, lifecycle, "1.0");
}
