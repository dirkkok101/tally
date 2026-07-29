using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget.Position;
using Xunit;

namespace Tally.Tests.Budget.Position;

/// <summary>
/// TC-BUDGET-EXACT-FINANCIAL-INTEGRITY / FR-BUDGET-POSITION-QUERY / DD-BUDGET-EXACT-POSITION-CALCULATION
/// Pure exhaustive bucketing — no storage, no Ledger I/O.
/// </summary>
public sealed class BudgetPositionCalculatorTests
{
    private const string CatA = "cat_a_budgeted";
    private const string CatB = "cat_b_zero";
    private const string CatC = "cat_c_unbudgeted";
    private const string CatD = "cat_d_extra";

    // ── Budgeted / ZeroBudget / Unbudgeted / Uncategorized buckets ───────────

    [Fact]
    public void Positive_planned_entry_with_actuals_is_Budgeted_once()
    {
        var result = Calculate(
            entries: [Entry(CatA, 1_000)],
            members: [Member(0, "tx-1", CatA, 400)],
            known: [Known(CatA)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, position.Kind);
        Assert.Equal(CatA, position.CategoryId);
        Assert.Equal(1_000, position.PlannedMinorUnits);
        Assert.Equal(400, position.ActualMinorUnits);
        Assert.Equal(600, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
        Assert.Equal(400, result.Totals.BudgetedActualMinorUnits);
        Assert.Equal(0, result.UncategorizedPosition.ActualMinorUnits);
    }

    [Fact]
    public void Explicit_zero_entry_with_actuals_is_ZeroBudget_once()
    {
        var result = Calculate(
            entries: [Entry(CatB, 0)],
            members: [Member(0, "tx-1", CatB, 250)],
            known: [Known(CatB)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, position.Kind);
        Assert.Equal(0, position.PlannedMinorUnits);
        Assert.Equal(250, position.ActualMinorUnits);
        Assert.Equal(0, position.RemainingMinorUnits);
        Assert.Equal(250, position.OverMinorUnits);
        Assert.Equal(250, result.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(0, result.Totals.BudgetedActualMinorUnits);
    }

    [Fact]
    public void Explicit_zero_entry_without_actuals_is_preserved_as_ZeroBudget_row()
    {
        var result = Calculate(
            entries: [Entry(CatB, 0)],
            members: [],
            known: [Known(CatB)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, position.Kind);
        Assert.Equal(0, position.PlannedMinorUnits);
        Assert.Equal(0, position.ActualMinorUnits);
        Assert.Equal(0, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
    }

    [Fact]
    public void Known_category_omitted_from_plan_is_Unbudgeted_with_null_planned_remaining_over()
    {
        var result = Calculate(
            entries: [Entry(CatA, 500)],
            members: [Member(0, "tx-1", CatC, 75)],
            known: [Known(CatA), Known(CatC, "Unbudgeted")]);

        Assert.Equal(2, result.CategoryPositions.Count);
        var unbudgeted = result.CategoryPositions.Single(p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(CatC, unbudgeted.CategoryId);
        Assert.Equal("Unbudgeted", unbudgeted.CurrentDisplayName);
        Assert.Null(unbudgeted.PlannedMinorUnits);
        Assert.Null(unbudgeted.RemainingMinorUnits);
        Assert.Null(unbudgeted.OverMinorUnits);
        Assert.Equal(75, unbudgeted.ActualMinorUnits);
        Assert.Equal(75, result.Totals.UnbudgetedActualMinorUnits);
    }

    [Fact]
    public void Null_category_actual_is_Uncategorized_with_no_invented_id_or_planned()
    {
        var result = Calculate(
            entries: [Entry(CatA, 100)],
            members: [Member(0, "tx-1", categoryId: null, amount: 33)],
            known: [Known(CatA)]);

        Assert.DoesNotContain(result.CategoryPositions, p => p.Kind == BudgetCategoryPositionKind.Uncategorized);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, result.UncategorizedPosition.Kind);
        Assert.Null(result.UncategorizedPosition.CategoryId);
        Assert.Null(result.UncategorizedPosition.CurrentDisplayName);
        Assert.Null(result.UncategorizedPosition.CurrentLifecycle);
        Assert.Null(result.UncategorizedPosition.PlannedMinorUnits);
        Assert.Null(result.UncategorizedPosition.RemainingMinorUnits);
        Assert.Null(result.UncategorizedPosition.OverMinorUnits);
        Assert.Equal(33, result.UncategorizedPosition.ActualMinorUnits);
        Assert.Equal(33, result.Totals.UncategorizedActualMinorUnits);
    }

    [Fact]
    public void Multiple_actuals_same_category_sum_exactly_once_per_ordinal()
    {
        var result = Calculate(
            entries: [Entry(CatA, 1_000)],
            members:
            [
                Member(0, "tx-1", CatA, 100),
                Member(1, "tx-2", CatA, 200),
                Member(2, "tx-3", CatA, 50)
            ],
            known: [Known(CatA)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(350, position.ActualMinorUnits);
        Assert.Equal(650, position.RemainingMinorUnits);
        Assert.Equal(350, result.Totals.ActualMinorUnits);
    }

    // ── Remaining / Over equations ───────────────────────────────────────────

    [Fact]
    public void When_planned_at_least_actual_Remaining_is_difference_and_Over_is_zero()
    {
        var result = Calculate(
            entries: [Entry(CatA, 1_000)],
            members: [Member(0, "tx-1", CatA, 1_000)],
            known: [Known(CatA)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(0, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
        Assert.Equal(0, result.Totals.RemainingMinorUnits);
        Assert.Equal(0, result.Totals.OverMinorUnits);
    }

    [Fact]
    public void When_actual_exceeds_planned_Over_is_difference_and_Remaining_is_zero()
    {
        var result = Calculate(
            entries: [Entry(CatA, 500)],
            members: [Member(0, "tx-1", CatA, 800)],
            known: [Known(CatA)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(0, position.RemainingMinorUnits);
        Assert.Equal(300, position.OverMinorUnits);
        Assert.Equal(0, result.Totals.RemainingMinorUnits);
        Assert.Equal(300, result.Totals.OverMinorUnits);
    }

    [Fact]
    public void Negative_refund_heavy_actual_is_preserved_exactly_and_not_clamped()
    {
        var result = Calculate(
            entries: [Entry(CatA, 1_000)],
            members: [Member(0, "tx-refund", CatA, -250)],
            known: [Known(CatA)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(-250, position.ActualMinorUnits);
        Assert.Equal(1_250, position.RemainingMinorUnits);
        Assert.Equal(0, position.OverMinorUnits);
        Assert.Equal(-250, result.Totals.ActualMinorUnits);
        Assert.Equal(-250, result.Totals.BudgetedActualMinorUnits);
    }

    [Fact]
    public void Negative_Uncategorized_actual_is_preserved_exactly()
    {
        var result = Calculate(
            entries: [],
            members: [Member(0, "tx-1", null, -40)],
            known: []);

        Assert.Equal(-40, result.UncategorizedPosition.ActualMinorUnits);
        Assert.Equal(-40, result.Totals.UncategorizedActualMinorUnits);
        Assert.Equal(-40, result.Totals.ActualMinorUnits);
        Assert.Equal(0, result.Totals.PlannedMinorUnits);
        // remaining = max(0 - (-40), 0) = 40; over = 0
        Assert.Equal(40, result.Totals.RemainingMinorUnits);
        Assert.Equal(0, result.Totals.OverMinorUnits);
    }

    // ── Empty plan / no actuals ──────────────────────────────────────────────

    [Fact]
    public void Empty_entries_and_empty_members_yield_exact_zero_totals()
    {
        var result = Calculate([], [], []);

        Assert.Empty(result.CategoryPositions);
        Assert.Equal(0, result.UncategorizedPosition.ActualMinorUnits);
        Assert.Equal(0, result.Totals.PlannedMinorUnits);
        Assert.Equal(0, result.Totals.ActualMinorUnits);
        Assert.Equal(0, result.Totals.RemainingMinorUnits);
        Assert.Equal(0, result.Totals.OverMinorUnits);
        Assert.Equal(0, result.Totals.BudgetedActualMinorUnits);
        Assert.Equal(0, result.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(0, result.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(0, result.Totals.UncategorizedActualMinorUnits);
    }

    [Fact]
    public void Plan_with_no_matching_actuals_preserves_planned_values_and_zero_actuals()
    {
        var result = Calculate(
            entries: [Entry(CatA, 900), Entry(CatB, 0)],
            members: [],
            known: [Known(CatA), Known(CatB)]);

        Assert.Equal(2, result.CategoryPositions.Count);
        Assert.All(result.CategoryPositions, p => Assert.Equal(0, p.ActualMinorUnits));
        Assert.Equal(900, result.Totals.PlannedMinorUnits);
        Assert.Equal(0, result.Totals.ActualMinorUnits);
        Assert.Equal(900, result.Totals.RemainingMinorUnits);
        Assert.Equal(0, result.Totals.OverMinorUnits);
    }

    [Fact]
    public void Omitted_known_category_without_actuals_is_not_invented()
    {
        var result = Calculate(
            entries: [Entry(CatA, 100)],
            members: [],
            known: [Known(CatA), Known(CatC)]);

        Assert.DoesNotContain(result.CategoryPositions, p => p.CategoryId == CatC);
        Assert.Equal(CatA, Assert.Single(result.CategoryPositions).CategoryId);
    }

    // ── Ordering and determinism ─────────────────────────────────────────────

    [Fact]
    public void Category_positions_sort_by_stable_id_ascending_Uncategorized_last()
    {
        var result = Calculate(
            entries: [Entry(CatD, 10), Entry(CatA, 20)],
            members:
            [
                Member(0, "tx-1", CatC, 1),
                Member(1, "tx-2", null, 2),
                Member(2, "tx-3", CatB, 3)
            ],
            known: [Known(CatA), Known(CatB), Known(CatC), Known(CatD)]);

        // Plan has CatA + CatD; CatB and CatC unbudgeted actuals. Sorted by id ascending.
        var ids = result.CategoryPositions.Select(p => p.CategoryId).ToArray();
        Assert.Equal(
            new[] { CatA, CatB, CatC, CatD }.Order(StringComparer.Ordinal).ToArray(),
            ids);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, result.UncategorizedPosition.Kind);
        Assert.Equal(2, result.UncategorizedPosition.ActualMinorUnits);
    }

    [Fact]
    public void Identical_inputs_produce_semantically_identical_results()
    {
        var entries = new[] { Entry(CatA, 500), Entry(CatB, 0) };
        var members = new[]
        {
            Member(0, "tx-1", CatA, 100),
            Member(1, "tx-2", CatC, 50),
            Member(2, "tx-3", null, 25)
        };
        var known = new[] { Known(CatA), Known(CatB), Known(CatC) };

        var first = Calculate(entries, members, known);
        var second = Calculate(entries, members, known);

        Assert.Equal(first.CategoryPositions, second.CategoryPositions);
        Assert.Equal(first.UncategorizedPosition, second.UncategorizedPosition);
        Assert.Equal(first.Totals, second.Totals);
    }

    [Fact]
    public void Calculation_schema_version_is_stable_provenance_constant()
    {
        Assert.Equal("budget-position-v2", BudgetPositionCalculator.CalculationSchemaVersion);
    }

    // ── Full-set reconciliation ──────────────────────────────────────────────

    [Fact]
    public void Planned_equals_checked_entry_sum_and_actual_equals_four_bucket_sum()
    {
        var result = Calculate(
            entries: [Entry(CatA, 1_000), Entry(CatB, 0), Entry(CatD, 250)],
            members:
            [
                Member(0, "tx-1", CatA, 400),
                Member(1, "tx-2", CatB, 10),
                Member(2, "tx-3", CatC, 30),
                Member(3, "tx-4", null, 5)
            ],
            known: [Known(CatA), Known(CatB), Known(CatC), Known(CatD)]);

        Assert.Equal(1_250, result.Totals.PlannedMinorUnits);
        Assert.Equal(400, result.Totals.BudgetedActualMinorUnits);
        Assert.Equal(10, result.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(30, result.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(5, result.Totals.UncategorizedActualMinorUnits);
        Assert.Equal(
            result.Totals.BudgetedActualMinorUnits
            + result.Totals.ZeroBudgetActualMinorUnits
            + result.Totals.UnbudgetedActualMinorUnits
            + result.Totals.UncategorizedActualMinorUnits,
            result.Totals.ActualMinorUnits);
        Assert.Equal(445, result.Totals.ActualMinorUnits);
        Assert.Equal(805, result.Totals.RemainingMinorUnits);
        Assert.Equal(0, result.Totals.OverMinorUnits);
    }

    [Fact]
    public void Expected_snapshot_total_mismatch_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members: [Member(0, "tx-1", CatA, 40)],
                known: [Known(CatA)],
                expectedTotal: 99));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expected_snapshot_total_match_succeeds()
    {
        var result = Calculate(
            entries: [Entry(CatA, 100)],
            members: [Member(0, "tx-1", CatA, 40)],
            known: [Known(CatA)],
            expectedTotal: 40);

        Assert.Equal(40, result.Totals.ActualMinorUnits);
    }

    // ── Integrity: ordinals, unknown, duplicates ─────────────────────────────

    [Fact]
    public void Duplicate_ordinals_are_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members:
                [
                    Member(0, "tx-1", CatA, 10),
                    Member(0, "tx-2", CatA, 20)
                ],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate ordinal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_ordinal_in_dense_range_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members:
                [
                    Member(0, "tx-1", CatA, 10),
                    Member(2, "tx-2", CatA, 20)
                ],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing ordinal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_category_on_actual_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members: [Member(0, "tx-1", "cat_unknown", 10)],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_category_on_plan_entry_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry("cat_unknown", 100)],
                members: [],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Category_lifecycle_Unknown_in_evidence_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members: [],
                known:
                [
                    new CategoryLifecycleEvidence(
                        CatA,
                        "Name",
                        CategoryLifecycleStatus.Unknown,
                        "1.0")
                ]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_plan_category_entries_are_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100), Entry(CatA, 200)],
                members: [],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate category", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_transaction_identity_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 100)],
                members:
                [
                    Member(0, "tx-same", CatA, 10),
                    Member(1, "tx-same", CatA, 20)
                ],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Archived_category_with_plan_entry_remains_Budgeted()
    {
        var result = Calculate(
            entries: [Entry(CatA, 300)],
            members: [Member(0, "tx-1", CatA, 50)],
            known: [Known(CatA, "Archived Food", CategoryLifecycleStatus.Archived)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, position.Kind);
        Assert.Equal(CategoryLifecycleStatus.Archived, position.CurrentLifecycle);
        Assert.Equal(50, position.ActualMinorUnits);
    }

    [Fact]
    public void Archived_category_omitted_from_plan_is_Unbudgeted_not_Uncategorized()
    {
        var result = Calculate(
            entries: [],
            members: [Member(0, "tx-1", CatC, 12)],
            known: [Known(CatC, "Old", CategoryLifecycleStatus.Archived)]);

        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Unbudgeted, position.Kind);
        Assert.Equal(CategoryLifecycleStatus.Archived, position.CurrentLifecycle);
        Assert.Equal(0, result.UncategorizedPosition.ActualMinorUnits);
    }

    // ── Overflow ────────────────────────────────────────────────────────────

    [Fact]
    public void Planned_sum_overflow_is_integrity_failure_with_no_partial_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, long.MaxValue), Entry(CatB, 1)],
                members: [],
                known: [Known(CatA), Known(CatB)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("overflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Actual_sum_overflow_is_integrity_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 0)],
                members:
                [
                    Member(0, "tx-1", CatA, long.MaxValue),
                    Member(1, "tx-2", CatA, 1)
                ],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
        Assert.Contains("overflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Variance_overflow_when_subtracting_MinValue_is_integrity_failure()
    {
        // planned - actual where actual = long.MinValue and planned = 0 overflows when forming difference.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Calculate(
                entries: [Entry(CatA, 0)],
                members: [Member(0, "tx-1", CatA, long.MinValue)],
                known: [Known(CatA)]));

        Assert.StartsWith(BudgetErrors.Integrity, ex.Message, StringComparison.Ordinal);
    }

    // ── Provenance attachment ────────────────────────────────────────────────

    [Fact]
    public void ToPosition_attaches_revision_period_ledger_and_schema()
    {
        var calculation = Calculate(
            entries: [Entry(CatA, 100)],
            members: [Member(0, "tx-1", CatA, 40)],
            known: [Known(CatA)]);

        var revision = new BudgetPlanRevision(
            RevisionId: "rev_1",
            PlanId: "plan_1",
            RevisionNumber: 1,
            Status: BudgetRevisionStatus.Active,
            ActorKind: "owner",
            ActorLabel: "owner",
            ActorRunId: null,
            Reason: "seed",
            CreatedAtUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            CategoryContractVersion: "1.0",
            PayloadHash: "abc",
            ActivatedAtUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            SupersededAtUtc: null,
            SupersededByRevisionId: null,
            Entries: [Entry(CatA, 100)]);

        var period = new BudgetPeriodDetail(
            2026, 7, "ZAR", "2026-07-01", "2026-08-01", BudgetPeriodState.Current);
        var ledger = new LedgerSnapshotEvidence("1.0", "snap-1", "2026-07-27T12:00:00Z", "gen-1");

        var position = BudgetPositionCalculator.ToPosition(calculation, revision, period, ledger);

        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, position.CalculationSchemaVersion);
        Assert.Equal("plan_1", position.PlanId);
        Assert.Equal("rev_1", position.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, position.RevisionStatus);
        Assert.Equal("ZAR", position.CurrencyCode);
        Assert.Equal("1.0", position.CategoryContractVersion);
        Assert.Equal(ledger, position.Ledger);
        Assert.Equal(calculation.CategoryPositions, position.CategoryPositions);
        Assert.Equal(calculation.UncategorizedPosition, position.UncategorizedPosition);
        Assert.Equal(calculation.Totals, position.Totals);
    }

    [Fact]
    public void CalculatePosition_matches_manual_bucket_then_ToPosition()
    {
        var entries = new[] { Entry(CatA, 200) };
        var members = new[] { Member(0, "tx-1", CatA, 50) };
        var known = new[] { Known(CatA) };
        var revision = new BudgetPlanRevision(
            "rev_x", "plan_x", 2, BudgetRevisionStatus.Draft,
            "owner", "owner", null, "draft",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            "1.0", "hash", null, null, null, entries);
        var period = new BudgetPeriodDetail(
            2026, 7, "ZAR", "2026-07-01", "2026-08-01", BudgetPeriodState.Current);
        var ledger = new LedgerSnapshotEvidence("1.0", "snap", "exp", "fp");

        var viaConvenience = BudgetPositionCalculator.CalculatePosition(
            revision, period, ledger, members, known);
        var viaParts = BudgetPositionCalculator.ToPosition(
            BudgetPositionCalculator.Calculate(entries, members, known),
            revision, period, ledger);

        Assert.Equal(viaParts.CalculationSchemaVersion, viaConvenience.CalculationSchemaVersion);
        Assert.Equal(viaParts.PlanId, viaConvenience.PlanId);
        Assert.Equal(viaParts.RevisionId, viaConvenience.RevisionId);
        Assert.Equal(viaParts.RevisionStatus, viaConvenience.RevisionStatus);
        Assert.Equal(viaParts.Period, viaConvenience.Period);
        Assert.Equal(viaParts.CurrencyCode, viaConvenience.CurrencyCode);
        Assert.Equal(viaParts.CategoryContractVersion, viaConvenience.CategoryContractVersion);
        Assert.Equal(viaParts.Ledger, viaConvenience.Ledger);
        Assert.Equal(viaParts.CategoryPositions, viaConvenience.CategoryPositions);
        Assert.Equal(viaParts.UncategorizedPosition, viaConvenience.UncategorizedPosition);
        Assert.Equal(viaParts.Totals, viaConvenience.Totals);
    }

    // ── Null guards ──────────────────────────────────────────────────────────

    [Fact]
    public void Null_entries_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BudgetPositionCalculator.Calculate(null!, [], []));
    }

    [Fact]
    public void Null_members_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BudgetPositionCalculator.Calculate([], null!, []));
    }

    [Fact]
    public void Null_known_categories_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BudgetPositionCalculator.Calculate([], [], null!));
    }

    // ── Property-style reconciliation cases ──────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 40, 60, 0)]
    [InlineData(100, 100, 0, 0)]
    [InlineData(100, 140, 0, 40)]
    [InlineData(100, -20, 120, 0)]
    [InlineData(0, 50, 0, 50)]
    [InlineData(0, -10, 10, 0)]
    public void Category_variance_equations_hold_for_signed_boundary_values(
        long planned,
        long actual,
        long expectedRemaining,
        long expectedOver)
    {
        var result = Calculate(
            entries: [Entry(CatA, planned)],
            members: actual == 0 && planned >= 0
                ? []
                : [Member(0, "tx-1", CatA, actual)],
            known: [Known(CatA)]);

        // When actual==0 and we pass empty members, actual is still 0.
        var position = Assert.Single(result.CategoryPositions);
        Assert.Equal(planned, position.PlannedMinorUnits);
        Assert.Equal(actual, position.ActualMinorUnits);
        Assert.Equal(expectedRemaining, position.RemainingMinorUnits);
        Assert.Equal(expectedOver, position.OverMinorUnits);
        Assert.Equal(expectedRemaining, result.Totals.RemainingMinorUnits);
        Assert.Equal(expectedOver, result.Totals.OverMinorUnits);
    }

    [Fact]
    public void Mixed_buckets_property_each_member_contributes_to_exactly_one_bucket()
    {
        var members = new[]
        {
            Member(0, "tx-budgeted", CatA, 100),
            Member(1, "tx-zero", CatB, 20),
            Member(2, "tx-unbudgeted", CatC, 30),
            Member(3, "tx-uncategorized", null, 40),
            Member(4, "tx-budgeted-2", CatA, 5)
        };

        var result = Calculate(
            entries: [Entry(CatA, 500), Entry(CatB, 0)],
            members: members,
            known: [Known(CatA), Known(CatB), Known(CatC)]);

        Assert.Equal(105, result.Totals.BudgetedActualMinorUnits);
        Assert.Equal(20, result.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(30, result.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(40, result.Totals.UncategorizedActualMinorUnits);
        Assert.Equal(members.Sum(m => m.BudgetActualMinorUnits), result.Totals.ActualMinorUnits);

        // No category appears in more than one position kind.
        Assert.Equal(
            result.CategoryPositions.Select(p => p.CategoryId).Distinct(StringComparer.Ordinal).Count(),
            result.CategoryPositions.Count);
    }

    [Fact]
    public void All_zero_plan_with_mixed_actuals_reconciles()
    {
        var result = Calculate(
            entries: [Entry(CatA, 0), Entry(CatB, 0)],
            members:
            [
                Member(0, "tx-1", CatA, 1),
                Member(1, "tx-2", CatB, 2),
                Member(2, "tx-3", CatC, 3),
                Member(3, "tx-4", null, 4)
            ],
            known: [Known(CatA), Known(CatB), Known(CatC)]);

        Assert.Equal(0, result.Totals.PlannedMinorUnits);
        Assert.Equal(10, result.Totals.ActualMinorUnits);
        Assert.Equal(0, result.Totals.RemainingMinorUnits);
        Assert.Equal(10, result.Totals.OverMinorUnits);
        Assert.All(
            result.CategoryPositions.Where(p => p.Kind == BudgetCategoryPositionKind.ZeroBudget),
            p => Assert.Equal(0, p.PlannedMinorUnits));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BudgetPositionCalculation Calculate(
        IReadOnlyList<BudgetPlanEntry> entries,
        IReadOnlyList<BudgetActualMember> members,
        IReadOnlyList<CategoryLifecycleEvidence> known,
        long? expectedTotal = null) =>
        BudgetPositionCalculator.Calculate(entries, members, known, expectedTotal);

    private static BudgetPlanEntry Entry(string categoryId, long planned) =>
        new(categoryId, planned);

    private static BudgetActualMember Member(
        int ordinal,
        string transactionId,
        string? categoryId,
        long amount) =>
        new(ordinal, transactionId, "2026-07-15", categoryId, amount, AncestryIds: [], EffectiveCategoryId: null);

    private static CategoryLifecycleEvidence Known(
        string categoryId,
        string? displayName = null,
        CategoryLifecycleStatus lifecycle = CategoryLifecycleStatus.Active) =>
        new(categoryId, displayName ?? categoryId, lifecycle, "1.0");
}
