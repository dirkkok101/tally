using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget.Position;
using Xunit;

namespace Tally.Tests.Budget.Position;

/// <summary>
/// Nearest-ancestor envelope resolution (DD-BUDGET-CATEGORY-ENVELOPE-RESOLUTION).
/// TC-BUDGET-ENVELOPE-* — pure calculator, no storage or LEDGER I/O.
/// </summary>
public sealed class BudgetEnvelopeResolutionTests
{
    private const string Root = "cat_root";
    private const string Child = "cat_child";
    private const string Grand = "cat_grand";
    private const string Sibling = "cat_sibling";
    private const string OtherRoot = "cat_other_root";

    // ── TC-BUDGET-ENVELOPE-PARENT-ABSORBS-DESCENDANTS ─────────────────────────

    [Fact]
    public void Parent_entry_absorbs_multi_level_descendant_actuals_exactly_once()
    {
        // TC-BUDGET-ENVELOPE-PARENT-ABSORBS-DESCENDANTS
        // Root planned 600_000; actuals on root (100), child (200), grand (300).
        var result = Calculate(
            entries: [Entry(Root, 600_000)],
            members:
            [
                Member(0, "tx-root", Root, 100, [Root]),
                Member(1, "tx-child", Child, 200, [Root, Child]),
                Member(2, "tx-grand", Grand, 300, [Root, Child, Grand])
            ],
            known: [Known(Root), Known(Child), Known(Grand)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, position.Kind);
        Assert.Equal(Root, position.CategoryId);
        Assert.Equal(600, position.ActualMinorUnits);
        Assert.Equal(100, position.DirectActualMinorUnits);
        Assert.Equal(500, position.DescendantActualMinorUnits);
        Assert.Equal([Child, Grand], position.AbsorbedCategoryIds);
        Assert.Equal(600, checked(position.DirectActualMinorUnits + position.DescendantActualMinorUnits));
        Assert.Equal(0, result.Totals.UnbudgetedActualMinorUnits);
        Assert.DoesNotContain(result.CategoryPositions, p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
    }

    // ── TC-BUDGET-ENVELOPE-NEAREST-ENTRY-WINS ─────────────────────────────────

    [Fact]
    public void Nearest_descendant_entry_wins_over_funded_ancestor()
    {
        // TC-BUDGET-ENVELOPE-NEAREST-ENTRY-WINS
        // Parent and child both funded; grand actual resolves to child, not parent.
        var result = Calculate(
            entries: [Entry(Root, 1_000), Entry(Child, 400)],
            members:
            [
                Member(0, "tx-root-direct", Root, 50, [Root]),
                Member(1, "tx-child-direct", Child, 80, [Root, Child]),
                Member(2, "tx-grand", Grand, 120, [Root, Child, Grand]),
                Member(3, "tx-sibling", Sibling, 30, [Root, Sibling])
            ],
            known: [Known(Root), Known(Child), Known(Grand), Known(Sibling)]);

        Assert.Equal(2, result.CategoryPositions.Count);
        var root = result.CategoryPositions.Single(p => p.CategoryId == Root);
        var child = result.CategoryPositions.Single(p => p.CategoryId == Child);

        Assert.Equal(BudgetCategoryPositionKind.Budgeted, root.Kind);
        Assert.Equal(80, root.ActualMinorUnits); // 50 direct + 30 sibling-absorbed
        Assert.Equal(50, root.DirectActualMinorUnits);
        Assert.Equal(30, root.DescendantActualMinorUnits);
        Assert.Equal([Sibling], root.AbsorbedCategoryIds);

        Assert.Equal(BudgetCategoryPositionKind.Budgeted, child.Kind);
        Assert.Equal(200, child.ActualMinorUnits); // 80 direct + 120 grand
        Assert.Equal(80, child.DirectActualMinorUnits);
        Assert.Equal(120, child.DescendantActualMinorUnits);
        Assert.Equal([Grand], child.AbsorbedCategoryIds);

        Assert.Equal(0, result.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(280, result.Totals.ActualMinorUnits);
    }

    // ── TC-BUDGET-ENVELOPE-ZERO-CHILD-BLOCKS-PARENT ───────────────────────────

    [Fact]
    public void Explicit_zero_descendant_entry_blocks_fallback_to_funded_ancestor()
    {
        // TC-BUDGET-ENVELOPE-ZERO-CHILD-BLOCKS-PARENT
        var result = Calculate(
            entries: [Entry(Root, 1_000), Entry(Child, 0)],
            members:
            [
                Member(0, "tx-root", Root, 100, [Root]),
                Member(1, "tx-child", Child, 250, [Root, Child]),
                Member(2, "tx-grand", Grand, 50, [Root, Child, Grand])
            ],
            known: [Known(Root), Known(Child), Known(Grand)]);

        var root = result.CategoryPositions.Single(p => p.CategoryId == Root);
        var child = result.CategoryPositions.Single(p => p.CategoryId == Child);

        Assert.Equal(BudgetCategoryPositionKind.Budgeted, root.Kind);
        Assert.Equal(100, root.ActualMinorUnits);
        Assert.Equal(100, root.DirectActualMinorUnits);
        Assert.Equal(0, root.DescendantActualMinorUnits);
        Assert.Null(root.AbsorbedCategoryIds);
        // Funded ancestor Remaining is unreduced by the zero-child subtree (1000 - 100).
        Assert.Equal(900, root.RemainingMinorUnits);

        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, child.Kind);
        Assert.Equal(300, child.ActualMinorUnits); // 250 + 50
        Assert.Equal(250, child.DirectActualMinorUnits);
        Assert.Equal(50, child.DescendantActualMinorUnits);
        Assert.Equal([Grand], child.AbsorbedCategoryIds);
        Assert.Equal(300, child.OverMinorUnits);

        Assert.Equal(100, result.Totals.BudgetedActualMinorUnits);
        Assert.Equal(300, result.Totals.ZeroBudgetActualMinorUnits);
    }

    // ── TC-BUDGET-ENVELOPE-UNBUDGETED-FALLBACK ────────────────────────────────

    [Fact]
    public void Unbudgeted_when_no_ancestry_element_carries_an_entry()
    {
        // TC-BUDGET-ENVELOPE-UNBUDGETED-FALLBACK
        var result = Calculate(
            entries: [Entry(OtherRoot, 500)],
            members:
            [
                Member(0, "tx-a", Child, 40, [Root, Child]),
                Member(1, "tx-b", Grand, 60, [Root, Child, Grand])
            ],
            known: [Known(OtherRoot), Known(Root), Known(Child), Known(Grand)]);

        var unbudgeted = result.CategoryPositions
            .Where(p => p.Kind == BudgetCategoryPositionKind.Unbudgeted)
            .OrderBy(p => p.CategoryId, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, unbudgeted.Count);

        var childRow = unbudgeted.Single(p => p.CategoryId == Child);
        Assert.Equal(40, childRow.ActualMinorUnits);
        Assert.Equal(40, childRow.DirectActualMinorUnits);
        Assert.Equal(0, childRow.DescendantActualMinorUnits);
        Assert.Null(childRow.AbsorbedCategoryIds);

        var grandRow = unbudgeted.Single(p => p.CategoryId == Grand);
        Assert.Equal(60, grandRow.ActualMinorUnits);
        Assert.Equal(60, grandRow.DirectActualMinorUnits);
        Assert.Equal(0, grandRow.DescendantActualMinorUnits);
        Assert.Null(grandRow.AbsorbedCategoryIds);

        // OtherRoot entry still present with zero actual.
        var funded = result.CategoryPositions.Single(p => p.CategoryId == OtherRoot);
        Assert.Equal(0, funded.ActualMinorUnits);
        Assert.Equal(100, result.Totals.UnbudgetedActualMinorUnits);
    }

    [Fact]
    public void Two_unfunded_siblings_produce_separate_unbudgeted_rows_not_aggregated_ancestor()
    {
        // Success criterion: shared unfunded ancestry → two Unbudgeted rows, no ancestor row.
        var result = Calculate(
            entries: [],
            members:
            [
                Member(0, "tx-c", Child, 10, [Root, Child]),
                Member(1, "tx-s", Sibling, 20, [Root, Sibling])
            ],
            known: [Known(Root), Known(Child), Known(Sibling)]);

        Assert.Equal(2, result.CategoryPositions.Count);
        Assert.All(result.CategoryPositions, p =>
        {
            Assert.Equal(BudgetCategoryPositionKind.Unbudgeted, p.Kind);
            Assert.Equal(0, p.DescendantActualMinorUnits);
            Assert.Null(p.AbsorbedCategoryIds);
            Assert.Equal(p.ActualMinorUnits, p.DirectActualMinorUnits);
        });
        Assert.DoesNotContain(result.CategoryPositions, p => p.CategoryId == Root);
        Assert.Equal(30, result.Totals.UnbudgetedActualMinorUnits);
    }

    // ── TC-BUDGET-ENVELOPE-FLAT-TAXONOMY-UNCHANGED ────────────────────────────

    [Fact]
    public void Flat_taxonomy_matches_exact_identity_v1_result_shape()
    {
        // TC-BUDGET-ENVELOPE-FLAT-TAXONOMY-UNCHANGED
        // All roots with single-element ancestry — every descendant actual is zero.
        var result = Calculate(
            entries: [Entry(Root, 1_000), Entry(OtherRoot, 0)],
            members:
            [
                Member(0, "tx-1", Root, 400, [Root]),
                Member(1, "tx-2", OtherRoot, 50, [OtherRoot]),
                Member(2, "tx-3", Sibling, 25, [Sibling]),
                Member(3, "tx-4", null, 10, [])
            ],
            known: [Known(Root), Known(OtherRoot), Known(Sibling)]);

        var root = result.CategoryPositions.Single(p => p.CategoryId == Root);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, root.Kind);
        Assert.Equal(400, root.ActualMinorUnits);
        Assert.Equal(400, root.DirectActualMinorUnits);
        Assert.Equal(0, root.DescendantActualMinorUnits);
        Assert.Null(root.AbsorbedCategoryIds);

        var zero = result.CategoryPositions.Single(p => p.CategoryId == OtherRoot);
        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, zero.Kind);
        Assert.Equal(50, zero.ActualMinorUnits);
        Assert.Equal(0, zero.DescendantActualMinorUnits);

        var unbudgeted = result.CategoryPositions.Single(p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(Sibling, unbudgeted.CategoryId);
        Assert.Equal(25, unbudgeted.ActualMinorUnits);
        Assert.Equal(0, unbudgeted.DescendantActualMinorUnits);

        Assert.Equal(10, result.UncategorizedPosition.ActualMinorUnits);
        Assert.Equal(0, result.UncategorizedPosition.DescendantActualMinorUnits);
        Assert.Equal(485, result.Totals.ActualMinorUnits);
        Assert.All(result.CategoryPositions, p => Assert.Equal(0, p.DescendantActualMinorUnits));
    }

    // ── TC-BUDGET-ENVELOPE-EXACTLY-ONCE-PARTITION ─────────────────────────────

    [Fact]
    public void Every_row_partition_sums_to_row_actual_and_membership_total()
    {
        // TC-BUDGET-ENVELOPE-EXACTLY-ONCE-PARTITION
        var members = new[]
        {
            Member(0, "tx-0", Root, 11, [Root]),
            Member(1, "tx-1", Child, 22, [Root, Child]),
            Member(2, "tx-2", Grand, 33, [Root, Child, Grand]),
            Member(3, "tx-3", Sibling, 44, [Root, Sibling]),
            Member(4, "tx-4", OtherRoot, 55, [OtherRoot]),
            Member(5, "tx-5", null, 66, [])
        };

        var result = Calculate(
            entries: [Entry(Root, 500), Entry(Child, 0)],
            members: members,
            known: [Known(Root), Known(Child), Known(Grand), Known(Sibling), Known(OtherRoot)]);

        Assert.All(result.CategoryPositions, p =>
            Assert.Equal(
                p.ActualMinorUnits,
                checked(p.DirectActualMinorUnits + p.DescendantActualMinorUnits)));

        Assert.Equal(
            result.UncategorizedPosition.ActualMinorUnits,
            checked(
                result.UncategorizedPosition.DirectActualMinorUnits
                + result.UncategorizedPosition.DescendantActualMinorUnits));

        var rowSum = result.CategoryPositions.Sum(p => p.ActualMinorUnits)
            + result.UncategorizedPosition.ActualMinorUnits;
        Assert.Equal(members.Sum(m => m.BudgetActualMinorUnits), rowSum);
        Assert.Equal(rowSum, result.Totals.ActualMinorUnits);
        Assert.Equal(
            result.Totals.BudgetedActualMinorUnits
            + result.Totals.ZeroBudgetActualMinorUnits
            + result.Totals.UnbudgetedActualMinorUnits
            + result.Totals.UncategorizedActualMinorUnits,
            result.Totals.ActualMinorUnits);
    }

    [Fact]
    public void Property_partition_reconciles_across_generated_envelope_shapes()
    {
        // TC-BUDGET-ENVELOPE-EXACTLY-ONCE-PARTITION — multi-shape smoke.
        // Shapes: parent-only, parent+child entry, zero-child, unbudgeted-only, flat.
        var cases = new (BudgetPlanEntry[] Entries, BudgetActualMember[] Members, CategoryLifecycleEvidence[] Known)[]
        {
            (
                [Entry(Root, 100)],
                [Member(0, "a", Grand, 7, [Root, Child, Grand])],
                [Known(Root), Known(Child), Known(Grand)]
            ),
            (
                [Entry(Root, 100), Entry(Child, 50)],
                [
                    Member(0, "a", Root, 1, [Root]),
                    Member(1, "b", Grand, 2, [Root, Child, Grand])
                ],
                [Known(Root), Known(Child), Known(Grand)]
            ),
            (
                [Entry(Root, 100), Entry(Child, 0)],
                [Member(0, "a", Grand, 9, [Root, Child, Grand])],
                [Known(Root), Known(Child), Known(Grand)]
            ),
            (
                [],
                [
                    Member(0, "a", Child, 3, [Root, Child]),
                    Member(1, "b", Sibling, 4, [Root, Sibling])
                ],
                [Known(Root), Known(Child), Known(Sibling)]
            ),
            (
                [Entry(Root, 10), Entry(OtherRoot, 20)],
                [
                    Member(0, "a", Root, 1, [Root]),
                    Member(1, "b", OtherRoot, 2, [OtherRoot])
                ],
                [Known(Root), Known(OtherRoot)]
            )
        };

        foreach (var (entries, members, known) in cases)
        {
            var result = Calculate(entries, members, known);
            Assert.All(
                result.CategoryPositions,
                p => Assert.Equal(
                    p.ActualMinorUnits,
                    checked(p.DirectActualMinorUnits + p.DescendantActualMinorUnits)));
            var membership = members.Sum(m => m.BudgetActualMinorUnits);
            Assert.Equal(membership, result.Totals.ActualMinorUnits);
        }
    }

    // ── ResolveEnvelope surface + EffectiveCategoryId semantics ──────────────

    [Fact]
    public void ResolveEnvelope_returns_nearest_planned_category_as_effective_id()
    {
        var planned = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [Root] = 1_000,
            [Child] = 0
        };

        var grand = Member(0, "tx", Grand, 1, [Root, Child, Grand]);
        Assert.Equal(Child, BudgetPositionCalculator.ResolveEnvelope(grand, planned));

        var rootOnly = Member(1, "tx2", Root, 1, [Root]);
        Assert.Equal(Root, BudgetPositionCalculator.ResolveEnvelope(rootOnly, planned));

        var unfunded = Member(2, "tx3", Sibling, 1, [OtherRoot, Sibling]);
        Assert.Null(BudgetPositionCalculator.ResolveEnvelope(unfunded, planned));

        var uncategorized = Member(3, "tx4", null, 1, []);
        Assert.Null(BudgetPositionCalculator.ResolveEnvelope(uncategorized, planned));
    }

    [Fact]
    public void Absorbed_category_ids_follow_first_seen_member_ordinal_order()
    {
        // Success criterion: absorbed list names descendants in ascending ordinal order.
        // Grand appears at ordinal 0, Child at ordinal 1 → [Grand, Child] not sorted by id.
        var result = Calculate(
            entries: [Entry(Root, 1_000)],
            members:
            [
                Member(0, "tx-grand", Grand, 10, [Root, Child, Grand]),
                Member(1, "tx-child", Child, 20, [Root, Child]),
                Member(2, "tx-grand-2", Grand, 5, [Root, Child, Grand])
            ],
            known: [Known(Root), Known(Child), Known(Grand)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal([Grand, Child], position.AbsorbedCategoryIds);
        Assert.Equal(0, position.DirectActualMinorUnits);
        Assert.Equal(35, position.DescendantActualMinorUnits);
        Assert.Equal(35, position.ActualMinorUnits);
    }

    [Fact]
    public void Empty_ancestry_falls_back_to_exact_category_identity()
    {
        // Preserves budget-position-v1 exact-identity when AncestryIds is empty.
        var result = Calculate(
            entries: [Entry(Root, 500)],
            members:
            [
                Member(0, "tx-1", Root, 40, []),
                Member(1, "tx-2", Child, 60, [])
            ],
            known: [Known(Root), Known(Child)]);

        var root = result.CategoryPositions.Single(p => p.CategoryId == Root);
        Assert.Equal(40, root.ActualMinorUnits);
        Assert.Equal(40, root.DirectActualMinorUnits);
        Assert.Equal(0, root.DescendantActualMinorUnits);

        var unbudgeted = result.CategoryPositions.Single(p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(Child, unbudgeted.CategoryId);
        Assert.Equal(60, unbudgeted.ActualMinorUnits);
    }

    [Fact]
    public void Negative_descendant_actual_is_absorbed_and_stays_signed()
    {
        var result = Calculate(
            entries: [Entry(Root, 1_000)],
            members:
            [
                Member(0, "tx-refund", Child, -250, [Root, Child]),
                Member(1, "tx-spend", Root, 100, [Root])
            ],
            known: [Known(Root), Known(Child)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(-150, position.ActualMinorUnits);
        Assert.Equal(100, position.DirectActualMinorUnits);
        Assert.Equal(-250, position.DescendantActualMinorUnits);
        Assert.Equal([Child], position.AbsorbedCategoryIds);
        Assert.Equal(1_150, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
