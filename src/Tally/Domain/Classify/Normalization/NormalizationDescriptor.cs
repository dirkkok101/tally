namespace Tally.Domain.Classify.Normalization;

/// <summary>
/// Code-owned normalization version descriptor (DM-CLASSIFY-RULE-VOCABULARY).
/// Pure value object — no storage or Ledger dependency.
/// </summary>
public sealed record NormalizationDescriptor(
    string Version,
    string UnicodeForm,
    bool CaseFold,
    string PunctuationPolicy,
    string WhitespacePolicy,
    bool PreservesDigits,
    bool PreservesTokenOrder,
    int MaxInputLength)
{
    /// <summary>Published normalization_v1 identity and policies.</summary>
    public static NormalizationDescriptor V1 { get; } = new(
        Version: "normalization_v1",
        UnicodeForm: "NFKC",
        CaseFold: true,
        PunctuationPolicy: "boundary",
        WhitespacePolicy: "collapse",
        PreservesDigits: true,
        PreservesTokenOrder: true,
        MaxInputLength: 2048);

    /// <summary>Byte-stable canonical UTF-8 JSON for hashing and discovery proofs.</summary>
    public string ToCanonicalJson() =>
        string.Concat(
            "{\"caseFold\":", CaseFold ? "true" : "false",
            ",\"maxInputLength\":", MaxInputLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",\"preservesDigits\":", PreservesDigits ? "true" : "false",
            ",\"preservesTokenOrder\":", PreservesTokenOrder ? "true" : "false",
            ",\"punctuationPolicy\":\"", PunctuationPolicy, "\"",
            ",\"unicodeForm\":\"", UnicodeForm, "\"",
            ",\"version\":\"", Version, "\"",
            ",\"whitespacePolicy\":\"", WhitespacePolicy, "\"}");
}
