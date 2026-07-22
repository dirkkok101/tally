namespace Tally.Domain.Ledger.Dimensions;

public static class SpendPool
{
    public const string InvalidError = "LEDGER-SPEND-POOL-INVALID";

    public static bool TryName(string? value, out string name) => TryText(value, 128, out name);
    public static bool TryReason(string? value, out string reason) => TryText(value, 512, out reason);

    private static bool TryText(string? value, int maximum, out string text)
    {
        text = value?.Trim() ?? string.Empty;
        return text.Length is > 0 && text.Length <= maximum && text.All(character => !char.IsControl(character));
    }
}
