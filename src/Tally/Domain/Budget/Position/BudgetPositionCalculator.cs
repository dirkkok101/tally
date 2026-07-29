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
    public const string CalculationSchemaVersion = "budget-position-v2";

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

        // Nearest-ancestor envelope resolution (DD-BUDGET-CATEGORY-ENVELOPE-RESOLUTION):
        // each member resolves to at most one governing plan entry; partition is direct vs descendant.
        var directByEnvelope = new Dictionary<string, long>(StringComparer.Ordinal);
        var descendantByEnvelope = new Dictionary<string, long>(StringComparer.Ordinal);
        var absorbedByEnvelope = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var absorbedSeenByEnvelope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unbudgetedByCategory = new Dictionary<string, long>(StringComparer.Ordinal);
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

                var effectiveCategoryId = ResolveEnvelope(member, plannedByCategory);
                if (effectiveCategoryId is null)
                {
                    // Unbudgeted: keyed by exact assigned Spend Category; never absorbs.
                    if (unbudgetedByCategory.TryGetValue(member.CategoryId, out var existingUnbudgeted))
                    {
                        unbudgetedByCategory[member.CategoryId] =
                            checked(existingUnbudgeted + member.BudgetActualMinorUnits);
                    }
                    else
                    {
                        unbudgetedByCategory[member.CategoryId] = member.BudgetActualMinorUnits;
                    }

                    continue;
                }

                if (string.Equals(member.CategoryId, effectiveCategoryId, StringComparison.Ordinal))
                {
                    if (directByEnvelope.TryGetValue(effectiveCategoryId, out var existingDirect))
                    {
                        directByEnvelope[effectiveCategoryId] =
                            checked(existingDirect + member.BudgetActualMinorUnits);
                    }
                    else
                    {
                        directByEnvelope[effectiveCategoryId] = member.BudgetActualMinorUnits;
                    }
                }
                else
                {
                    if (descendantByEnvelope.TryGetValue(effectiveCategoryId, out var existingDescendant))
                    {
                        descendantByEnvelope[effectiveCategoryId] =
                            checked(existingDescendant + member.BudgetActualMinorUnits);
                    }
                    else
                    {
                        descendantByEnvelope[effectiveCategoryId] = member.BudgetActualMinorUnits;
                    }

                    // Absorbed identifiers in ascending member-ordinal first-seen order.
                    if (!absorbedSeenByEnvelope.TryGetValue(effectiveCategoryId, out var seen))
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                        absorbedSeenByEnvelope[effectiveCategoryId] = seen;
                        absorbedByEnvelope[effectiveCategoryId] = [];
                    }

                    if (seen.Add(member.CategoryId))
                    {
                        absorbedByEnvelope[effectiveCategoryId].Add(member.CategoryId);
                    }
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
        var positions = new List<CategoryPosition>(plannedByCategory.Count + unbudgetedByCategory.Count);
        long budgetedActualSubtotal = 0;
        long zeroBudgetActualSubtotal = 0;
        long unbudgetedActualSubtotal = 0;
        long plannedTotal = 0;

        try
        {
            foreach (var (categoryId, planned) in plannedByCategory)
            {
                plannedTotal = checked(plannedTotal + planned);
                directByEnvelope.TryGetValue(categoryId, out var direct);
                descendantByEnvelope.TryGetValue(categoryId, out var descendant);
                var actual = checked(direct + descendant);
                absorbedByEnvelope.TryGetValue(categoryId, out var absorbedList);
                IReadOnlyList<string>? absorbed = absorbedList is { Count: > 0 } ? absorbedList : null;

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
                    OverMinorUnits: over,
                    DirectActualMinorUnits: direct,
                    DescendantActualMinorUnits: descendant,
                    AbsorbedCategoryIds: absorbed));
            }

            // Unbudgeted: exact assigned Spend Category, no absorption, no aggregated ancestors.
            foreach (var (categoryId, actual) in unbudgetedByCategory)
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
                    OverMinorUnits: null,
                    DirectActualMinorUnits: actual,
                    DescendantActualMinorUnits: 0,
                    AbsorbedCategoryIds: null));
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
                OverMinorUnits: null,
                DirectActualMinorUnits: uncategorizedActual,
                DescendantActualMinorUnits: 0,
                AbsorbedCategoryIds: null);

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
    /// Resolve the governing Category Budget Entry for one actual by scanning frozen ancestry
    /// from self toward root (last index → 0). Returns the nearest planned category id, or
    /// <c>null</c> when the outcome is Unbudgeted Spend / Uncategorized Spend
    /// (DD-BUDGET-CATEGORY-ENVELOPE-RESOLUTION / EffectiveCategoryId).
    /// </summary>
    public static string? ResolveEnvelope(
        BudgetActualMember member,
        IReadOnlyDictionary<string, long> plannedByCategory)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(plannedByCategory);

        if (member.CategoryId is null)
        {
            return null;
        }

        // Ancestry is validated before resolution: non-empty, self-last, distinct, known.
        // Scan self → root (final index toward zero); first planned element wins.
        var ancestry = member.AncestryIds;
        for (var i = ancestry.Count - 1; i >= 0; i--)
        {
            var candidate = ancestry[i];
            if (plannedByCategory.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return null;
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

                // Ancestry mirrors the LEDGER ActualsCalculator invariant
                // (DD-BUDGET-CATEGORY-ENVELOPE-RESOLUTION / TC-BUDGET-ENVELOPE-ANCESTRY-INTEGRITY).
                ValidateCategorizedAncestry(member, known);
            }
            else if (member.AncestryIds is { Count: > 0 })
            {
                throw Integrity(
                    "Uncategorized actual membership carries a non-empty frozen Spend Category ancestry.");
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

    /// <summary>
    /// Categorized member ancestry must be non-empty, end with the assigned Spend Category,
    /// contain distinct identifiers, and resolve every element in known evidence.
    /// </summary>
    private static void ValidateCategorizedAncestry(
        BudgetActualMember member,
        IReadOnlyDictionary<string, CategoryLifecycleEvidence> known)
    {
        var ancestry = member.AncestryIds;
        if (ancestry is null || ancestry.Count == 0)
        {
            throw Integrity(
                "Categorized actual membership has empty frozen Spend Category ancestry.");
        }

        if (!string.Equals(ancestry[^1], member.CategoryId, StringComparison.Ordinal))
        {
            throw Integrity(
                "Categorized actual membership frozen ancestry does not end with the assigned Spend Category identifier.");
        }

        var seen = new HashSet<string>(ancestry.Count, StringComparer.Ordinal);
        foreach (var ancestryId in ancestry)
        {
            if (string.IsNullOrWhiteSpace(ancestryId))
            {
                throw Integrity("Frozen Spend Category ancestry contains a blank identifier.");
            }

            if (!seen.Add(ancestryId))
            {
                throw Integrity("Frozen Spend Category ancestry contains a repeated identifier.");
            }

            if (!known.ContainsKey(ancestryId))
            {
                throw Integrity(
                    $"Frozen Spend Category ancestry references unknown category identifier '{ancestryId}'.");
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

    private static InvalidOperationException Integrity(string detail, Exception? inner = null)
    {
        // First phrase is the amount-free reason token (bd-nqp9); full prose stays in Message.
        var reason = detail.Split(':', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "unspecified";
        }

        var token = string.Concat(reason.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'))
            .Trim('_');
        var exception = inner is null
            ? new InvalidOperationException($"{IntegrityErrorCode}: {detail}")
            : new InvalidOperationException($"{IntegrityErrorCode}: {detail}", inner);
        exception.Data[IntegrityReasonDataKey] = token;
        return exception;
    }

    /// <summary>Exception.Data key for amount-free calculator integrity reasons.</summary>
    public const string IntegrityReasonDataKey = "BudgetIntegrityReason";

    public static string? TryGetIntegrityReason(Exception exception) =>
        exception.Data[IntegrityReasonDataKey] as string;
}

/// <summary>
/// Bucket results produced by <see cref="BudgetPositionCalculator"/> before provenance attachment.
/// Category positions are ordered by stable category ID ascending; Uncategorized is separate.
/// </summary>
public sealed record BudgetPositionCalculation(
    IReadOnlyList<CategoryPosition> CategoryPositions,
    CategoryPosition UncategorizedPosition,
    BudgetPositionTotals Totals);
