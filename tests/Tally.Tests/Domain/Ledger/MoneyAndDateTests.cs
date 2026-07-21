using System.Text.Json;
using Tally.Domain.Ledger;
using Xunit;

namespace Tally.Tests.Domain.Ledger;

public sealed class MoneyAndDateTests
{
    // TC-LEDGER-EXACT-MONEY-CONFORMANCE / DD-LEDGER-FINANCIAL-REPRESENTATION
    [Theory]
    [InlineData("-123.45", -12345, "-123.45")]
    [InlineData("0", 0, "0")]
    [InlineData("1", 100, "1")]
    [InlineData("1.20", 120, "1.20")]
    [InlineData("92233720368547758.07", long.MaxValue, "92233720368547758.07")]
    [InlineData("-92233720368547758.08", long.MinValue, "-92233720368547758.08")]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_canonical_decimal_round_trips(string text, long minorUnits, string canonical)
    {
        Assert.True(Money.TryParse(text, out var money, out var error), error);
        Assert.Equal(minorUnits, money.MinorUnits);
        Assert.Equal(canonical, money.ToString());
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE / DD-LEDGER-FINANCIAL-REPRESENTATION
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+1.00")]
    [InlineData("01.00")]
    [InlineData("1.")]
    [InlineData(".01")]
    [InlineData("1.2")]
    [InlineData("1.234")]
    [InlineData("1e2")]
    [InlineData("0.00")]
    [InlineData("-0")]
    [InlineData("-0.00")]
    [InlineData("92233720368547758.08")]
    [InlineData("-92233720368547758.09")]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_rejects_lossy_or_noncanonical_lexemes(string text)
    {
        Assert.False(Money.TryParse(text, out _, out var error));
        Assert.Equal(Money.InvalidAmountError, error);
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE / FR-LEDGER-TRANSACTION-RECORDING
    [Theory]
    [InlineData("0")]
    public void FR_LEDGER_TRANSACTION_RECORDING_rejects_zero_transaction_amount(string text)
    {
        Assert.False(Money.TryParseTransactionAmount(text, out _, out var error));
        Assert.Equal(Money.ZeroTransactionAmountError, error);
    }

    // DD-LEDGER-FINANCIAL-REPRESENTATION
    [Theory]
    [InlineData("ZAR", true)]
    [InlineData("zar", false)]
    [InlineData("USD", false)]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_accepts_only_zar(string text, bool accepted)
    {
        Assert.Equal(accepted, LedgerCurrency.TryParse(text, out _, out var error));
        Assert.Equal(accepted ? null : LedgerCurrency.UnsupportedCurrencyError, error);
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE / DD-LEDGER-FINANCIAL-REPRESENTATION
    [Fact]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_rejects_json_numbers_and_accepts_strings()
    {
        using var numeric = JsonDocument.Parse("12.34");
        using var stringValue = JsonDocument.Parse("\"12.34\"");

        Assert.False(Money.TryParseJson(numeric.RootElement, out _, out var numericError));
        Assert.Equal(Money.InvalidAmountError, numericError);
        Assert.True(Money.TryParseJson(stringValue.RootElement, out var money, out var stringError), stringError);
        Assert.Equal("12.34", money.ToString());
    }

    // TC-LEDGER-TRANSACTION-RECORDING-CONTRACT / DD-LEDGER-FINANCIAL-REPRESENTATION
    [Theory]
    [InlineData(LedgerAccountKind.Asset, 25, 25)]
    [InlineData(LedgerAccountKind.Asset, -25, -25)]
    [InlineData(LedgerAccountKind.Liability, 25, -25)]
    [InlineData(LedgerAccountKind.Liability, -25, 25)]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_resolves_owner_economic_position_sign(LedgerAccountKind accountKind, long balanceMovementMinor, long expectedSignedMinor)
    {
        Assert.Equal(expectedSignedMinor, Money.FromAccountBalanceMovement(accountKind, balanceMovementMinor).MinorUnits);
    }

    // DD-LEDGER-FINANCIAL-REPRESENTATION
    [Fact]
    public void DD_LEDGER_FINANCIAL_REPRESENTATION_rejects_an_unknown_account_kind() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.FromAccountBalanceMovement((LedgerAccountKind)99, 25));

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-101)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(101)]
    [InlineData(long.MaxValue)]
    public void TC_LEDGER_EXACT_MONEY_CONFORMANCE_minor_units_round_trip_through_canonical_text(long minorUnits)
    {
        var canonical = Money.FromMinorUnits(minorUnits).ToString();
        Assert.True(Money.TryParse(canonical, out var reparsed, out var error), error);
        Assert.Equal(minorUnits, reparsed.MinorUnits);
    }

    // DM-LEDGER-TRANSACTION-FACT
    [Fact]
    public void DM_LEDGER_TRANSACTION_FACT_keeps_posting_date_distinct_and_uses_transaction_date_as_effective_date()
    {
        Assert.True(EffectiveDate.TryParse("2026-07-21", out var transactionDate, out var transactionError), transactionError);
        Assert.True(EffectiveDate.TryParse("2026-07-22", out var postingDate, out var postingError), postingError);

        Assert.Equal("2026-07-21", transactionDate.ToString());
        Assert.Equal("2026-07-22", postingDate.ToString());
        Assert.Equal(transactionDate, EffectiveDate.Resolve(transactionDate, postingDate));
    }

    // DM-LEDGER-TRANSACTION-FACT
    [Fact]
    public void DM_LEDGER_TRANSACTION_FACT_accepts_a_valid_local_leap_day()
    {
        Assert.True(EffectiveDate.TryParse("2024-02-29", out var date, out var error), error);
        Assert.Equal("2024-02-29", date.ToString());
    }

    // DM-LEDGER-TRANSACTION-FACT
    [Theory]
    [InlineData("2026-2-01")]
    [InlineData("2026-02-1")]
    [InlineData("2026/02/01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-02-01T00:00:00Z")]
    public void DM_LEDGER_TRANSACTION_FACT_rejects_non_local_or_noncanonical_dates(string text)
    {
        Assert.False(EffectiveDate.TryParse(text, out _, out var error));
        Assert.Equal(EffectiveDate.InvalidDateError, error);
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE
    [Fact]
    public void TC_LEDGER_EXACT_MONEY_CONFORMANCE_accumulates_zero_and_detects_overflow()
    {
        Assert.Equal(0, ExactTotals.Zero.NetAccountMovement.MinorUnits);
        var maximum = Money.FromMinorUnits(long.MaxValue);
        Assert.Throws<OverflowException>(() => new ExactTotals(maximum).Add(Money.FromMinorUnits(1)));
        var minimum = Money.FromMinorUnits(long.MinValue);
        Assert.Throws<OverflowException>(() => new ExactTotals(minimum).Add(Money.FromMinorUnits(-1)));
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE
    [Fact]
    public void TC_LEDGER_EXACT_MONEY_CONFORMANCE_generates_and_validates_canonical_ulids()
    {
        var identifier = LedgerId.New();

        Assert.Equal(26, identifier.ToString().Length);
        Assert.True(LedgerId.TryParse(identifier.ToString(), out var parsed, out var error), error);
        Assert.Equal(identifier, parsed);
        Assert.False(LedgerId.TryParse("0000000000000000000000000I", out _, out var invalidError));
        Assert.Equal(LedgerId.InvalidIdentifierError, invalidError);
        Assert.False(LedgerId.TryParse("550e8400-e29b-41d4-a716-446655440000", out _, out var guidError));
        Assert.Equal(LedgerId.InvalidIdentifierError, guidError);
    }

    // TC-LEDGER-EXACT-MONEY-CONFORMANCE
    [Fact]
    public void TC_LEDGER_EXACT_MONEY_CONFORMANCE_generated_ulids_are_unique_and_canonical()
    {
        var identifiers = Enumerable.Range(0, 256).Select(_ => LedgerId.New().ToString()).ToArray();
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identifiers, value => Assert.True(LedgerId.TryParse(value, out _, out _)));
    }
}
