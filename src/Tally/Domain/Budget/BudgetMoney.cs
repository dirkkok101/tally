namespace Tally.Domain.Budget;

/// <summary>
/// Exact decimal money parsing for LEDGER actual contribution strings consumed by BUDGET.
/// BUDGET-owned so Features never imports private LEDGER domain types (public-composition boundary).
/// </summary>
public readonly record struct BudgetMoney
{
    public const string InvalidAmountError = "amount.invalid";

    private BudgetMoney(long minorUnits) => MinorUnits = minorUnits;

    public long MinorUnits { get; }

    public static BudgetMoney FromMinorUnits(long minorUnits) => new(minorUnits);

    public static bool TryParse(string? value, out BudgetMoney money, out string? error)
    {
        money = default;
        error = InvalidAmountError;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var negative = value[0] == '-';
        var start = negative ? 1 : 0;
        if (start == value.Length
            || value[0] == '+'
            || (value[start] == '0' && value.Length > start + 1 && value[start + 1] != '.'))
        {
            return false;
        }

        var decimalIndex = value.IndexOf('.', start);
        if (decimalIndex >= 0 && (decimalIndex != value.Length - 3 || decimalIndex == start))
        {
            return false;
        }

        var integralEnd = decimalIndex < 0 ? value.Length : decimalIndex;
        if (integralEnd == start)
        {
            return false;
        }

        ulong absolute = 0;
        try
        {
            for (var index = start; index < integralEnd; index++)
            {
                var digit = value[index] - '0';
                if (digit is < 0 or > 9)
                {
                    return false;
                }

                absolute = checked(absolute * 10 + (ulong)digit);
            }

            if (decimalIndex >= 0)
            {
                for (var index = decimalIndex + 1; index < value.Length; index++)
                {
                    var digit = value[index] - '0';
                    if (digit is < 0 or > 9)
                    {
                        return false;
                    }

                    absolute = checked(absolute * 10 + (ulong)digit);
                }
            }
            else
            {
                absolute = checked(absolute * 100);
            }
        }
        catch (OverflowException)
        {
            // Try contract: values beyond ulong accumulation are invalid, never thrown.
            return false;
        }

        if (absolute > long.MaxValue)
        {
            return false;
        }

        var signed = (long)absolute;
        if (negative)
        {
            signed = checked(-signed);
        }

        money = new BudgetMoney(signed);
        error = null;
        return true;
    }
}
