namespace Tally.Domain.Ledger.Dimensions;

public static class PaymentIdentity
{
    public const string InvalidError = "LEDGER-PAYMENT-IDENTITY-INVALID";

    public static bool TryLabel(string? value, out string label) => TryText(value, 128, out label);
    public static bool TryReason(string? value, out string reason) => TryText(value, 512, out reason);

    public static bool TryMaskedSuffix(string? value, out string? suffix)
    {
        suffix = value?.Trim();
        return suffix is null || suffix.Length is >= 1 and <= 4 && suffix.All(char.IsAsciiDigit);
    }

    private static bool TryText(string? value, int maximum, out string text)
    {
        text = value?.Trim() ?? string.Empty;
        return text.Length is > 0 && text.Length <= maximum && text.All(character => !char.IsControl(character));
    }
}
