using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;

namespace Tally.Features.Budget.Contract;

/// <summary>
/// Pure domain-to-contract mapping root for BUDGET (DD-BUDGET-APPLICATION-ARCHITECTURE).
/// No I/O, no TimeProvider, no Ledger access — only pure transforms.
/// </summary>
public static class BudgetContractMapper
{
    public static long SumPlannedMinorUnits(IReadOnlyList<BudgetPlanEntryInput> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.PlannedMinorUnits < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), "Planned minor units must be non-negative.");
            }

            total = checked(total + entry.PlannedMinorUnits);
        }

        return total;
    }

    public static long SumPlannedMinorUnits(IReadOnlyList<BudgetPlanEntryDetail> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        long total = 0;
        foreach (var entry in entries)
        {
            total = checked(total + entry.PlannedMinorUnits);
        }

        return total;
    }

    public static IReadOnlyList<BudgetPlanEntryDetail> OrderEntries(IReadOnlyList<BudgetPlanEntryDetail> entries) =>
        entries.OrderBy(entry => entry.CategoryId, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<CategoryPosition> OrderCategoryPositions(IReadOnlyList<CategoryPosition> positions) =>
        positions
            .Where(position => position.Kind != BudgetCategoryPositionKind.Uncategorized)
            .OrderBy(position => position.CategoryId, StringComparer.Ordinal)
            .ToArray();

    public static bool IsSupportedContractVersion(string? version) =>
        string.Equals(version, BudgetOperationIds.ContractVersion, StringComparison.Ordinal);
}
