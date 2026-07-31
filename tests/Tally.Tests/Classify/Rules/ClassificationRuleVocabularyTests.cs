using System.Globalization;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-VOCABULARY / bd-3gmq
/// Closed grammar: fields, predicates, validation, and canonical condition hashes.
/// </summary>
public sealed class ClassificationRuleVocabularyTests
{
    [Fact]
    public void Registry_exposes_exactly_four_fields_in_designed_order()
    {
        Assert.Equal(
            [
                ClassificationRuleVocabulary.DescriptionNormalized,
                ClassificationRuleVocabulary.AccountId,
                ClassificationRuleVocabulary.AmountDirection,
                ClassificationRuleVocabulary.AmountAbsoluteMinor
            ],
            ClassificationRuleVocabulary.Fields.Select(f => f.FieldKey).ToArray());
    }

    [Fact]
    public void Description_normalized_allows_equals_starts_with_and_token_sequence()
    {
        var field = Assert.Single(
            ClassificationRuleVocabulary.Fields,
            f => f.FieldKey == ClassificationRuleVocabulary.DescriptionNormalized);
        Assert.Equal(
            [
                ClassificationRuleVocabulary.EqualsPredicate,
                ClassificationRuleVocabulary.StartsWithPredicate,
                ClassificationRuleVocabulary.ContainsTokenSequencePredicate
            ],
            field.AllowedPredicates);
    }

    [Fact]
    public void Account_id_allows_only_equals()
    {
        var field = Assert.Single(
            ClassificationRuleVocabulary.Fields,
            f => f.FieldKey == ClassificationRuleVocabulary.AccountId);
        Assert.Equal([ClassificationRuleVocabulary.EqualsPredicate], field.AllowedPredicates);
    }

    [Fact]
    public void Amount_direction_allows_only_equals()
    {
        var field = Assert.Single(
            ClassificationRuleVocabulary.Fields,
            f => f.FieldKey == ClassificationRuleVocabulary.AmountDirection);
        Assert.Equal([ClassificationRuleVocabulary.EqualsPredicate], field.AllowedPredicates);
    }

    [Fact]
    public void Amount_absolute_minor_allows_equals_and_between_inclusive()
    {
        var field = Assert.Single(
            ClassificationRuleVocabulary.Fields,
            f => f.FieldKey == ClassificationRuleVocabulary.AmountAbsoluteMinor);
        Assert.Equal(
            [
                ClassificationRuleVocabulary.EqualsPredicate,
                ClassificationRuleVocabulary.BetweenInclusivePredicate
            ],
            field.AllowedPredicates);
    }

    [Fact]
    public void Forbidden_predicates_are_not_in_registry()
    {
        var kinds = ClassificationRuleVocabulary.Predicates.Select(p => p.PredicateKind).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("regex", kinds);
        Assert.DoesNotContain("fuzzy", kinds);
        Assert.DoesNotContain("wildcard", kinds);
        Assert.DoesNotContain("or", kinds);
        Assert.DoesNotContain("not", kinds);
        Assert.DoesNotContain("contains", kinds); // only contains_token_sequence
        Assert.DoesNotContain("matches", kinds);
    }

    [Fact]
    public void Unknown_field_returns_stable_field_error()
    {
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, "merchant.name", ClassificationRuleVocabulary.EqualsPredicate, "x", null, null, null,
            out _, out var error));
        Assert.Equal("fieldKey", error!.Field);
        Assert.Equal(RuleVocabularyErrors.UnknownField, error.Code);
    }

    [Fact]
    public void Unknown_predicate_returns_stable_error()
    {
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, "regex", "x", null, null, null,
            out _, out var error));
        Assert.Equal(RuleVocabularyErrors.UnknownPredicate, error!.Code);
    }

    [Fact]
    public void Disallowed_predicate_on_field_returns_not_allowed()
    {
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.StartsWithPredicate, "x", null, null, null,
            out _, out var error));
        Assert.Equal(RuleVocabularyErrors.PredicateNotAllowed, error!.Code);
    }

    [Fact]
    public void Description_equals_normalizes_value_text()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.EqualsPredicate,
            "ACME, Inc.",
            null, null, null,
            out var condition,
            out var error));
        Assert.Null(error);
        Assert.Equal("acme inc", condition!.ValueText);
        Assert.Equal(64, condition.CanonicalHash.Length);
    }

    [Fact]
    public void Description_starts_with_and_token_sequence_are_accepted()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.DescriptionNormalized, ClassificationRuleVocabulary.StartsWithPredicate,
            "Pay", null, null, null, out var starts, out _));
        Assert.Equal("pay", starts!.ValueText);

        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            1, ClassificationRuleVocabulary.DescriptionNormalized, ClassificationRuleVocabulary.ContainsTokenSequencePredicate,
            "coffee shop", null, null, null, out var seq, out _));
        Assert.Equal("coffee shop", seq!.ValueText);
    }

    [Fact]
    public void Empty_or_whitespace_text_value_is_invalid()
    {
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate,
            "   ", null, null, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.InvalidValue, error!.Code);
    }

    [Fact]
    public void Over_length_account_id_is_rejected()
    {
        var tooLong = new string('a', 129);
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate,
            tooLong, null, null, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.ValueTooLong, error!.Code);
    }

    [Fact]
    public void Direction_equals_accepts_inflow_and_outflow_only()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountDirection, ClassificationRuleVocabulary.EqualsPredicate,
            null, null, null, "INFLOW", out var inflow, out _));
        Assert.Equal("inflow", inflow!.EnumValue);

        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountDirection, ClassificationRuleVocabulary.EqualsPredicate,
            null, null, null, "sideways", out _, out var error));
        Assert.Equal(RuleVocabularyErrors.InvalidValue, error!.Code);
    }

    [Fact]
    public void Absolute_minor_equals_requires_non_negative_min()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountAbsoluteMinor, ClassificationRuleVocabulary.EqualsPredicate,
            null, 1250, null, null, out var condition, out _));
        Assert.Equal(1250, condition!.ValueMinorMin);
        Assert.Equal(1250, condition.ValueMinorMax);

        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountAbsoluteMinor, ClassificationRuleVocabulary.EqualsPredicate,
            null, -1, null, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.InvalidMinorRange, error!.Code);
    }

    [Fact]
    public void Absolute_minor_between_inclusive_requires_ordered_non_negative_range()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountAbsoluteMinor, ClassificationRuleVocabulary.BetweenInclusivePredicate,
            null, 100, 500, null, out var condition, out _));
        Assert.Equal(100, condition!.ValueMinorMin);
        Assert.Equal(500, condition.ValueMinorMax);

        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AmountAbsoluteMinor, ClassificationRuleVocabulary.BetweenInclusivePredicate,
            null, 500, 100, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.InvalidMinorRange, error!.Code);
    }

    [Fact]
    public void Empty_rule_is_rejected()
    {
        Assert.False(ClassificationRuleVocabulary.TryValidateRule([], out _, out var error));
        Assert.Equal(RuleVocabularyErrors.EmptyRule, error!.Code);
    }

    [Fact]
    public void Duplicate_ordinals_are_rejected()
    {
        Assert.False(ClassificationRuleVocabulary.TryValidateRule(
            [
                (0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, "a", null, null, null),
                (0, ClassificationRuleVocabulary.AmountDirection, ClassificationRuleVocabulary.EqualsPredicate, null, null, null, "outflow")
            ],
            out _,
            out var error));
        Assert.Equal(RuleVocabularyErrors.DuplicateOrdinal, error!.Code);
    }

    [Fact]
    public void Negative_ordinal_is_rejected()
    {
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            -1, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate,
            "a", null, null, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.InvalidOrdinal, error!.Code);
    }

    [Fact]
    public void Valid_and_rule_builds_canonical_conditions_in_ordinal_order()
    {
        Assert.True(ClassificationRuleVocabulary.TryValidateRule(
            [
                (1, ClassificationRuleVocabulary.AmountDirection, ClassificationRuleVocabulary.EqualsPredicate, null, null, null, "outflow"),
                (0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, "acct-1", null, null, null)
            ],
            out var conditions,
            out var error));
        Assert.Null(error);
        Assert.Equal(2, conditions.Count);
        Assert.Equal(0, conditions[0].Ordinal);
        Assert.Equal(ClassificationRuleVocabulary.AccountId, conditions[0].FieldKey);
        Assert.Equal(1, conditions[1].Ordinal);
    }

    [Fact]
    public void Condition_hash_is_byte_stable_across_repeated_construction()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.DescriptionNormalized, ClassificationRuleVocabulary.EqualsPredicate,
            "Shop #1", null, null, null, out var a, out _));
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.DescriptionNormalized, ClassificationRuleVocabulary.EqualsPredicate,
            "Shop #1", null, null, null, out var b, out _));
        Assert.Equal(a!.CanonicalHash, b!.CanonicalHash);
        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Condition_hash_differs_when_value_differs()
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate,
            "a", null, null, null, out var a, out _));
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate,
            "b", null, null, null, out var b, out _));
        Assert.NotEqual(a!.CanonicalHash, b!.CanonicalHash);
    }

    [Fact]
    public void Catalogue_fingerprint_is_stable_across_cultures()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tr = ClassificationRuleVocabulary.CatalogueFingerprint();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var de = ClassificationRuleVocabulary.CatalogueFingerprint();
            Assert.Equal(tr, de);
            Assert.Equal(64, tr.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Normalization_descriptor_matches_domain_v1()
    {
        Assert.Equal("normalization_v1", ClassificationRuleVocabulary.Normalization.Version);
        Assert.Equal(NormalizationDescriptor.V1.ToCanonicalJson(), ClassificationRuleVocabulary.Normalization.ToCanonicalJson());
    }

    [Fact]
    public void Over_length_description_value_is_rejected_by_normalizer_bound()
    {
        var tooLong = new string('x', NormalizerV1.MaxInputLength + 1);
        Assert.False(ClassificationRuleVocabulary.TryCreateCondition(
            0, ClassificationRuleVocabulary.DescriptionNormalized, ClassificationRuleVocabulary.EqualsPredicate,
            tooLong, null, null, null, out _, out var error));
        Assert.Equal(RuleVocabularyErrors.ValueTooLong, error!.Code);
    }
}
