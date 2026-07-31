using System.Globalization;
using System.Text;

namespace Tally.Domain.Classify.Evaluation;

/// <summary>
/// Bounded historical match evidence (DM-CLASSIFY-EVALUATION-OUTCOME).
/// Retains only rule, condition, field, predicate, and normalized-value hash references —
/// never full projection payloads or raw financial description text.
/// </summary>
public sealed class MatchEvidence : IEquatable<MatchEvidence>
{
    public MatchEvidence(
        string ruleVersionId,
        string conditionId,
        string fieldKey,
        string predicateKind,
        string normalizedValueHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicateKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValueHash);
        if (normalizedValueHash.Length != 64)
        {
            throw new ArgumentException(
                "normalized_value_hash must be a 64-character hex SHA-256 digest.",
                nameof(normalizedValueHash));
        }

        RuleVersionId = ruleVersionId;
        ConditionId = conditionId;
        FieldKey = fieldKey;
        PredicateKind = predicateKind;
        NormalizedValueHash = normalizedValueHash;
    }

    public string RuleVersionId { get; }
    public string ConditionId { get; }
    public string FieldKey { get; }
    public string PredicateKind { get; }
    public string NormalizedValueHash { get; }

    /// <summary>Byte-stable canonical JSON for hashing and semantic equality.</summary>
    public string ToCanonicalJson()
    {
        var sb = new StringBuilder(192);
        sb.Append('{');
        AppendString(sb, "conditionId", ConditionId);
        sb.Append(',');
        AppendString(sb, "fieldKey", FieldKey);
        sb.Append(',');
        AppendString(sb, "normalizedValueHash", NormalizedValueHash);
        sb.Append(',');
        AppendString(sb, "predicateKind", PredicateKind);
        sb.Append(',');
        AppendString(sb, "ruleVersionId", RuleVersionId);
        sb.Append('}');
        return sb.ToString();
    }

    public bool Equals(MatchEvidence? other) =>
        other is not null
        && string.Equals(RuleVersionId, other.RuleVersionId, StringComparison.Ordinal)
        && string.Equals(ConditionId, other.ConditionId, StringComparison.Ordinal)
        && string.Equals(FieldKey, other.FieldKey, StringComparison.Ordinal)
        && string.Equals(PredicateKind, other.PredicateKind, StringComparison.Ordinal)
        && string.Equals(NormalizedValueHash, other.NormalizedValueHash, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MatchEvidence other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(RuleVersionId, ConditionId, FieldKey, PredicateKind, NormalizedValueHash);

    private static void AppendString(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":\"");
        foreach (var ch in value)
        {
            if (ch is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        sb.Append('"');
    }
}

/// <summary>Stable sort order for evidence rows: rule version, then condition id, then field.</summary>
public static class MatchEvidenceOrdering
{
    public static IReadOnlyList<MatchEvidence> Order(IEnumerable<MatchEvidence> evidence) =>
        evidence
            .OrderBy(e => e.RuleVersionId, StringComparer.Ordinal)
            .ThenBy(e => e.ConditionId, StringComparer.Ordinal)
            .ThenBy(e => e.FieldKey, StringComparer.Ordinal)
            .ThenBy(e => e.PredicateKind, StringComparer.Ordinal)
            .ToArray();

    public static string CanonicalEvidenceFingerprint(IReadOnlyList<MatchEvidence> evidence) =>
        CanonicalClassificationHasher.HashOrderedLines(
            Order(evidence).Select(e => e.ToCanonicalJson()));
}
