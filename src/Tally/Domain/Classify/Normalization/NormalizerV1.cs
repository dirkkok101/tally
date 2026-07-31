using System.Globalization;
using System.Text;

namespace Tally.Domain.Classify.Normalization;

/// <summary>
/// Pure classification normalization v1 (DD-CLASSIFY-RULE-VOCABULARY):
/// Unicode NFKC, invariant case folding, punctuation-to-boundary, whitespace collapse;
/// preserves digits and token order; does not remove dates, amounts, reference numbers, or stop words.
/// </summary>
public static class NormalizerV1
{
    public static NormalizationDescriptor Descriptor => NormalizationDescriptor.V1;

    public static int MaxInputLength => Descriptor.MaxInputLength;

    /// <summary>
    /// Normalizes owner-visible description text for matching. Returns false when input exceeds
    /// <see cref="MaxInputLength"/> (no silent truncation).
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string? errorCode)
    {
        normalized = string.Empty;
        errorCode = null;

        if (input is null)
        {
            normalized = string.Empty;
            return true;
        }

        if (input.Length > MaxInputLength)
        {
            errorCode = RuleVocabularyErrors.ValueTooLong;
            return false;
        }

        // Culture-invariant NFKC + case fold.
        var folded = input.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var buffer = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                buffer.Append(rune.ToString());
                continue;
            }

            // Punctuation and other non-letters become token boundaries; whitespace collapses later.
            buffer.Append(' ');
        }

        normalized = CollapseWhitespace(buffer.ToString());
        return true;
    }

    /// <summary>Tokenize a normalized string on single spaces (already collapsed).</summary>
    public static IReadOnlyList<string> Tokenize(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<string>();
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string CollapseWhitespace(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts);
    }
}

/// <summary>Stable field-level validation codes for the closed rule vocabulary (domain-local).</summary>
public static class RuleVocabularyErrors
{
    public const string UnknownField = "CLASSIFY-RULE-FIELD-UNKNOWN";
    public const string UnknownPredicate = "CLASSIFY-RULE-PREDICATE-UNKNOWN";
    public const string PredicateNotAllowed = "CLASSIFY-RULE-PREDICATE-NOT-ALLOWED";
    public const string InvalidValue = "CLASSIFY-RULE-VALUE-INVALID";
    public const string ValueTooLong = "CLASSIFY-RULE-VALUE-TOO-LONG";
    public const string InvalidMinorRange = "CLASSIFY-RULE-MINOR-RANGE-INVALID";
    public const string DuplicateOrdinal = "CLASSIFY-RULE-ORDINAL-DUPLICATE";
    public const string DuplicateCondition = "CLASSIFY-RULE-CONDITION-DUPLICATE";
    public const string EmptyRule = "CLASSIFY-RULE-EMPTY";
    public const string InvalidOrdinal = "CLASSIFY-RULE-ORDINAL-INVALID";
}
