namespace Tally.Domain.Ledger.Categories;

public static class SpendCategory
{
    public const string InvalidError = "LEDGER-CATEGORY-INVALID";

    public static bool TryName(string? value, out string normalized) => TryText(value, 128, out normalized);
    public static bool TryReason(string? value, out string normalized) => TryText(value, 512, out normalized);

    private static bool TryText(string? value, int maximumLength, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximumLength && normalized.All(character => !char.IsControl(character));
    }
}
