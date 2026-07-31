using System.Globalization;
using Tally.Domain.Classify.Normalization;

namespace Tally.Domain.Classify.Rules;

/// <summary>Field registry entry for the closed classification_v1 grammar.</summary>
public sealed record FieldDescriptor(
    string FieldKey,
    RuleConditionValueType ValueType,
    IReadOnlyList<string> AllowedPredicates,
    int MaxValueLength);

/// <summary>Predicate registry entry for the closed classification_v1 grammar.</summary>
public sealed record PredicateDescriptor(
    string PredicateKind,
    RuleConditionValueType OperandType,
    RulePredicateCardinality Cardinality);

/// <summary>
/// Finite code-owned field/predicate registry for CLASSIFY v1 (DM-CLASSIFY-RULE-VOCABULARY).
/// AND-only composition; no OR/NOT/priority/regex/fuzzy/wildcard/script/plugin/model scores.
/// </summary>
public static class ClassificationRuleVocabulary
{
    public const string DescriptionNormalized = "description.normalized";
    public const string AccountId = "account.id";
    public const string AmountDirection = "amount.direction";
    public const string AmountAbsoluteMinor = "amount.absolute_minor";

    public const string EqualsPredicate = "equals";
    public const string StartsWithPredicate = "starts_with";
    public const string ContainsTokenSequencePredicate = "contains_token_sequence";
    public const string BetweenInclusivePredicate = "between_inclusive";

    public const string DirectionInflow = "inflow";
    public const string DirectionOutflow = "outflow";

    public static NormalizationDescriptor Normalization => NormalizationDescriptor.V1;

    public static IReadOnlyList<FieldDescriptor> Fields { get; } =
    [
        new(DescriptionNormalized, RuleConditionValueType.Text,
            [EqualsPredicate, StartsWithPredicate, ContainsTokenSequencePredicate],
            MaxValueLength: NormalizerV1.MaxInputLength),
        new(AccountId, RuleConditionValueType.Text, [EqualsPredicate], MaxValueLength: 128),
        new(AmountDirection, RuleConditionValueType.EnumDirection, [EqualsPredicate], MaxValueLength: 16),
        new(AmountAbsoluteMinor, RuleConditionValueType.AbsoluteMinor,
            [EqualsPredicate, BetweenInclusivePredicate], MaxValueLength: 0)
    ];

    public static IReadOnlyList<PredicateDescriptor> Predicates { get; } =
    [
        new(EqualsPredicate, RuleConditionValueType.Text, RulePredicateCardinality.UnaryValue),
        new(StartsWithPredicate, RuleConditionValueType.Text, RulePredicateCardinality.UnaryValue),
        new(ContainsTokenSequencePredicate, RuleConditionValueType.Text, RulePredicateCardinality.UnaryValue),
        new(BetweenInclusivePredicate, RuleConditionValueType.AbsoluteMinor, RulePredicateCardinality.RangeInclusive)
    ];

    public static FieldDescriptor? FindField(string fieldKey) =>
        Fields.FirstOrDefault(field => string.Equals(field.FieldKey, fieldKey, StringComparison.Ordinal));

    public static bool IsKnownField(string fieldKey) => FindField(fieldKey) is not null;

    public static bool IsPredicateAllowed(string fieldKey, string predicateKind)
    {
        var field = FindField(fieldKey);
        return field is not null
            && field.AllowedPredicates.Contains(predicateKind, StringComparer.Ordinal);
    }

    /// <summary>
    /// Validate and construct a canonical <see cref="RuleCondition"/> from wire-shaped inputs.
    /// Text operands for description.normalized are normalized via <see cref="NormalizerV1"/>.
    /// </summary>
    public static bool TryCreateCondition(
        int ordinal,
        string fieldKey,
        string predicateKind,
        string? valueText,
        long? valueMinorMin,
        long? valueMinorMax,
        string? enumValue,
        out RuleCondition? condition,
        out RuleConditionValidationError? error)
    {
        condition = null;
        error = null;

        if (ordinal < 0)
        {
            error = new RuleConditionValidationError("ordinal", RuleVocabularyErrors.InvalidOrdinal);
            return false;
        }

        var field = FindField(fieldKey);
        if (field is null)
        {
            error = new RuleConditionValidationError("fieldKey", RuleVocabularyErrors.UnknownField);
            return false;
        }

        if (!field.AllowedPredicates.Contains(predicateKind, StringComparer.Ordinal))
        {
            // Distinguish unknown global predicates from field-disallowed ones.
            var knownPredicate = Predicates.Any(p => string.Equals(p.PredicateKind, predicateKind, StringComparison.Ordinal));
            error = new RuleConditionValidationError(
                "predicateKind",
                knownPredicate ? RuleVocabularyErrors.PredicateNotAllowed : RuleVocabularyErrors.UnknownPredicate);
            return false;
        }

        return field.ValueType switch
        {
            RuleConditionValueType.Text => TryCreateTextCondition(
                ordinal, field, predicateKind, valueText, out condition, out error),
            RuleConditionValueType.EnumDirection => TryCreateDirectionCondition(
                ordinal, fieldKey, predicateKind, enumValue, valueText, out condition, out error),
            RuleConditionValueType.AbsoluteMinor => TryCreateMinorCondition(
                ordinal, fieldKey, predicateKind, valueMinorMin, valueMinorMax, out condition, out error),
            _ => Fail(out condition, out error, "fieldKey", RuleVocabularyErrors.UnknownField)
        };
    }

    /// <summary>
    /// Validate a non-empty AND rule: at least one condition, unique ordinals, each condition valid.
    /// </summary>
    public static bool TryValidateRule(
        IReadOnlyList<(int Ordinal, string FieldKey, string PredicateKind, string? ValueText, long? ValueMinorMin, long? ValueMinorMax, string? EnumValue)> conditions,
        out IReadOnlyList<RuleCondition> canonical,
        out RuleConditionValidationError? error)
    {
        canonical = Array.Empty<RuleCondition>();
        error = null;

        if (conditions is null || conditions.Count == 0)
        {
            error = new RuleConditionValidationError("conditions", RuleVocabularyErrors.EmptyRule);
            return false;
        }

        var ordinals = new HashSet<int>();
        var built = new List<RuleCondition>(conditions.Count);
        foreach (var item in conditions.OrderBy(c => c.Ordinal).ThenBy(c => c.FieldKey, StringComparer.Ordinal))
        {
            if (!ordinals.Add(item.Ordinal))
            {
                error = new RuleConditionValidationError("ordinal", RuleVocabularyErrors.DuplicateOrdinal);
                return false;
            }

            if (!TryCreateCondition(
                    item.Ordinal,
                    item.FieldKey,
                    item.PredicateKind,
                    item.ValueText,
                    item.ValueMinorMin,
                    item.ValueMinorMax,
                    item.EnumValue,
                    out var condition,
                    out error))
            {
                return false;
            }

            built.Add(condition!);
        }

        canonical = built;
        return true;
    }

    /// <summary>Byte-stable catalogue hash over field and predicate registry + normalization descriptor.</summary>
    public static string CatalogueFingerprint()
    {
        var payload = string.Join(
            '|',
            Fields.Select(f => f.FieldKey + ":" + string.Join(",", f.AllowedPredicates) + ":" + f.MaxValueLength.ToString(CultureInfo.InvariantCulture))
                .Concat(Predicates.Select(p => p.PredicateKind + ":" + p.OperandType + ":" + p.Cardinality))
                .Append(Normalization.ToCanonicalJson()));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private static bool TryCreateTextCondition(
        int ordinal,
        FieldDescriptor field,
        string predicateKind,
        string? valueText,
        out RuleCondition? condition,
        out RuleConditionValidationError? error)
    {
        condition = null;
        error = null;
        if (string.IsNullOrWhiteSpace(valueText))
        {
            error = new RuleConditionValidationError("valueText", RuleVocabularyErrors.InvalidValue);
            return false;
        }

        string canonicalText;
        if (string.Equals(field.FieldKey, DescriptionNormalized, StringComparison.Ordinal))
        {
            if (!NormalizerV1.TryNormalize(valueText, out canonicalText, out var normalizeError))
            {
                error = new RuleConditionValidationError("valueText", normalizeError ?? RuleVocabularyErrors.ValueTooLong);
                return false;
            }

            if (canonicalText.Length == 0)
            {
                error = new RuleConditionValidationError("valueText", RuleVocabularyErrors.InvalidValue);
                return false;
            }
        }
        else
        {
            canonicalText = valueText.Trim();
            if (canonicalText.Length == 0 || canonicalText.Length > field.MaxValueLength)
            {
                error = new RuleConditionValidationError(
                    "valueText",
                    canonicalText.Length > field.MaxValueLength
                        ? RuleVocabularyErrors.ValueTooLong
                        : RuleVocabularyErrors.InvalidValue);
                return false;
            }
        }

        if (string.Equals(predicateKind, ContainsTokenSequencePredicate, StringComparison.Ordinal)
            && NormalizerV1.Tokenize(canonicalText).Count == 0)
        {
            error = new RuleConditionValidationError("valueText", RuleVocabularyErrors.InvalidValue);
            return false;
        }

        condition = RuleCondition.Create(ordinal, field.FieldKey, predicateKind, valueText: canonicalText);
        return true;
    }

    private static bool TryCreateDirectionCondition(
        int ordinal,
        string fieldKey,
        string predicateKind,
        string? enumValue,
        string? valueText,
        out RuleCondition? condition,
        out RuleConditionValidationError? error)
    {
        condition = null;
        error = null;
        var raw = enumValue ?? valueText;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = new RuleConditionValidationError("enumValue", RuleVocabularyErrors.InvalidValue);
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is not (DirectionInflow or DirectionOutflow))
        {
            error = new RuleConditionValidationError("enumValue", RuleVocabularyErrors.InvalidValue);
            return false;
        }

        if (!string.Equals(predicateKind, EqualsPredicate, StringComparison.Ordinal))
        {
            error = new RuleConditionValidationError("predicateKind", RuleVocabularyErrors.PredicateNotAllowed);
            return false;
        }

        condition = RuleCondition.Create(ordinal, fieldKey, predicateKind, enumValue: normalized);
        return true;
    }

    private static bool TryCreateMinorCondition(
        int ordinal,
        string fieldKey,
        string predicateKind,
        long? valueMinorMin,
        long? valueMinorMax,
        out RuleCondition? condition,
        out RuleConditionValidationError? error)
    {
        condition = null;
        error = null;

        if (string.Equals(predicateKind, EqualsPredicate, StringComparison.Ordinal))
        {
            if (valueMinorMin is null || valueMinorMax is not null || valueMinorMin < 0)
            {
                error = new RuleConditionValidationError("valueMinorMin", RuleVocabularyErrors.InvalidMinorRange);
                return false;
            }

            condition = RuleCondition.Create(ordinal, fieldKey, predicateKind, valueMinorMin: valueMinorMin, valueMinorMax: valueMinorMin);
            return true;
        }

        if (string.Equals(predicateKind, BetweenInclusivePredicate, StringComparison.Ordinal))
        {
            if (valueMinorMin is null || valueMinorMax is null
                || valueMinorMin < 0 || valueMinorMax < 0
                || valueMinorMin > valueMinorMax)
            {
                error = new RuleConditionValidationError("valueMinorMin", RuleVocabularyErrors.InvalidMinorRange);
                return false;
            }

            condition = RuleCondition.Create(
                ordinal, fieldKey, predicateKind, valueMinorMin: valueMinorMin, valueMinorMax: valueMinorMax);
            return true;
        }

        error = new RuleConditionValidationError("predicateKind", RuleVocabularyErrors.PredicateNotAllowed);
        return false;
    }

    private static bool Fail(
        out RuleCondition? condition,
        out RuleConditionValidationError? error,
        string field,
        string code)
    {
        condition = null;
        error = new RuleConditionValidationError(field, code);
        return false;
    }
}
