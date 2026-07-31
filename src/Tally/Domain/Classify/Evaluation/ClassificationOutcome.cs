using System.Globalization;
using System.Text;

namespace Tally.Domain.Classify.Evaluation;

/// <summary>Closed outcome partition for one projected transaction (DM-CLASSIFY-EVALUATION-OUTCOME).</summary>
public enum ClassificationOutcomeKind
{
    Suggestion,
    NoSuggestion,
    Conflict,
    Stale
}

/// <summary>
/// One ordered evaluation outcome. Exactly one kind per input ordinal.
/// Conflict and no-suggestion never select a category; suggestion always names one active category
/// and every contributing rule version (no hidden priority winner).
/// </summary>
public sealed class ClassificationOutcome : IEquatable<ClassificationOutcome>
{
    private ClassificationOutcome(
        int ordinal,
        string transactionId,
        ClassificationOutcomeKind kind,
        string? categoryId,
        IReadOnlyList<string> contributingRuleVersionIds,
        IReadOnlyList<MatchEvidence> evidence,
        IReadOnlyList<string> staleDimensions,
        string itemLifecycleFingerprint,
        string safeReason)
    {
        Ordinal = ordinal;
        TransactionId = transactionId;
        Kind = kind;
        CategoryId = categoryId;
        ContributingRuleVersionIds = contributingRuleVersionIds;
        Evidence = evidence;
        StaleDimensions = staleDimensions;
        ItemLifecycleFingerprint = itemLifecycleFingerprint;
        SafeReason = safeReason;
    }

    public int Ordinal { get; }
    public string TransactionId { get; }
    public ClassificationOutcomeKind Kind { get; }
    public string? CategoryId { get; }
    public IReadOnlyList<string> ContributingRuleVersionIds { get; }
    public IReadOnlyList<MatchEvidence> Evidence { get; }
    public IReadOnlyList<string> StaleDimensions { get; }
    public string ItemLifecycleFingerprint { get; }
    public string SafeReason { get; }

    public static ClassificationOutcome Suggestion(
        int ordinal,
        string transactionId,
        string categoryId,
        IReadOnlyList<string> contributingRuleVersionIds,
        IReadOnlyList<MatchEvidence> evidence,
        string itemLifecycleFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentNullException.ThrowIfNull(contributingRuleVersionIds);
        ArgumentNullException.ThrowIfNull(evidence);
        if (contributingRuleVersionIds.Count == 0)
        {
            throw new ArgumentException("A suggestion must name at least one contributing rule version.");
        }

        return new ClassificationOutcome(
            ordinal,
            transactionId,
            ClassificationOutcomeKind.Suggestion,
            categoryId,
            contributingRuleVersionIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            MatchEvidenceOrdering.Order(evidence),
            Array.Empty<string>(),
            itemLifecycleFingerprint,
            safeReason: "suggestion");
    }

    public static ClassificationOutcome NoSuggestion(
        int ordinal,
        string transactionId,
        string itemLifecycleFingerprint,
        string safeReason = "no_matching_rule")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return new ClassificationOutcome(
            ordinal,
            transactionId,
            ClassificationOutcomeKind.NoSuggestion,
            categoryId: null,
            contributingRuleVersionIds: Array.Empty<string>(),
            evidence: Array.Empty<MatchEvidence>(),
            staleDimensions: Array.Empty<string>(),
            itemLifecycleFingerprint,
            safeReason);
    }

    public static ClassificationOutcome Conflict(
        int ordinal,
        string transactionId,
        IReadOnlyList<string> contributingRuleVersionIds,
        IReadOnlyList<MatchEvidence> evidence,
        string itemLifecycleFingerprint,
        string safeReason = "incompatible_category_conflict")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(contributingRuleVersionIds);
        ArgumentNullException.ThrowIfNull(evidence);
        if (contributingRuleVersionIds.Count < 2)
        {
            throw new ArgumentException("A conflict must name at least two contributing rule versions.");
        }

        // Never select a category on conflict — no hidden winner.
        return new ClassificationOutcome(
            ordinal,
            transactionId,
            ClassificationOutcomeKind.Conflict,
            categoryId: null,
            contributingRuleVersionIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            MatchEvidenceOrdering.Order(evidence),
            Array.Empty<string>(),
            itemLifecycleFingerprint,
            safeReason);
    }

    public static ClassificationOutcome Stale(
        int ordinal,
        string transactionId,
        IReadOnlyList<string> staleDimensions,
        string itemLifecycleFingerprint,
        string safeReason = "stale_fingerprint")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(staleDimensions);
        if (staleDimensions.Count == 0)
        {
            throw new ArgumentException("A stale outcome must identify at least one fingerprint dimension.");
        }

        return new ClassificationOutcome(
            ordinal,
            transactionId,
            ClassificationOutcomeKind.Stale,
            categoryId: null,
            contributingRuleVersionIds: Array.Empty<string>(),
            evidence: Array.Empty<MatchEvidence>(),
            staleDimensions: staleDimensions
                .OrderBy(d => d, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            itemLifecycleFingerprint,
            safeReason);
    }

    /// <summary>Byte-stable semantic identity for determinism proofs (excludes nothing material).</summary>
    public string ToCanonicalJson()
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        AppendString(sb, "categoryId", CategoryId);
        sb.Append(',');
        AppendStringArray(sb, "contributingRuleVersionIds", ContributingRuleVersionIds);
        sb.Append(',');
        AppendString(sb, "itemLifecycleFingerprint", ItemLifecycleFingerprint);
        sb.Append(',');
        AppendString(sb, "kind", FormatKind(Kind));
        sb.Append(',');
        sb.Append("\"ordinal\":").Append(Ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append(',');
        AppendString(sb, "safeReason", SafeReason);
        sb.Append(',');
        AppendStringArray(sb, "staleDimensions", StaleDimensions);
        sb.Append(',');
        AppendString(sb, "transactionId", TransactionId);
        sb.Append(',');
        sb.Append("\"evidence\":[");
        for (var i = 0; i < Evidence.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Evidence[i].ToCanonicalJson());
        }

        sb.Append("]}");
        return sb.ToString();
    }

    public bool Equals(ClassificationOutcome? other) =>
        other is not null
        && string.Equals(ToCanonicalJson(), other.ToCanonicalJson(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ClassificationOutcome other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToCanonicalJson());

    public static string FormatKind(ClassificationOutcomeKind kind) => kind switch
    {
        ClassificationOutcomeKind.Suggestion => "suggestion",
        ClassificationOutcomeKind.NoSuggestion => "no_suggestion",
        ClassificationOutcomeKind.Conflict => "conflict",
        ClassificationOutcomeKind.Stale => "stale",
        _ => kind.ToString()
    };

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
            if (ch is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        sb.Append('"');
    }

    private static void AppendStringArray(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.Append('"').Append(name).Append("\":[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"');
            foreach (var ch in values[i])
            {
                if (ch is '"' or '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(ch);
            }

            sb.Append('"');
        }

        sb.Append(']');
    }
}

/// <summary>Complete pure evaluation result with exact row accounting.</summary>
public sealed class ClassificationEvaluationResult
{
    public ClassificationEvaluationResult(
        EvaluationFingerprint fingerprint,
        IReadOnlyList<ClassificationOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(outcomes);

        Fingerprint = fingerprint;
        Outcomes = outcomes
            .OrderBy(o => o.Ordinal)
            .ThenBy(o => o.TransactionId, StringComparer.Ordinal)
            .ToArray();

        InputCount = Outcomes.Count;
        SuggestionCount = Outcomes.Count(o => o.Kind == ClassificationOutcomeKind.Suggestion);
        NoSuggestionCount = Outcomes.Count(o => o.Kind == ClassificationOutcomeKind.NoSuggestion);
        ConflictCount = Outcomes.Count(o => o.Kind == ClassificationOutcomeKind.Conflict);
        StaleCount = Outcomes.Count(o => o.Kind == ClassificationOutcomeKind.Stale);

        if (SuggestionCount + NoSuggestionCount + ConflictCount + StaleCount != InputCount)
        {
            throw new InvalidOperationException(
                "Outcome partition accounting failed: kind totals must equal input count.");
        }
    }

    public EvaluationFingerprint Fingerprint { get; }
    public IReadOnlyList<ClassificationOutcome> Outcomes { get; }
    public int InputCount { get; }
    public int SuggestionCount { get; }
    public int NoSuggestionCount { get; }
    public int ConflictCount { get; }
    public int StaleCount { get; }

    /// <summary>Byte-stable hash of ordered outcome canonical JSON for determinism proofs.</summary>
    public string OutcomesCanonicalHash =>
        CanonicalClassificationHasher.HashOrderedLines(Outcomes.Select(o => o.ToCanonicalJson()));
}
