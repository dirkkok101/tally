using Tally.Contracts.Ledger.Accounts;

namespace Tally.Domain.Ledger.Accounts;

public sealed record AccountDefinition(
    string InstitutionName,
    string DisplayName,
    AccountType AccountType,
    AccountClass AccountClass,
    string MaskedIdentifier,
    string CurrencyCode)
{
    public const string InvalidError = "validation.invalid_input";
    public const string TypeUnsupportedError = "LEDGER-ACCOUNT-TYPE-UNSUPPORTED";
    public const string CurrencyUnsupportedError = "LEDGER-CURRENCY-UNSUPPORTED";

    public static bool TryCreate(CreateAccountInput input, out AccountDefinition? account, out string? error)
    {
        account = null;
        if (!Enum.IsDefined(input.AccountType))
        {
            error = TypeUnsupportedError;
            return false;
        }

        if (!LedgerCurrency.TryParse(input.CurrencyCode, out var currency, out _))
        {
            error = CurrencyUnsupportedError;
            return false;
        }

        if (!TryText(input.InstitutionName, 128, out var institution)
            || !TryText(input.DisplayName, 128, out var displayName)
            || !TryMaskedIdentifier(input.MaskedIdentifier, out var maskedIdentifier))
        {
            error = InvalidError;
            return false;
        }

        account = new(institution, displayName, input.AccountType, ClassFor(input.AccountType), maskedIdentifier, currency.Code);
        error = null;
        return true;
    }

    public static bool TryDisplayName(string? value, out string normalized) => TryText(value, 128, out normalized);
    public static bool TryReason(string? value, out string normalized) => TryText(value, 512, out normalized);
    public static bool TryInstitutionFilter(string? value, out string? normalized)
    {
        if (value is null)
        {
            normalized = null;
            return true;
        }

        var valid = TryText(value, 128, out var text);
        normalized = valid ? text : null;
        return valid;
    }

    public static AccountClass ClassFor(AccountType accountType) => accountType switch
    {
        AccountType.Cheque or AccountType.Savings or AccountType.OtherAsset => AccountClass.Asset,
        AccountType.CreditCard or AccountType.OtherLiability => AccountClass.Liability,
        _ => throw new ArgumentOutOfRangeException(nameof(accountType))
    };

    private static bool TryText(string? value, int maximumLength, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximumLength
            && normalized.All(character => !char.IsControl(character));
    }

    private static bool TryMaskedIdentifier(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 2 or > 32
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '*' or '•' or '-' or ' '))) return false;
        var digitCount = normalized.Count(char.IsAsciiDigit);
        return digitCount is >= 1 and <= 4 && !normalized.All(char.IsAsciiDigit);
    }
}
