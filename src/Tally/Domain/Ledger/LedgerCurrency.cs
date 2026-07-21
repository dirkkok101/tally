namespace Tally.Domain.Ledger;

public readonly record struct LedgerCurrency
{
    public const string UnsupportedCurrencyError = "currency.unsupported";
    public static readonly LedgerCurrency Zar = new("ZAR");

    private LedgerCurrency(string code) => Code = code;

    public string Code { get; }

    public static bool TryParse(string? value, out LedgerCurrency currency, out string? error)
    {
        if (value == "ZAR")
        {
            currency = Zar;
            error = null;
            return true;
        }

        currency = default;
        error = UnsupportedCurrencyError;
        return false;
    }

    public override string ToString() => Code ?? string.Empty;
}
