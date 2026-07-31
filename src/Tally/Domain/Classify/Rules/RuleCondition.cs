using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Domain.Classify.Normalization;

namespace Tally.Domain.Classify.Rules;

/// <summary>Value type for a field's operand payload.</summary>
public enum RuleConditionValueType
{
    Text,
    EnumDirection,
    AbsoluteMinor
}

/// <summary>Operand cardinality for a predicate.</summary>
public enum RulePredicateCardinality
{
    UnaryValue,
    RangeInclusive
}

/// <summary>
/// Canonical typed AND-condition (DM-CLASSIFY-RULE-VOCABULARY). Immutable value with byte-stable hash.
/// </summary>
public sealed class RuleCondition : IEquatable<RuleCondition>
{
    private RuleCondition(
        string conditionId,
        int ordinal,
        string fieldKey,
        string predicateKind,
        string? valueText,
        long? valueMinorMin,
        long? valueMinorMax,
        string? enumValue)
    {
        ConditionId = conditionId;
        Ordinal = ordinal;
        FieldKey = fieldKey;
        PredicateKind = predicateKind;
        ValueText = valueText;
        ValueMinorMin = valueMinorMin;
        ValueMinorMax = valueMinorMax;
        EnumValue = enumValue;
        CanonicalHash = ComputeHash();
    }

    public string ConditionId { get; }
    public int Ordinal { get; }
    public string FieldKey { get; }
    public string PredicateKind { get; }
    public string? ValueText { get; }
    public long? ValueMinorMin { get; }
    public long? ValueMinorMax { get; }
    public string? EnumValue { get; }

    /// <summary>SHA-256 hex of <see cref="ToCanonicalJson"/> (culture-invariant).</summary>
    public string CanonicalHash { get; }

    public static RuleCondition Create(
        int ordinal,
        string fieldKey,
        string predicateKind,
        string? valueText = null,
        long? valueMinorMin = null,
        long? valueMinorMax = null,
        string? enumValue = null)
    {
        var conditionId = ComputeConditionId(ordinal, fieldKey, predicateKind, valueText, valueMinorMin, valueMinorMax, enumValue);
        return new RuleCondition(conditionId, ordinal, fieldKey, predicateKind, valueText, valueMinorMin, valueMinorMax, enumValue);
    }

    /// <summary>Byte-stable canonical JSON (sorted keys, invariant numbers, explicit nulls).</summary>
    public string ToCanonicalJson()
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        AppendString(sb, "conditionId", ConditionId);
        sb.Append(',');
        AppendString(sb, "enumValue", EnumValue);
        sb.Append(',');
        AppendString(sb, "fieldKey", FieldKey);
        sb.Append(',');
        AppendString(sb, "predicateKind", PredicateKind);
        sb.Append(',');
        AppendInt(sb, "ordinal", Ordinal);
        sb.Append(',');
        AppendLong(sb, "valueMinorMax", ValueMinorMax);
        sb.Append(',');
        AppendLong(sb, "valueMinorMin", ValueMinorMin);
        sb.Append(',');
        AppendString(sb, "valueText", ValueText);
        sb.Append('}');
        return sb.ToString();
    }

    public bool Equals(RuleCondition? other) =>
        other is not null && string.Equals(CanonicalHash, other.CanonicalHash, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RuleCondition other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalHash);

    private string ComputeHash() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())));

    private static string ComputeConditionId(
        int ordinal,
        string fieldKey,
        string predicateKind,
        string? valueText,
        long? valueMinorMin,
        long? valueMinorMax,
        string? enumValue)
    {
        // Deterministic id from content (not a random ULID) so hashes stay stable without storage.
        var payload = string.Concat(
            ordinal.ToString(CultureInfo.InvariantCulture), "|",
            fieldKey, "|",
            predicateKind, "|",
            valueText ?? "", "|",
            valueMinorMin?.ToString(CultureInfo.InvariantCulture) ?? "", "|",
            valueMinorMax?.ToString(CultureInfo.InvariantCulture) ?? "", "|",
            enumValue ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
    }

    private static void AppendString(StringBuilder sb, string name, string? value)
    {
        sb.Append('"').Append(name).Append("\":");
        if (value is null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }

        sb.Append('"');
    }

    private static void AppendInt(StringBuilder sb, string name, int value)
    {
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendLong(StringBuilder sb, string name, long? value)
    {
        sb.Append('"').Append(name).Append("\":");
        if (value is null) sb.Append("null");
        else sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Stable field-level validation failure for rule authoring.</summary>
public sealed record RuleConditionValidationError(string Field, string Code);
