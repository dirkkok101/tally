using System.Globalization;

namespace Tally.Domain.Ledger;

public readonly record struct EffectiveDate
{
    public const string InvalidDateError = "date.invalid";
    private const string Format = "yyyy-MM-dd";

    private EffectiveDate(DateOnly value) => Value = value;

    public DateOnly Value { get; }

    public static EffectiveDate Resolve(EffectiveDate transactionDate, EffectiveDate? postingDate) => transactionDate;

    public static bool TryParse(string? value, out EffectiveDate date, out string? error)
    {
        if (value is not null && value.Length == Format.Length && DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = new EffectiveDate(parsed);
            error = null;
            return true;
        }

        date = default;
        error = InvalidDateError;
        return false;
    }

    public override string ToString() => Value.ToString(Format, CultureInfo.InvariantCulture);
}
