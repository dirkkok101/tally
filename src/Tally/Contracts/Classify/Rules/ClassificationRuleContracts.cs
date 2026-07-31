using System.Text.Json.Serialization;

namespace Tally.Contracts.Classify.Rules;

/// <summary>Closed rule field keys for classification_v1 (DM-CLASSIFY-RULE-VOCABULARY).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassificationRuleFieldKey>))]
public enum ClassificationRuleFieldKey
{
    [JsonStringEnumMemberName("description.normalized")]
    DescriptionNormalized,

    [JsonStringEnumMemberName("account.id")]
    AccountId,

    [JsonStringEnumMemberName("amount.direction")]
    AmountDirection,

    [JsonStringEnumMemberName("amount.absolute_minor")]
    AmountAbsoluteMinor
}

/// <summary>Closed predicates allowed by the classification_v1 registry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassificationRulePredicateKind>))]
public enum ClassificationRulePredicateKind
{
    [JsonStringEnumMemberName("equals")]
    Equals,

    [JsonStringEnumMemberName("starts_with")]
    StartsWith,

    [JsonStringEnumMemberName("contains_token_sequence")]
    ContainsTokenSequence,

    [JsonStringEnumMemberName("between_inclusive")]
    BetweenInclusive
}

/// <summary>Closed amount direction for amount.direction equals predicates.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassificationAmountDirectionValue>))]
public enum ClassificationAmountDirectionValue
{
    [JsonStringEnumMemberName("inflow")]
    Inflow,

    [JsonStringEnumMemberName("outflow")]
    Outflow
}

/// <summary>Wire contract for one AND-composed rule condition (no OR/NOT/regex in v1).</summary>
public sealed record ClassificationRuleConditionInput(
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] ClassificationRuleFieldKey FieldKey,
    [property: JsonRequired] ClassificationRulePredicateKind PredicateKind,
    string? ValueText = null,
    long? ValueMinorMin = null,
    long? ValueMinorMax = null,
    ClassificationAmountDirectionValue? EnumValue = null);

/// <summary>Immutable draft/active rule version payload on the wire.</summary>
public sealed record ClassificationRuleVersionDetail(
    [property: JsonRequired] string RuleId,
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] IReadOnlyList<ClassificationRuleConditionInput> Conditions,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] bool BroadApplyAllowed);

/// <summary>Normalization version descriptor published for discovery.</summary>
public sealed record ClassificationNormalizationDescriptor(
    [property: JsonRequired] string Version,
    [property: JsonRequired] string UnicodeForm,
    [property: JsonRequired] bool CaseFold,
    [property: JsonRequired] string PunctuationPolicy,
    [property: JsonRequired] string WhitespacePolicy,
    [property: JsonRequired] bool PreservesDigits,
    [property: JsonRequired] bool PreservesTokenOrder,
    [property: JsonRequired] int MaxInputLength);

/// <summary>v1 normalization identity (design classification_v1).</summary>
public static class ClassificationNormalizationVersions
{
    public const string V1 = "normalization_v1";
}
