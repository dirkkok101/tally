using System.Globalization;

namespace Tally.Domain.Budget.Periods;

/// <summary>
/// Explicit ZAR calendar-month Budget Period (DM-BUDGET-PERIOD-PLAN).
/// Boundaries are half-open: StartInclusive is month day one; EndExclusive is next month day one.
/// Lifecycle state is never stored — derive via <see cref="BudgetPeriodResolver.Classify"/>.
/// </summary>
public readonly record struct BudgetPeriod
{
    private const string DateFormat = "yyyy-MM-dd";

    private BudgetPeriod(int year, int month, string currencyCode, DateOnly startInclusive, DateOnly endExclusive)
    {
        Year = year;
        Month = month;
        CurrencyCode = currencyCode;
        StartInclusive = startInclusive;
        EndExclusive = endExclusive;
    }

    public int Year { get; }
    public int Month { get; }
    public string CurrencyCode { get; }
    public DateOnly StartInclusive { get; }
    public DateOnly EndExclusive { get; }

    internal static BudgetPeriod CreateValidated(
        int year,
        int month,
        string currencyCode,
        DateOnly startInclusive,
        DateOnly endExclusive) =>
        new(year, month, currencyCode, startInclusive, endExclusive);

    public string FormatStartInclusive() =>
        StartInclusive.ToString(DateFormat, CultureInfo.InvariantCulture);

    public string FormatEndExclusive() =>
        EndExclusive.ToString(DateFormat, CultureInfo.InvariantCulture);
}
