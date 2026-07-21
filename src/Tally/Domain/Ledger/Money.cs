using System.Text.Json;

namespace Tally.Domain.Ledger;

public enum LedgerAccountKind
{
    Asset,
    Liability
}

public readonly record struct Money
{
    public const string InvalidAmountError = "amount.invalid";
    public const string ZeroTransactionAmountError = "amount.zero";

    private Money(long minorUnits) => MinorUnits = minorUnits;

    public long MinorUnits { get; }

    public static Money FromMinorUnits(long minorUnits) => new(minorUnits);

    public static Money FromAccountBalanceMovement(LedgerAccountKind accountKind, long balanceMovementMinor) => accountKind switch
    {
        LedgerAccountKind.Asset => FromMinorUnits(balanceMovementMinor),
        LedgerAccountKind.Liability => FromMinorUnits(checked(-balanceMovementMinor)),
        _ => throw new ArgumentOutOfRangeException(nameof(accountKind))
    };

    public static bool TryParseTransactionAmount(string? value, out Money money, out string? error)
    {
        if (!TryParse(value, out money, out error)) return false;
        if (money.MinorUnits != 0) return true;

        error = ZeroTransactionAmountError;
        return false;
    }

    public static bool TryParseJson(JsonElement value, out Money money, out string? error) =>
        value.ValueKind == JsonValueKind.String
            ? TryParse(value.GetString(), out money, out error)
            : Fail(out money, out error);

    public static bool TryParse(string? value, out Money money, out string? error)
    {
        money = default;
        error = InvalidAmountError;
        if (string.IsNullOrEmpty(value)) return false;

        var negative = value[0] == '-';
        var start = negative ? 1 : 0;
        if (start == value.Length || value[0] == '+' || value[start] == '0' && value.Length > start + 1 && value[start + 1] != '.') return false;

        var decimalIndex = value.IndexOf('.', start);
        if (decimalIndex >= 0 && (decimalIndex != value.Length - 3 || decimalIndex == start)) return false;
        var integralEnd = decimalIndex < 0 ? value.Length : decimalIndex;
        if (integralEnd == start) return false;

        ulong absolute = 0;
        for (var index = start; index < integralEnd; index++)
        {
            if (value[index] is < '0' or > '9') return false;
            if (!TryAppendDigit(ref absolute, value[index] - '0')) return false;
        }

        if (decimalIndex >= 0)
        {
            for (var index = decimalIndex + 1; index < value.Length; index++)
            {
                if (value[index] is < '0' or > '9' || !TryAppendDigit(ref absolute, value[index] - '0')) return false;
            }
        }
        else if (absolute > 92233720368547758UL) return false;
        else absolute *= 100;

        if (absolute == 0 && (negative || decimalIndex >= 0)) return false;
        if (negative)
        {
            if (absolute > 9223372036854775808UL) return false;
            money = FromMinorUnits(absolute == 9223372036854775808UL ? long.MinValue : -(long)absolute);
        }
        else
        {
            if (absolute > long.MaxValue) return false;
            money = FromMinorUnits((long)absolute);
        }

        error = null;
        return true;
    }

    public override string ToString()
    {
        var negative = MinorUnits < 0;
        var absolute = negative ? (ulong)(-(MinorUnits + 1)) + 1 : (ulong)MinorUnits;
        var whole = absolute / 100;
        var cents = absolute % 100;
        return cents == 0
            ? (negative ? "-" : string.Empty) + whole.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Concat(negative ? "-" : string.Empty, whole.ToString(System.Globalization.CultureInfo.InvariantCulture), ".", cents.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryAppendDigit(ref ulong value, int digit)
    {
        if (value > (ulong.MaxValue - (uint)digit) / 10) return false;
        value = value * 10 + (uint)digit;
        return true;
    }

    private static bool Fail(out Money money, out string? error)
    {
        money = default;
        error = InvalidAmountError;
        return false;
    }
}
