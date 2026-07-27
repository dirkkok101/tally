using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Domain.Budget.Plans;

namespace Tally.Domain.Budget.Position;

/// <summary>
/// Pure exhaustive Budget Position bucketing (DD-BUDGET-EXACT-POSITION-CALCULATION).
/// No I/O, no storage, no Ledger calls — one immutable plan entry set and one complete
/// actuals membership become Budgeted / ZeroBudget / Unbudgeted / Uncategorized once.
/// </summary>
public static class BudgetPositionCalculator
{
    /// <summary>Provenance schema for calculator output (DM-BUDGET-POSITION-PROJECTION).</summary>
    public const string CalculationSchemaVersion = "budget-position-v1";

    public const string IntegrityErrorCode = BudgetErrors.Integrity;

    /// <summary>
    /// Pure checked exhaustive bucketing over plan entries and one complete actuals membership.
    /// </summary>
    /// <exception cref="ArgumentNullException">When a required collection is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// When membership integrity fails (duplicate/missing ordinals, unknown categories,
    /// duplicate plan categories, overflow, or snapshot total mismatch). Message is
    /// prefixed with <see cref="IntegrityErrorCode"/>.
    /// </exception>
    public static BudgetPositionCalculation Calculate(
        IReadOnlyList<BudgetPlanEntry> entries,
        IReadOnlyList<BudgetActualMember> actualMembers,
        IReadOnlyList<CategoryLifecycleEvidence> knownCategories,
        long? expectedBudgetActualTotalMinorUnits = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(actualMembers);
        ArgumentNullException.ThrowIfNull(knownCategories);

        var known = IndexKnownCategories(knownCategories);
        var plannedByCategory = IndexPlanEntries(entries, known);

        ValidateMembership(actualMembers, known);

        // Sum actuals per stable category and uncategorized, each member once.
        var actualByCategory = new Dictionary<string, long>(StringComparer.Ordinal);
        long uncategorizedActual = 0;
        long membershipActualTotal = 0;

        try
        {
            foreach (var member in actualMembers)
            {
                membershipActualTotal = checked(membershipActualTotal + member.BudgetActualMinorUnits);

                if (member.CategoryId is null)
                {
                    uncategorizedActual = checked(uncategorizedActual + member.BudgetActualMinorUnits);
                    continue;
                }

                if (actualByCategory.TryGetValue(member.CategoryId, out var existing))
                {
                    actualByCategory[member.CategoryId] = checked(existing + member.BudgetActualMinorUnits);
                }
                else
                {
                    actualByCategory[member.CategoryId] = member.BudgetActualMinorUnits;
                }
            }
        }
        catch (OverflowException ex)
        {
            throw Integrity("Checked Int64 arithmetic overflowed while summing membership actuals.", ex);
        }

        if (expectedBudgetActualTotalMinorUnits is long expected
            && expected != membershipActualTotal)
        {
            throw Integrity(
                "Membership Budget Actual total does not reconcile to the expected Ledger snapshot total.");
        }

        // Every explicit plan entry produces a Budgeted or ZeroBudget row (including zeros).
        var positions = new List<CategoryPosition>(plannedByCategory.Count + actualByCategory.Count);
        long budgetedActualSubtotal = 0;
        long zeroBudgetActualSubtotal = 0;
        long unbudgetedActualSubtotal = 0;
        long plannedTotal = 0;

        try
        {
            foreach (var (categoryId, planned) in plannedByCategory)
            {
                plannedTotal = checked(plannedTotal + planned);
                actualByCategory.TryGetValue(categoryId, out var actual);
                actualByCategory.Remove(categoryId);

                var kind = planned > 0
                    ? BudgetCategoryPositionKind.Budgeted
                    : BudgetCategoryPositionKind.ZeroBudget;

                if (kind == BudgetCategoryPositionKind.Budgeted)
                {
                    budgetedActualSubtotal = checked(budgetedActualSubtotal + actual);
                }
                else
                {
                    zeroBudgetActualSubtotal = checked(zeroBudgetActualSubtotal + actual);
                }

                var (remaining, over) = Variance(planned, actual);
                var evidence = known[categoryId];
                positions.Add(new CategoryPosition(
                    CategoryId: categoryId,
                    CurrentDisplayName: evidence.CurrentDisplayName,
                    CurrentLifecycle: evidence.Lifecycle,
                    Kind: kind,
                    PlannedMinorUnits: planned,
                    ActualMinorUnits: actual,
                    RemainingMinorUnits: remaining,
                    OverMinorUnits: over));
            }

            // Remaining non-null category actuals are Unbudgeted (omitted from plan).
            foreach (var (categoryId, actual) in actualByCategory)
            {
                unbudgetedActualSubtotal = checked(unbudgetedActualSubtotal + actual);
                var evidence = known[categoryId];
                positions.Add(new CategoryPosition(
                    CategoryId: categoryId,
                    CurrentDisplayName: evidence.CurrentDisplayName,
                    CurrentLifecycle: evidence.Lifecycle,
                    Kind: BudgetCategoryPositionKind.Unbudgeted,
                    PlannedMinorUnits: null,
                    ActualMinorUnits: actual,
                    RemainingMinorUnits: null,
                    OverMinorUnits: null));
            }

            positions.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.CategoryId, right.CategoryId));

            var actualTotal = checked(
                budgetedActualSubtotal + zeroBudgetActualSubtotal + unbudgetedActualSubtotal + uncategorizedActual);

            if (actualTotal != membershipActualTotal)
            {
                throw Integrity("Bucket actual subtotals do not reconcile to the membership total.");
            }

            var (totalRemaining, totalOver) = Variance(plannedTotal, actualTotal);

            var totals = new BudgetPositionTotals(
                PlannedMinorUnits: plannedTotal,
                ActualMinorUnits: actualTotal,
                RemainingMinorUnits: totalRemaining,
                OverMinorUnits: totalOver,
                BudgetedActualMinorUnits: budgetedActualSubtotal,
                ZeroBudgetActualMinorUnits: zeroBudgetActualSubtotal,
                UnbudgetedActualMinorUnits: unbudgetedActualSubtotal,
                UncategorizedActualMinorUnits: uncategorizedActual);

            var uncategorized = new CategoryPosition(
                CategoryId: null,
                CurrentDisplayName: null,
                CurrentLifecycle: null,
                Kind: BudgetCategoryPositionKind.Uncategorized,
                PlannedMinorUnits: null,
                ActualMinorUnits: uncategorizedActual,
                RemainingMinorUnits: null,
                OverMinorUnits: null);

            return new BudgetPositionCalculation(
                CategoryPositions: positions,
                UncategorizedPosition: uncategorized,
                Totals: totals);
        }
        catch (OverflowException ex)
        {
            throw Integrity("Checked Int64 arithmetic overflowed while calculating Budget Position.", ex);
        }
    }

    /// <summary>
    /// Assemble a full <see cref="BudgetPosition"/> provenance envelope around a pure calculation.
    /// </summary>
    public static BudgetPosition ToPosition(
        BudgetPositionCalculation calculation,
        BudgetPlanRevision revision,
        BudgetPeriodDetail period,
        LedgerSnapshotEvidence ledger)
    {
        ArgumentNullException.ThrowIfNull(calculation);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(ledger);

        return new BudgetPosition(
            CalculationSchemaVersion: CalculationSchemaVersion,
            PlanId: revision.PlanId,
            RevisionId: revision.RevisionId,
            RevisionStatus: revision.Status,
            Period: period,
            CurrencyCode: period.CurrencyCode,
            CategoryContractVersion: revision.CategoryContractVersion,
            Ledger: ledger,
            CategoryPositions: calculation.CategoryPositions,
            UncategorizedPosition: calculation.UncategorizedPosition,
            Totals: calculation.Totals);
    }

    /// <summary>
    /// Convenience: calculate buckets and attach plan/period/ledger provenance in one call.
    /// </summary>
    public static BudgetPosition CalculatePosition(
        BudgetPlanRevision revision,
        BudgetPeriodDetail period,
        LedgerSnapshotEvidence ledger,
        IReadOnlyList<BudgetActualMember> actualMembers,
        IReadOnlyList<CategoryLifecycleEvidence> knownCategories,
        long? expectedBudgetActualTotalMinorUnits = null)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var calculation = Calculate(
            revision.Entries,
            actualMembers,
            knownCategories,
            expectedBudgetActualTotalMinorUnits);
        return ToPosition(calculation, revision, period, ledger);
    }

    private static Dictionary<string, CategoryLifecycleEvidence> IndexKnownCategories(
        IReadOnlyList<CategoryLifecycleEvidence> knownCategories)
    {
        var known = new Dictionary<string, CategoryLifecycleEvidence>(
            knownCategories.Count,
            StringComparer.Ordinal);

        foreach (var evidence in knownCategories)
        {
            if (string.IsNullOrWhiteSpace(evidence.CategoryId))
            {
                throw Integrity("Known category evidence contains a blank category identifier.");
            }

            if (evidence.Lifecycle == CategoryLifecycleStatus.Unknown)
            {
                throw Integrity(
                    $"Category '{evidence.CategoryId}' has unknown lifecycle evidence and cannot be used for position calculation.");
            }

            if (!known.TryAdd(evidence.CategoryId, evidence))
            {
                throw Integrity(
                    $"Known category evidence contains duplicate category identifier '{evidence.CategoryId}'.");
            }
        }

        return known;
    }

    private static Dictionary<string, long> IndexPlanEntries(
        IReadOnlyList<BudgetPlanEntry> entries,
        IReadOnlyDictionary<string, CategoryLifecycleEvidence> known)
    {
        var planned = new Dictionary<string, long>(entries.Count, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CategoryId))
            {
                throw Integrity("Plan entry contains a blank category identifier.");
            }

            if (entry.PlannedMinorUnits < 0)
            {
                throw Integrity(
                    $"Plan entry for category '{entry.CategoryId}' has a negative planned amount.");
            }

            if (!known.ContainsKey(entry.CategoryId))
            {
                throw Integrity(
                    $"Plan entry references unknown category identifier '{entry.CategoryId}'.");
            }

            if (!planned.TryAdd(entry.CategoryId, entry.PlannedMinorUnits))
            {
                throw Integrity(
                    $"Plan entries contain duplicate category identifier '{entry.CategoryId}'.");
            }
        }

        return planned;
    }

    private static void ValidateMembership(
        IReadOnlyList<BudgetActualMember> actualMembers,
        IReadOnlyDictionary<string, CategoryLifecycleEvidence> known)
    {
        var count = actualMembers.Count;
        if (count == 0)
        {
            return;
        }

        var seenOrdinals = new HashSet<int>(count);
        var seenTransactions = new HashSet<string>(count, StringComparer.Ordinal);

        foreach (var member in actualMembers)
        {
            if (!seenOrdinals.Add(member.Ordinal))
            {
                throw Integrity($"Actual membership contains duplicate ordinal {member.Ordinal}.");
            }

            if (string.IsNullOrWhiteSpace(member.TransactionId))
            {
                throw Integrity("Actual membership contains a blank transaction identity.");
            }

            if (!seenTransactions.Add(member.TransactionId))
            {
                throw Integrity(
                    $"Actual membership contains duplicate transaction identity '{member.TransactionId}'.");
            }

            if (member.CategoryId is not null)
            {
                if (string.IsNullOrWhiteSpace(member.CategoryId))
                {
                    throw Integrity("Actual membership contains a blank category identifier.");
                }

                if (!known.ContainsKey(member.CategoryId))
                {
                    throw Integrity(
                        $"Actual membership references unknown category identifier '{member.CategoryId}'.");
                }
            }
        }

        // Ordinals must be the complete dense set 0..count-1 (once each).
        for (var expected = 0; expected < count; expected++)
        {
            if (!seenOrdinals.Contains(expected))
            {
                throw Integrity(
                    $"Actual membership is missing ordinal {expected} (expected dense 0..{count - 1}).");
            }
        }
    }

    /// <summary>Remaining = max(planned − actual, 0); Over = max(actual − planned, 0).</summary>
    private static (long Remaining, long Over) Variance(long planned, long actual)
    {
        try
        {
            var difference = checked(planned - actual);
            if (difference >= 0)
            {
                return (difference, 0);
            }

            // actual > planned; over = actual - planned = -difference (checked).
            return (0, checked(-difference));
        }
        catch (OverflowException ex)
        {
            throw Integrity("Checked Int64 arithmetic overflowed while computing Remaining/Over.", ex);
        }
    }

    private static InvalidOperationException Integrity(string detail, Exception? inner = null) =>
        inner is null
            ? new InvalidOperationException($"{IntegrityErrorCode}: {detail}")
            : new InvalidOperationException($"{IntegrityErrorCode}: {detail}", inner);
}

/// <summary>
/// Bucket results produced by <see cref="BudgetPositionCalculator"/> before provenance attachment.
/// Category positions are ordered by stable category ID ascending; Uncategorized is separate.
/// </summary>
public sealed record BudgetPositionCalculation(
    IReadOnlyList<CategoryPosition> CategoryPositions,
    CategoryPosition UncategorizedPosition,
    BudgetPositionTotals Totals);
