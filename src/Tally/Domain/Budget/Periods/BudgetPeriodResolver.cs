using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;

namespace Tally.Domain.Budget.Periods;

/// <summary>
/// Validates explicit period inputs and classifies lifecycle from one trusted host DateOnly
/// (DD-BUDGET-TRUSTED-PERIOD-TIME). Caller and stored columns never supply authority for state.
/// </summary>
public static class BudgetPeriodResolver
{
    public const string InvalidPeriodError = BudgetErrors.InvalidPeriod;

    /// <summary>
    /// Builds a validated half-open ZAR calendar month from explicit year/month/currency.
    /// </summary>
    public static bool TryCreate(int year, int month, string? currencyCode, out BudgetPeriod period, out string? error)
    {
        period = default;
        if (currencyCode is not "ZAR"
            || month is < 1 or > 12
            || year < DateOnly.MinValue.Year || year > DateOnly.MaxValue.Year)
        {
            error = InvalidPeriodError;
            return false;
        }

        try
        {
            var startInclusive = new DateOnly(year, month, 1);
            var endExclusive = startInclusive.AddMonths(1);
            period = BudgetPeriod.CreateValidated(year, month, currencyCode, startInclusive, endExclusive);
            error = null;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Calendar overflow (e.g. 9999-12 has no next-month day one).
            error = InvalidPeriodError;
            return false;
        }
    }

    /// <summary>
    /// Validates an explicit wire period; omitted input fails closed before state access.
    /// </summary>
    public static bool TryCreate(BudgetPeriodInput? input, out BudgetPeriod period, out string? error)
    {
        if (input is null)
        {
            period = default;
            error = InvalidPeriodError;
            return false;
        }

        return TryCreate(input.Year, input.Month, input.CurrencyCode, out period, out error);
    }

    /// <summary>
    /// Classifies a validated period against one trusted calendar date:
    /// Current when startInclusive &lt;= today &lt; endExclusive;
    /// Future when startInclusive &gt; today;
    /// Closed when endExclusive &lt;= today.
    /// </summary>
    public static BudgetPeriodState Classify(BudgetPeriod period, DateOnly today)
    {
        if (period.StartInclusive > today) return BudgetPeriodState.Future;
        if (period.EndExclusive <= today) return BudgetPeriodState.Closed;
        return BudgetPeriodState.Current;
    }

    /// <summary>
    /// Validates explicit input then classifies against a trusted DateOnly already captured by the caller.
    /// </summary>
    public static bool Resolve(
        int year,
        int month,
        string? currencyCode,
        DateOnly today,
        out BudgetPeriod period,
        out BudgetPeriodState state,
        out string? error)
    {
        if (!TryCreate(year, month, currencyCode, out period, out error))
        {
            state = default;
            return false;
        }

        state = Classify(period, today);
        error = null;
        return true;
    }

    /// <summary>
    /// Validates explicit input then classifies from one TimeProvider-derived host local calendar date.
    /// Does not accept caller current date, timezone, or lifecycle state.
    /// </summary>
    public static bool Resolve(
        int year,
        int month,
        string? currencyCode,
        TimeProvider timeProvider,
        out BudgetPeriod period,
        out BudgetPeriodState state,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        return Resolve(year, month, currencyCode, today, out period, out state, out error);
    }
}
