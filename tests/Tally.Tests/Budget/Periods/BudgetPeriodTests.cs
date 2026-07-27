using System.Globalization;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Domain.Budget.Periods;
using Xunit;

namespace Tally.Tests.Budget.Periods;

/// <summary>
/// TC-BUDGET-PLAN-IDENTITY-CONTRACT / FR-BUDGET-PLAN-IDENTITY / DD-BUDGET-TRUSTED-PERIOD-TIME
/// Trusted monthly period identity and lifecycle classification — pure domain, no storage.
/// </summary>
public sealed class BudgetPeriodTests
{
    // --- Valid boundaries ---

    [Fact]
    public void TryCreate_valid_year_month_zar_resolves_month_first_half_open_bounds()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var period, out var error), error);
        Assert.Null(error);
        Assert.Equal(2026, period.Year);
        Assert.Equal(7, period.Month);
        Assert.Equal("ZAR", period.CurrencyCode);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartInclusive);
        Assert.Equal(new DateOnly(2026, 8, 1), period.EndExclusive);
        Assert.Equal("2026-07-01", period.FormatStartInclusive());
        Assert.Equal("2026-08-01", period.FormatEndExclusive());
    }

    [Fact]
    public void TryCreate_leap_year_february_ends_on_march_first()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2024, 2, "ZAR", out var period, out var error), error);
        Assert.Equal(new DateOnly(2024, 2, 1), period.StartInclusive);
        Assert.Equal(new DateOnly(2024, 3, 1), period.EndExclusive);
    }

    [Fact]
    public void TryCreate_non_leap_february_ends_on_march_first()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2025, 2, "ZAR", out var period, out var error), error);
        Assert.Equal(new DateOnly(2025, 2, 1), period.StartInclusive);
        Assert.Equal(new DateOnly(2025, 3, 1), period.EndExclusive);
    }

    [Fact]
    public void TryCreate_december_crosses_year_boundary_to_january_first()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 12, "ZAR", out var period, out var error), error);
        Assert.Equal(new DateOnly(2026, 12, 1), period.StartInclusive);
        Assert.Equal(new DateOnly(2027, 1, 1), period.EndExclusive);
    }

    [Fact]
    public void TryCreate_january_after_year_boundary_is_self_contained()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2027, 1, "ZAR", out var period, out var error), error);
        Assert.Equal(new DateOnly(2027, 1, 1), period.StartInclusive);
        Assert.Equal(new DateOnly(2027, 2, 1), period.EndExclusive);
    }

    // --- Lifecycle classification (half-open) ---

    [Theory]
    [InlineData(2026, 7, 1, BudgetPeriodState.Current)]  // start inclusive
    [InlineData(2026, 7, 15, BudgetPeriodState.Current)]
    [InlineData(2026, 7, 31, BudgetPeriodState.Current)]
    [InlineData(2026, 8, 1, BudgetPeriodState.Closed)]   // end exclusive
    [InlineData(2026, 8, 2, BudgetPeriodState.Closed)]
    [InlineData(2026, 6, 30, BudgetPeriodState.Future)]
    [InlineData(2025, 1, 1, BudgetPeriodState.Future)]
    public void Classify_start_inclusive_end_exclusive_lifecycle(int year, int month, int day, BudgetPeriodState expected)
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var period, out var error), error);
        var state = BudgetPeriodResolver.Classify(period, new DateOnly(year, month, day));
        Assert.Equal(expected, state);
    }

    [Fact]
    public void Classify_same_period_changes_with_trusted_today_not_stored_state()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var period, out _), "create");
        Assert.Equal(BudgetPeriodState.Future, BudgetPeriodResolver.Classify(period, new DateOnly(2026, 6, 1)));
        Assert.Equal(BudgetPeriodState.Current, BudgetPeriodResolver.Classify(period, new DateOnly(2026, 7, 1)));
        Assert.Equal(BudgetPeriodState.Closed, BudgetPeriodResolver.Classify(period, new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void Resolve_captures_time_provider_local_calendar_date_not_caller_state()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 23, 59, 59, TimeSpan.FromHours(2)));
        Assert.True(
            BudgetPeriodResolver.Resolve(2026, 7, "ZAR", clock, out var period, out var state, out var error),
            error);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartInclusive);
        Assert.Equal(BudgetPeriodState.Current, state);
    }

    // --- Equality / explicit input stability ---

    [Fact]
    public void Equivalent_explicit_inputs_produce_equal_periods_and_identical_bounds()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var a, out _), "a");
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var b, out _), "b");
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.StartInclusive, b.StartInclusive);
        Assert.Equal(a.EndExclusive, b.EndExclusive);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_month_or_year_are_not_equal()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var july, out _), "july");
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 8, "ZAR", out var august, out _), "august");
        Assert.True(BudgetPeriodResolver.TryCreate(2025, 7, "ZAR", out var prior, out _), "prior");
        Assert.NotEqual(july, august);
        Assert.NotEqual(july, prior);
    }

    [Fact]
    public void TryCreate_from_wire_input_matches_explicit_year_month_currency()
    {
        var input = new BudgetPeriodInput(2026, 7, "ZAR");
        Assert.True(BudgetPeriodResolver.TryCreate(input, out var fromInput, out var error), error);
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 7, "ZAR", out var explicit_, out _), "explicit");
        Assert.Equal(explicit_, fromInput);
    }

    [Fact]
    public void TryCreate_boundaries_use_invariant_culture_and_ignore_caller_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.True(BudgetPeriodResolver.TryCreate(2026, 12, "ZAR", out var period, out var error), error);
            Assert.Equal("2026-12-01", period.FormatStartInclusive());
            Assert.Equal("2027-01-01", period.FormatEndExclusive());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // --- Validation failures (stable error before any state access) ---

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    [InlineData(100)]
    public void TryCreate_rejects_invalid_month(int month)
    {
        Assert.False(BudgetPeriodResolver.TryCreate(2026, month, "ZAR", out var period, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zar")]
    [InlineData("USD")]
    [InlineData("Zar")]
    [InlineData("ZAR ")]
    public void TryCreate_rejects_non_zar_or_omitted_currency(string? currency)
    {
        Assert.False(BudgetPeriodResolver.TryCreate(2026, 7, currency, out var period, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000)]
    public void TryCreate_rejects_year_outside_reasonable_calendar_range(int year)
    {
        Assert.False(BudgetPeriodResolver.TryCreate(year, 1, "ZAR", out var period, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
    }

    [Fact]
    public void TryCreate_rejects_overflow_at_dateonly_calendar_ceiling()
    {
        Assert.False(BudgetPeriodResolver.TryCreate(9999, 12, "ZAR", out var period, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
    }

    [Fact]
    public void TryCreate_rejects_omitted_wire_period_before_state_access()
    {
        Assert.False(BudgetPeriodResolver.TryCreate(null, out var period, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
    }

    [Fact]
    public void Resolve_rejects_invalid_input_without_producing_state()
    {
        Assert.False(
            BudgetPeriodResolver.Resolve(2026, 0, "ZAR", new DateOnly(2026, 7, 1), out var period, out var state, out var error));
        Assert.Equal(BudgetErrors.InvalidPeriod, error);
        Assert.Equal(default, period);
        Assert.Equal(default, state);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "TestZone",
            now.Offset,
            "TestZone",
            "TestZone");
    }
}
