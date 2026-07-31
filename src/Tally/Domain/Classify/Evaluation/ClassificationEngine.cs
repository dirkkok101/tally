using System.Globalization;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;

namespace Tally.Domain.Classify.Evaluation;

/// <summary>
/// Pure synchronous classification evaluator (DD-CLASSIFY-DETERMINISTIC-EVALUATION).
/// Processes projection items by ordinal and rules by stable version id; aggregates compatible
/// same-category matches; conflicts on incompatible categories with no selected winner.
/// No storage, Ledger mutation, clock, random, culture, network, or host-session inputs.
/// </summary>
public static class ClassificationEngine
{
    /// <summary>
    /// Evaluate a complete ordered projection against an immutable rule set.
    /// Every input ordinal yields exactly one suggestion, no_suggestion, conflict, or stale outcome.
    /// </summary>
    public static ClassificationEvaluationResult Evaluate(ClassificationEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var rules = request.Rules
            .Where(r => request.ActiveCategoryIds.Contains(r.CategoryId))
            .OrderBy(r => r.RuleVersionId, StringComparer.Ordinal)
            .ToArray();

        var items = request.Items
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .ToArray();

        var outcomes = new List<ClassificationOutcome>(items.Length);
        var evaluationStale = request.EvaluationStaleDimensions.Count > 0
            ? request.EvaluationStaleDimensions
                .OrderBy(d => d, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        foreach (var item in items)
        {
            outcomes.Add(EvaluateItem(item, rules, evaluationStale));
        }

        return new ClassificationEvaluationResult(request.Fingerprint, outcomes);
    }

    private static ClassificationOutcome EvaluateItem(
        ClassificationEvaluationItem item,
        IReadOnlyList<ActiveRuleVersion> rules,
        IReadOnlyList<string> evaluationStaleDimensions)
    {
        if (evaluationStaleDimensions.Count > 0)
        {
            return ClassificationOutcome.Stale(
                item.Ordinal,
                item.TransactionId,
                evaluationStaleDimensions,
                item.ItemLifecycleFingerprint,
                safeReason: "evaluation_fingerprint_stale");
        }

        if (item.ItemStaleDimensions.Count > 0)
        {
            return ClassificationOutcome.Stale(
                item.Ordinal,
                item.TransactionId,
                item.ItemStaleDimensions,
                item.ItemLifecycleFingerprint,
                safeReason: "item_lifecycle_stale");
        }

        var matches = new List<RuleMatch>(capacity: Math.Min(rules.Count, 8));
        foreach (var rule in rules)
        {
            if (TryMatchRule(item, rule, out var evidence) && evidence is { Count: > 0 })
            {
                matches.Add(new RuleMatch(rule.RuleVersionId, rule.CategoryId, evidence));
            }
        }

        if (matches.Count == 0)
        {
            return ClassificationOutcome.NoSuggestion(
                item.Ordinal,
                item.TransactionId,
                item.ItemLifecycleFingerprint);
        }

        var categories = matches
            .Select(m => m.CategoryId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        var allEvidence = MatchEvidenceOrdering.Order(matches.SelectMany(m => m.Evidence));
        var allRuleIds = matches
            .Select(m => m.RuleVersionId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (categories.Length == 1)
        {
            // Compatible same-category aggregation: one suggestion naming every contributing rule version.
            return ClassificationOutcome.Suggestion(
                item.Ordinal,
                item.TransactionId,
                categories[0],
                allRuleIds,
                allEvidence,
                item.ItemLifecycleFingerprint);
        }

        // Incompatible categories → conflict with no selected category and no hidden winner.
        return ClassificationOutcome.Conflict(
            item.Ordinal,
            item.TransactionId,
            allRuleIds,
            allEvidence,
            item.ItemLifecycleFingerprint);
    }

    private static bool TryMatchRule(
        ClassificationEvaluationItem item,
        ActiveRuleVersion rule,
        out IReadOnlyList<MatchEvidence> evidence)
    {
        if (rule.Conditions.Count == 0)
        {
            evidence = Array.Empty<MatchEvidence>();
            return false;
        }

        var rows = new List<MatchEvidence>(rule.Conditions.Count);
        foreach (var condition in rule.Conditions.OrderBy(c => c.Ordinal).ThenBy(c => c.FieldKey, StringComparer.Ordinal))
        {
            if (!TryMatchCondition(item, condition, out var valueHash))
            {
                evidence = Array.Empty<MatchEvidence>();
                return false;
            }

            rows.Add(new MatchEvidence(
                rule.RuleVersionId,
                condition.ConditionId,
                condition.FieldKey,
                condition.PredicateKind,
                valueHash));
        }

        evidence = rows;
        return true;
    }

    private static bool TryMatchCondition(
        ClassificationEvaluationItem item,
        RuleCondition condition,
        out string normalizedValueHash)
    {
        normalizedValueHash = string.Empty;
        return condition.FieldKey switch
        {
            ClassificationRuleVocabulary.DescriptionNormalized =>
                MatchDescription(item, condition, out normalizedValueHash),
            ClassificationRuleVocabulary.AccountId =>
                MatchAccount(item, condition, out normalizedValueHash),
            ClassificationRuleVocabulary.AmountDirection =>
                MatchDirection(item, condition, out normalizedValueHash),
            ClassificationRuleVocabulary.AmountAbsoluteMinor =>
                MatchAbsoluteMinor(item, condition, out normalizedValueHash),
            _ => false
        };
    }

    private static bool MatchDescription(
        ClassificationEvaluationItem item,
        RuleCondition condition,
        out string normalizedValueHash)
    {
        normalizedValueHash = string.Empty;
        if (!NormalizerV1.TryNormalize(item.SourceDescription, out var normalized, out _))
        {
            return false;
        }

        normalizedValueHash = CanonicalClassificationHasher.HashUtf8(normalized);
        var operand = condition.ValueText ?? string.Empty;

        if (string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.EqualsPredicate, StringComparison.Ordinal))
        {
            return string.Equals(normalized, operand, StringComparison.Ordinal);
        }

        if (string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.StartsWithPredicate, StringComparison.Ordinal))
        {
            return normalized.StartsWith(operand, StringComparison.Ordinal);
        }

        if (string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.ContainsTokenSequencePredicate, StringComparison.Ordinal))
        {
            return ContainsTokenSequence(normalized, operand);
        }

        return false;
    }

    private static bool ContainsTokenSequence(string normalizedDescription, string normalizedOperand)
    {
        var haystack = NormalizerV1.Tokenize(normalizedDescription);
        var needle = NormalizerV1.Tokenize(normalizedOperand);
        if (needle.Count == 0 || haystack.Count < needle.Count)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Count - needle.Count; start++)
        {
            var matched = true;
            for (var i = 0; i < needle.Count; i++)
            {
                if (!string.Equals(haystack[start + i], needle[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchAccount(
        ClassificationEvaluationItem item,
        RuleCondition condition,
        out string normalizedValueHash)
    {
        var accountId = item.AccountId.Trim();
        normalizedValueHash = CanonicalClassificationHasher.HashUtf8(accountId);
        if (!string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.EqualsPredicate, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(accountId, condition.ValueText, StringComparison.Ordinal);
    }

    private static bool MatchDirection(
        ClassificationEvaluationItem item,
        RuleCondition condition,
        out string normalizedValueHash)
    {
        var direction = item.AmountDirection;
        normalizedValueHash = CanonicalClassificationHasher.HashUtf8(direction ?? string.Empty);
        if (direction is null)
        {
            return false;
        }

        if (!string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.EqualsPredicate, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(direction, condition.EnumValue, StringComparison.Ordinal);
    }

    private static bool MatchAbsoluteMinor(
        ClassificationEvaluationItem item,
        RuleCondition condition,
        out string normalizedValueHash)
    {
        var absolute = item.AmountAbsoluteMinor;
        if (absolute < 0)
        {
            // Domain invariant: absolute minor must be non-negative.
            normalizedValueHash = string.Empty;
            return false;
        }

        normalizedValueHash = CanonicalClassificationHasher.HashInt64(absolute);

        if (string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.EqualsPredicate, StringComparison.Ordinal))
        {
            return condition.ValueMinorMin is long min
                && absolute == min;
        }

        if (string.Equals(condition.PredicateKind, ClassificationRuleVocabulary.BetweenInclusivePredicate, StringComparison.Ordinal))
        {
            return condition.ValueMinorMin is long lo
                && condition.ValueMinorMax is long hi
                && absolute >= lo
                && absolute <= hi;
        }

        return false;
    }

    private static void ValidateRequest(ClassificationEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Fingerprint);
        ArgumentNullException.ThrowIfNull(request.Items);
        ArgumentNullException.ThrowIfNull(request.Rules);
        ArgumentNullException.ThrowIfNull(request.ActiveCategoryIds);
        ArgumentNullException.ThrowIfNull(request.EvaluationStaleDimensions);

        var ordinals = new HashSet<int>();
        var transactionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Ordinal < 0)
            {
                throw new ArgumentException("Evaluation item ordinals must be non-negative.");
            }

            if (!ordinals.Add(item.Ordinal))
            {
                throw new ArgumentException(
                    $"Duplicate evaluation ordinal {item.Ordinal.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (!transactionIds.Add(item.TransactionId))
            {
                throw new ArgumentException($"Duplicate evaluation transaction id '{item.TransactionId}'.");
            }

            if (string.IsNullOrWhiteSpace(item.TransactionId))
            {
                throw new ArgumentException("Transaction id is required.");
            }

            if (string.IsNullOrWhiteSpace(item.ItemLifecycleFingerprint))
            {
                throw new ArgumentException("Item lifecycle fingerprint is required.");
            }

            if (item.AmountAbsoluteMinor < 0)
            {
                throw new ArgumentException("AmountAbsoluteMinor must be non-negative.");
            }

            if (item.AmountDirection is not null
                && item.AmountDirection is not (
                    ClassificationRuleVocabulary.DirectionInflow
                    or ClassificationRuleVocabulary.DirectionOutflow))
            {
                throw new ArgumentException(
                    "AmountDirection must be null, inflow, or outflow (vocabulary closed set).");
            }
        }

        // Contiguous ordinals 0..n-1 required for projection accounting.
        if (ordinals.Count > 0)
        {
            for (var i = 0; i < ordinals.Count; i++)
            {
                if (!ordinals.Contains(i))
                {
                    throw new ArgumentException(
                        "Evaluation item ordinals must be contiguous starting at 0.");
                }
            }
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in request.Rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (string.IsNullOrWhiteSpace(rule.RuleVersionId))
            {
                throw new ArgumentException("Rule version id is required.");
            }

            if (!ruleIds.Add(rule.RuleVersionId))
            {
                throw new ArgumentException($"Duplicate rule version id '{rule.RuleVersionId}'.");
            }

            if (string.IsNullOrWhiteSpace(rule.CategoryId))
            {
                throw new ArgumentException("Rule category id is required.");
            }

            if (rule.Conditions is null || rule.Conditions.Count == 0)
            {
                throw new ArgumentException($"Rule '{rule.RuleVersionId}' must have at least one condition.");
            }
        }
    }

    private sealed record RuleMatch(
        string RuleVersionId,
        string CategoryId,
        IReadOnlyList<MatchEvidence> Evidence);
}

/// <summary>Immutable active rule version used by the pure engine (no lifecycle authority).</summary>
public sealed class ActiveRuleVersion
{
    public ActiveRuleVersion(
        string ruleVersionId,
        string categoryId,
        IReadOnlyList<RuleCondition> conditions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentNullException.ThrowIfNull(conditions);
        RuleVersionId = ruleVersionId;
        CategoryId = categoryId;
        Conditions = conditions;
    }

    public string RuleVersionId { get; }
    public string CategoryId { get; }
    public IReadOnlyList<RuleCondition> Conditions { get; }
}

/// <summary>
/// One projection row for pure evaluation. Directions use the closed vocabulary
/// (inflow/outflow); absolute minor is non-negative. Item-level stale dimensions short-circuit matching.
/// </summary>
public sealed class ClassificationEvaluationItem
{
    public ClassificationEvaluationItem(
        int ordinal,
        string transactionId,
        string accountId,
        string sourceDescription,
        string? amountDirection,
        long amountAbsoluteMinor,
        string itemLifecycleFingerprint,
        IReadOnlyList<string>? itemStaleDimensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(sourceDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemLifecycleFingerprint);
        Ordinal = ordinal;
        TransactionId = transactionId;
        AccountId = accountId;
        SourceDescription = sourceDescription;
        AmountDirection = amountDirection;
        AmountAbsoluteMinor = amountAbsoluteMinor;
        ItemLifecycleFingerprint = itemLifecycleFingerprint;
        ItemStaleDimensions = itemStaleDimensions is null
            ? Array.Empty<string>()
            : itemStaleDimensions
                .OrderBy(d => d, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    public int Ordinal { get; }
    public string TransactionId { get; }
    public string AccountId { get; }
    public string SourceDescription { get; }
    public string? AmountDirection { get; }
    public long AmountAbsoluteMinor { get; }
    public string ItemLifecycleFingerprint { get; }
    public IReadOnlyList<string> ItemStaleDimensions { get; }
}

/// <summary>Complete pure evaluation request (all ordering and limits are explicit inputs).</summary>
public sealed class ClassificationEvaluationRequest
{
    public ClassificationEvaluationRequest(
        EvaluationFingerprint fingerprint,
        IReadOnlyList<ClassificationEvaluationItem> items,
        IReadOnlyList<ActiveRuleVersion> rules,
        IReadOnlySet<string> activeCategoryIds,
        IReadOnlyList<string>? evaluationStaleDimensions = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(activeCategoryIds);
        Fingerprint = fingerprint;
        Items = items;
        Rules = rules;
        ActiveCategoryIds = activeCategoryIds;
        EvaluationStaleDimensions = evaluationStaleDimensions is null
            ? Array.Empty<string>()
            : evaluationStaleDimensions.ToArray();
    }

    public EvaluationFingerprint Fingerprint { get; }
    public IReadOnlyList<ClassificationEvaluationItem> Items { get; }
    public IReadOnlyList<ActiveRuleVersion> Rules { get; }
    public IReadOnlySet<string> ActiveCategoryIds { get; }
    public IReadOnlyList<string> EvaluationStaleDimensions { get; }
}
