using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Domain.Classify.Normalization;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-VOCABULARY / bd-3gmq
/// Golden normalization proofs for NormalizerV1 (NFKC, case fold, punctuation, whitespace).
/// </summary>
public sealed class NormalizerV1Tests
{
    [Fact]
    public void Descriptor_is_normalization_v1_with_nfkc_and_preservation_flags()
    {
        var d = NormalizerV1.Descriptor;
        Assert.Equal("normalization_v1", d.Version);
        Assert.Equal("NFKC", d.UnicodeForm);
        Assert.True(d.CaseFold);
        Assert.Equal("boundary", d.PunctuationPolicy);
        Assert.Equal("collapse", d.WhitespacePolicy);
        Assert.True(d.PreservesDigits);
        Assert.True(d.PreservesTokenOrder);
        Assert.Equal(2048, d.MaxInputLength);
    }

    [Fact]
    public void Applies_invariant_case_folding()
    {
        AssertNormalized("Coffee SHOP", "coffee shop");
        AssertNormalized("MiXeD CaSe 123", "mixed case 123");
    }

    [Fact]
    public void Applies_unicode_nfkc_compatibility()
    {
        // Fullwidth digits and letters collapse via NFKC then lower.
        AssertNormalized("ＡＢＣ１２３", "abc123");
        // Compatibility ligature ﬁ → fi
        AssertNormalized("ﬁle", "file");
    }

    [Fact]
    public void Punctuation_becomes_token_boundaries_not_removed_content()
    {
        AssertNormalized("ACME,Inc.", "acme inc");
        AssertNormalized("foo-bar_baz", "foo bar baz");
        AssertNormalized("hello!!!world", "hello world");
    }

    [Fact]
    public void Whitespace_collapses_and_trims()
    {
        AssertNormalized("  multi   space\t\nvalue  ", "multi space value");
    }

    [Fact]
    public void Preserves_digits_and_token_order()
    {
        AssertNormalized("INV-2024-0099 REF 42", "inv 2024 0099 ref 42");
        AssertNormalized("A1 B2 C3", "a1 b2 c3");
    }

    [Fact]
    public void Does_not_remove_stop_words_or_reference_tokens()
    {
        AssertNormalized("the payment to the merchant for the order", "the payment to the merchant for the order");
    }

    [Fact]
    public void Null_and_empty_normalize_to_empty()
    {
        Assert.True(NormalizerV1.TryNormalize(null, out var n1, out _));
        Assert.Equal(string.Empty, n1);
        Assert.True(NormalizerV1.TryNormalize("", out var n2, out _));
        Assert.Equal(string.Empty, n2);
        Assert.True(NormalizerV1.TryNormalize("   ", out var n3, out _));
        Assert.Equal(string.Empty, n3);
    }

    [Fact]
    public void Rejects_over_length_input_without_truncation()
    {
        var tooLong = new string('a', NormalizerV1.MaxInputLength + 1);
        Assert.False(NormalizerV1.TryNormalize(tooLong, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.Equal(RuleVocabularyErrors.ValueTooLong, error);
    }

    [Fact]
    public void Accepts_exact_max_length_input()
    {
        var exact = new string('b', NormalizerV1.MaxInputLength);
        Assert.True(NormalizerV1.TryNormalize(exact, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(exact, normalized);
    }

    [Fact]
    public void Tokenize_splits_on_collapsed_spaces()
    {
        Assert.Equal(["acme", "inc", "42"], NormalizerV1.Tokenize("acme inc 42"));
        Assert.Empty(NormalizerV1.Tokenize(""));
    }

    [Fact]
    public void Result_is_stable_across_cultures()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(NormalizerV1.TryNormalize("PAYMENT REF 99", out var tr, out _));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.True(NormalizerV1.TryNormalize("PAYMENT REF 99", out var en, out _));
            Assert.Equal(tr, en);
            Assert.Equal("payment ref 99", en);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Descriptor_canonical_json_and_hash_are_byte_stable()
    {
        var first = NormalizationDescriptor.V1.ToCanonicalJson();
        var second = NormalizationDescriptor.V1.ToCanonicalJson();
        Assert.Equal(first, second);
        var h1 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first)));
        var h2 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(second)));
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void Mixed_scripts_and_marks_normalize_deterministically()
    {
        AssertNormalized("Café #1", "café 1");
        AssertNormalized("naïve--pay", "naïve pay");
    }

    private static void AssertNormalized(string input, string expected)
    {
        Assert.True(NormalizerV1.TryNormalize(input, out var actual, out var error), error);
        Assert.Null(error);
        Assert.Equal(expected, actual);
    }
}
