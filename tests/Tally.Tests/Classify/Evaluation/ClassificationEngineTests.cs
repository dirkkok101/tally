using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-DETERMINISTIC-ENGINE / FR-CLASSIFY-DETERMINISTIC-EVALUATION / bd-2sde
/// Outcome partition: suggestion, no_suggestion, conflict, stale; compatible aggregation; no winner.
/// </summary>
public sealed class ClassificationEngineTests
{
    // ── Suggestion ───────────────────────────────────────────────────────────

    // FR-CLASSIFY-DETERMINISTIC-EVALUATION / AC: exactly one matching rule → suggestion
    [Fact]
    public void Single_matching_rule_produces_suggestion_with_category_and_rule_version()
    {
        var rule = Rule("rv-1", "cat-food", DescriptionEquals(0, "WHOLE FOODS"));
        var item = Item(0, "tx-1", description: "Whole Foods #12");
        var result = Evaluate([item], [rule], ["cat-food"]);

        Assert.Equal(1, result.InputCount);
        Assert.Equal(1, result.SuggestionCount);
        Assert.Equal(0, result.NoSuggestionCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(0, result.StaleCount);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, outcome.Kind);
        Assert.Equal("cat-food", outcome.CategoryId);
        Assert.Equal(["rv-1"], outcome.ContributingRuleVersionIds);
        Assert.Single(outcome.Evidence);
        Assert.Equal(ClassificationRuleVocabulary.DescriptionNormalized, outcome.Evidence[0].FieldKey);
        Assert.Equal(64, outcome.Evidence[0].NormalizedValueHash.Length);
    }

    // FR-CLASSIFY-DETERMINISTIC-EVALUATION / AC: multiple same-category matches → one suggestion
    [Fact]
    public void Compatible_same_category_rules_aggregate_all_contributing_versions()
    {
        var a = Rule("rv-a", "cat-x", DescriptionContains(0, "uber"));
        var b = Rule("rv-b", "cat-x", AccountEquals(0, "acct-1"));
        var item = Item(0, "tx-1", accountId: "acct-1", description: "UBER TRIP");
        var result = Evaluate([item], [b, a], ["cat-x"]);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, outcome.Kind);
        Assert.Equal("cat-x", outcome.CategoryId);
        Assert.Equal(["rv-a", "rv-b"], outcome.ContributingRuleVersionIds.ToArray());
        Assert.Equal(2, outcome.Evidence.Count);
        // Evidence ordered by rule version id, not input order.
        Assert.Equal("rv-a", outcome.Evidence[0].RuleVersionId);
        Assert.Equal("rv-b", outcome.Evidence[1].RuleVersionId);
    }

    [Fact]
    public void Three_compatible_rules_name_every_contributing_version_sorted()
    {
        var rules = new[]
        {
            Rule("rv-c", "cat-z", DirectionEquals(0, ClassificationRuleVocabulary.DirectionOutflow)),
            Rule("rv-a", "cat-z", DescriptionStartsWith(0, "shell")),
            Rule("rv-b", "cat-z", MinorBetween(0, 100, 10_000))
        };
        var item = Item(0, "tx-1", description: "SHELL 99", direction: ClassificationRuleVocabulary.DirectionOutflow, absoluteMinor: 500);
        var result = Evaluate([item], rules, ["cat-z"]);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, outcome.Kind);
        Assert.Equal(["rv-a", "rv-b", "rv-c"], outcome.ContributingRuleVersionIds.ToArray());
    }

    // ── No suggestion ────────────────────────────────────────────────────────

    // FR-CLASSIFY-DETERMINISTIC-EVALUATION / AC: no matching rule → no-suggestion
    [Fact]
    public void No_matching_rule_produces_explicit_no_suggestion_without_category()
    {
        var rule = Rule("rv-1", "cat-food", DescriptionEquals(0, "costco"));
        var item = Item(0, "tx-1", description: "Random Merchant");
        var result = Evaluate([item], [rule], ["cat-food"]);

        Assert.Equal(1, result.NoSuggestionCount);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, outcome.Kind);
        Assert.Null(outcome.CategoryId);
        Assert.Empty(outcome.ContributingRuleVersionIds);
        Assert.Empty(outcome.Evidence);
        Assert.Equal("no_matching_rule", outcome.SafeReason);
    }

    [Fact]
    public void Empty_rule_set_yields_no_suggestion_for_every_item()
    {
        var items = new[] { Item(0, "tx-0"), Item(1, "tx-1") };
        var result = Evaluate(items, [], ["cat-a"]);
        Assert.Equal(2, result.NoSuggestionCount);
        Assert.All(result.Outcomes, o => Assert.Equal(ClassificationOutcomeKind.NoSuggestion, o.Kind));
    }

    [Fact]
    public void Rule_targeting_inactive_category_is_excluded_and_yields_no_suggestion()
    {
        var rule = Rule("rv-1", "cat-archived", DescriptionEquals(0, "merchant"));
        var item = Item(0, "tx-1", description: "merchant");
        // Active catalogue does not include cat-archived.
        var result = Evaluate([item], [rule], ["cat-other"]);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(result.Outcomes).Kind);
    }

    // ── Conflict ─────────────────────────────────────────────────────────────

    // FR-CLASSIFY-DETERMINISTIC-EVALUATION / AC: different categories → conflict, no winner
    [Fact]
    public void Incompatible_category_matches_produce_conflict_with_no_selected_category()
    {
        var food = Rule("rv-food", "cat-food", DescriptionContains(0, "market"));
        var travel = Rule("rv-travel", "cat-travel", AccountEquals(0, "acct-1"));
        var item = Item(0, "tx-1", accountId: "acct-1", description: "MARKET PLACE");
        var result = Evaluate([item], [food, travel], ["cat-food", "cat-travel"]);

        Assert.Equal(1, result.ConflictCount);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Conflict, outcome.Kind);
        Assert.Null(outcome.CategoryId);
        Assert.Equal(["rv-food", "rv-travel"], outcome.ContributingRuleVersionIds.ToArray());
        Assert.Equal(2, outcome.Evidence.Count);
        Assert.Equal("incompatible_category_conflict", outcome.SafeReason);
    }

    [Fact]
    public void Conflict_never_selects_category_even_when_rule_input_order_changes()
    {
        var a = Rule("rv-a", "cat-a", DescriptionEquals(0, "x"));
        var b = Rule("rv-b", "cat-b", DescriptionEquals(0, "x"));
        var item = Item(0, "tx-1", description: "x");
        var left = Evaluate([item], [a, b], ["cat-a", "cat-b"]);
        var right = Evaluate([item], [b, a], ["cat-a", "cat-b"]);
        Assert.Null(left.Outcomes[0].CategoryId);
        Assert.Null(right.Outcomes[0].CategoryId);
        Assert.Equal(left.Outcomes[0].ToCanonicalJson(), right.Outcomes[0].ToCanonicalJson());
    }

    [Fact]
    public void Three_way_category_conflict_names_all_rules_and_selects_none()
    {
        var rules = new[]
        {
            Rule("rv-1", "c1", DescriptionEquals(0, "z")),
            Rule("rv-2", "c2", DescriptionEquals(0, "z")),
            Rule("rv-3", "c3", DescriptionEquals(0, "z"))
        };
        var result = Evaluate([Item(0, "tx", description: "z")], rules, ["c1", "c2", "c3"]);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Conflict, outcome.Kind);
        Assert.Null(outcome.CategoryId);
        Assert.Equal(3, outcome.ContributingRuleVersionIds.Count);
    }

    // ── Predicates ───────────────────────────────────────────────────────────

    [Fact]
    public void Description_equals_is_case_and_punctuation_normalized()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "whole-foods"));
        var item = Item(0, "tx", description: "Whole Foods");
        var outcome = Assert.Single(Evaluate([item], [rule], ["cat"]).Outcomes);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, outcome.Kind);
    }

    [Fact]
    public void Description_starts_with_matches_normalized_prefix()
    {
        var rule = Rule("rv-1", "cat", DescriptionStartsWith(0, "shell"));
        var item = Item(0, "tx", description: "SHELL OIL 123");
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([item], [rule], ["cat"]).Outcomes).Kind);
    }

    [Fact]
    public void Description_contains_token_sequence_requires_contiguous_tokens()
    {
        var rule = Rule("rv-1", "cat", DescriptionContains(0, "coffee shop"));
        var hit = Item(0, "tx-hit", description: "Local Coffee Shop Downtown");
        var miss = Item(0, "tx-miss", description: "Coffee Downtown Shop");
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([hit], [rule], ["cat"]).Outcomes).Kind);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(Evaluate([miss], [rule], ["cat"]).Outcomes).Kind);
    }

    [Fact]
    public void Account_id_equals_is_exact_after_trim()
    {
        var rule = Rule("rv-1", "cat", AccountEquals(0, "acct-9"));
        var hit = Item(0, "tx", accountId: " acct-9 ");
        var miss = Item(0, "tx", accountId: "acct-8");
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([hit], [rule], ["cat"]).Outcomes).Kind);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(Evaluate([miss], [rule], ["cat"]).Outcomes).Kind);
    }

    [Fact]
    public void Amount_direction_equals_inflow_and_outflow()
    {
        var inflow = Rule("rv-in", "cat-in", DirectionEquals(0, ClassificationRuleVocabulary.DirectionInflow));
        var outflow = Rule("rv-out", "cat-out", DirectionEquals(0, ClassificationRuleVocabulary.DirectionOutflow));
        var inItem = Item(0, "tx-in", direction: ClassificationRuleVocabulary.DirectionInflow);
        var outItem = Item(0, "tx-out", direction: ClassificationRuleVocabulary.DirectionOutflow);
        Assert.Equal("cat-in", Assert.Single(Evaluate([inItem], [inflow, outflow], ["cat-in", "cat-out"]).Outcomes).CategoryId);
        Assert.Equal("cat-out", Assert.Single(Evaluate([outItem], [inflow, outflow], ["cat-in", "cat-out"]).Outcomes).CategoryId);
    }

    [Fact]
    public void Zero_amount_direction_null_never_matches_direction_predicate()
    {
        var rule = Rule("rv-1", "cat", DirectionEquals(0, ClassificationRuleVocabulary.DirectionOutflow));
        var item = Item(0, "tx", direction: null, absoluteMinor: 0);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(Evaluate([item], [rule], ["cat"]).Outcomes).Kind);
    }

    [Fact]
    public void Absolute_minor_equals_and_between_inclusive()
    {
        var eq = Rule("rv-eq", "cat", MinorEquals(0, 1500));
        var between = Rule("rv-bt", "cat", MinorBetween(0, 1000, 2000));
        var item = Item(0, "tx", absoluteMinor: 1500);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([item], [eq], ["cat"]).Outcomes).Kind);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([item], [between], ["cat"]).Outcomes).Kind);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(Evaluate([Item(0, "tx", absoluteMinor: 999)], [between], ["cat"]).Outcomes).Kind);
    }

    [Fact]
    public void And_composition_requires_every_condition()
    {
        var rule = new ActiveRuleVersion(
            "rv-and",
            "cat",
            [
                DescriptionContains(0, "uber"),
                DirectionEquals(1, ClassificationRuleVocabulary.DirectionOutflow)
            ]);
        var full = Item(0, "tx", description: "UBER", direction: ClassificationRuleVocabulary.DirectionOutflow);
        var half = Item(0, "tx", description: "UBER", direction: ClassificationRuleVocabulary.DirectionInflow);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, Assert.Single(Evaluate([full], [rule], ["cat"]).Outcomes).Kind);
        Assert.Equal(ClassificationOutcomeKind.NoSuggestion, Assert.Single(Evaluate([half], [rule], ["cat"]).Outcomes).Kind);
    }

    // ── Stale ────────────────────────────────────────────────────────────────

    // FR-CLASSIFY-OUTCOME-INVALIDATION dimensions at evaluation time
    [Fact]
    public void Evaluation_stale_dimensions_mark_every_item_stale_without_matching()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "x"));
        var items = new[] { Item(0, "tx-0", description: "x"), Item(1, "tx-1", description: "x") };
        var result = Evaluate(
            items,
            [rule],
            ["cat"],
            evaluationStale: [EvaluationFingerprint.DimensionSnapshotExpiresAt]);

        Assert.Equal(2, result.StaleCount);
        Assert.Equal(0, result.SuggestionCount);
        Assert.All(result.Outcomes, o =>
        {
            Assert.Equal(ClassificationOutcomeKind.Stale, o.Kind);
            Assert.Null(o.CategoryId);
            Assert.Contains(EvaluationFingerprint.DimensionSnapshotExpiresAt, o.StaleDimensions);
            Assert.Empty(o.Evidence);
        });
    }

    [Fact]
    public void Item_level_stale_dimensions_short_circuit_only_that_item()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "ok"));
        var stale = Item(0, "tx-stale", description: "ok", itemStale: [EvaluationFingerprint.DimensionOrderedItems]);
        var live = Item(1, "tx-live", description: "ok");
        var result = Evaluate([stale, live], [rule], ["cat"]);
        Assert.Equal(ClassificationOutcomeKind.Stale, result.Outcomes[0].Kind);
        Assert.Equal(ClassificationOutcomeKind.Suggestion, result.Outcomes[1].Kind);
        Assert.Equal(1, result.StaleCount);
        Assert.Equal(1, result.SuggestionCount);
    }

    [Fact]
    public void Stale_outcome_lists_dimensions_in_stable_order()
    {
        var item = Item(0, "tx", itemStale:
        [
            EvaluationFingerprint.DimensionRuleSetVersion,
            EvaluationFingerprint.DimensionCategoryLifecycle,
            EvaluationFingerprint.DimensionNormalizationVersion
        ]);
        var outcome = Assert.Single(Evaluate([item], [], []).Outcomes);
        Assert.Equal(
            [
                EvaluationFingerprint.DimensionCategoryLifecycle,
                EvaluationFingerprint.DimensionNormalizationVersion,
                EvaluationFingerprint.DimensionRuleSetVersion
            ],
            outcome.StaleDimensions.ToArray());
    }

    // ── Ordering and accounting ──────────────────────────────────────────────

    [Fact]
    public void Outcomes_are_ordered_by_projection_ordinal()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "m"));
        // Insert out of order; engine must emit 0..n-1.
        var items = new[]
        {
            Item(2, "tx-2", description: "m"),
            Item(0, "tx-0", description: "other"),
            Item(1, "tx-1", description: "m")
        };
        var result = Evaluate(items, [rule], ["cat"]);
        Assert.Equal([0, 1, 2], result.Outcomes.Select(o => o.Ordinal).ToArray());
        Assert.Equal(
            [
                ClassificationOutcomeKind.NoSuggestion,
                ClassificationOutcomeKind.Suggestion,
                ClassificationOutcomeKind.Suggestion
            ],
            result.Outcomes.Select(o => o.Kind).ToArray());
        Assert.Equal(3, result.InputCount);
        Assert.Equal(2, result.SuggestionCount);
        Assert.Equal(1, result.NoSuggestionCount);
        Assert.Equal(result.InputCount, result.SuggestionCount + result.NoSuggestionCount + result.ConflictCount + result.StaleCount);
    }

    [Fact]
    public void Totals_always_equal_input_count_for_mixed_partition()
    {
        var food = Rule("rv-food", "cat-food", DescriptionEquals(0, "food"));
        var travel = Rule("rv-travel", "cat-travel", DescriptionEquals(0, "food"));
        var items = new[]
        {
            Item(0, "tx-s", description: "food"),
            Item(1, "tx-n", description: "none"),
            Item(2, "tx-c", description: "food"), // will conflict if both rules match — both match "food"
            Item(3, "tx-stale", description: "food", itemStale: [EvaluationFingerprint.DimensionStoreGeneration])
        };
        // For tx-s and tx-c both rules match different categories → conflict
        // Wait: both food and travel match description "food" → conflict for items 0 and 2
        // Item 1 no match; item 3 stale
        var result = Evaluate(items, [food, travel], ["cat-food", "cat-travel"]);
        Assert.Equal(4, result.InputCount);
        Assert.Equal(0, result.SuggestionCount);
        Assert.Equal(1, result.NoSuggestionCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.Equal(1, result.StaleCount);
        Assert.Equal(4, result.SuggestionCount + result.NoSuggestionCount + result.ConflictCount + result.StaleCount);
    }

    [Fact]
    public void Mixed_partition_with_compatible_suggestion()
    {
        var only = Rule("rv-only", "cat-food", DescriptionEquals(0, "foodco"));
        var a = Rule("rv-a", "cat-a", DescriptionEquals(0, "clash"));
        var b = Rule("rv-b", "cat-b", DescriptionEquals(0, "clash"));
        var items = new[]
        {
            Item(0, "tx-s", description: "foodco"),
            Item(1, "tx-n", description: "zzz"),
            Item(2, "tx-c", description: "clash"),
            Item(3, "tx-st", description: "foodco", itemStale: [EvaluationFingerprint.DimensionSnapshotId])
        };
        var result = Evaluate(items, [only, a, b], ["cat-food", "cat-a", "cat-b"]);
        Assert.Equal(1, result.SuggestionCount);
        Assert.Equal(1, result.NoSuggestionCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(1, result.StaleCount);
        Assert.Equal("cat-food", result.Outcomes[0].CategoryId);
        Assert.Null(result.Outcomes[2].CategoryId);
    }

    // ── Evidence bounds ──────────────────────────────────────────────────────

    [Fact]
    public void Match_evidence_retains_only_rule_condition_field_predicate_and_value_hash()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "alpha"));
        var item = Item(0, "tx", description: "Alpha!!!");
        var evidence = Assert.Single(Assert.Single(Evaluate([item], [rule], ["cat"]).Outcomes).Evidence);
        Assert.Equal("rv-1", evidence.RuleVersionId);
        Assert.False(string.IsNullOrWhiteSpace(evidence.ConditionId));
        Assert.Equal(ClassificationRuleVocabulary.DescriptionNormalized, evidence.FieldKey);
        Assert.Equal(ClassificationRuleVocabulary.EqualsPredicate, evidence.PredicateKind);
        Assert.Equal(64, evidence.NormalizedValueHash.Length);
        // Evidence must not embed raw description text.
        Assert.DoesNotContain("Alpha", evidence.ToCanonicalJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", evidence.NormalizedValueHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalized_value_hash_is_stable_for_equivalent_descriptions()
    {
        var rule = Rule("rv-1", "cat", DescriptionEquals(0, "costco"));
        var a = Item(0, "tx-a", description: "Costco");
        var b = Item(0, "tx-b", description: "COSTCO!!!");
        var ha = Assert.Single(Assert.Single(Evaluate([a], [rule], ["cat"]).Outcomes).Evidence).NormalizedValueHash;
        var hb = Assert.Single(Assert.Single(Evaluate([b], [rule], ["cat"]).Outcomes).Evidence).NormalizedValueHash;
        Assert.Equal(ha, hb);
        Assert.Equal(CanonicalClassificationHasher.HashUtf8("costco"), ha);
    }

    // ── Fingerprint ──────────────────────────────────────────────────────────

    [Fact]
    public void Evaluation_fingerprint_covers_all_required_dimensions()
    {
        var fp = Fingerprint();
        Assert.Equal(64, fp.CanonicalHash.Length);
        Assert.False(string.IsNullOrWhiteSpace(fp.LedgerContractVersion));
        Assert.False(string.IsNullOrWhiteSpace(fp.ProjectionVersion));
        Assert.False(string.IsNullOrWhiteSpace(fp.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(fp.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(fp.SnapshotExpiresAt));
        Assert.False(string.IsNullOrWhiteSpace(fp.CategoryLifecycleFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(fp.NormalizationVersion));
        Assert.False(string.IsNullOrWhiteSpace(fp.RuleSetVersionId));
        Assert.False(string.IsNullOrWhiteSpace(fp.OrderedItemsFingerprint));
        Assert.Equal(9, EvaluationFingerprint.AllDimensions.Count);
    }

    [Fact]
    public void Fingerprint_diff_identifies_changed_dimensions_only()
    {
        var baseFp = Fingerprint(ruleSet: "rs-1");
        var changed = Fingerprint(ruleSet: "rs-2", snapshot: "snap-other");
        var diffs = baseFp.DiffDimensions(changed);
        Assert.Contains(EvaluationFingerprint.DimensionRuleSetVersion, diffs);
        Assert.Contains(EvaluationFingerprint.DimensionSnapshotId, diffs);
        Assert.DoesNotContain(EvaluationFingerprint.DimensionNormalizationVersion, diffs);
        Assert.Empty(baseFp.DiffDimensions(baseFp));
    }

    [Fact]
    public void Ordered_items_fingerprint_is_order_insensitive_to_input_sequence()
    {
        var rows = new[]
        {
            (0, "tx-a", "life-a"),
            (1, "tx-b", "life-b")
        };
        var forward = EvaluationFingerprint.ComputeOrderedItemsFingerprint(rows);
        var reverse = EvaluationFingerprint.ComputeOrderedItemsFingerprint(rows.Reverse());
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Category_lifecycle_fingerprint_is_stable_for_permuted_catalogue()
    {
        var a = EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
            [("c-b", "active"), ("c-a", "active")]);
        var b = EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
            [("c-a", "active"), ("c-b", "active")]);
        Assert.Equal(a, b);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_ordinals_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Evaluate([Item(0, "tx-a"), Item(0, "tx-b")], [], []));
    }

    [Fact]
    public void Non_contiguous_ordinals_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Evaluate([Item(0, "tx-a"), Item(2, "tx-b")], [], []));
    }

    [Fact]
    public void Conflict_factory_rejects_single_rule_and_suggestion_requires_category()
    {
        Assert.Throws<ArgumentException>(() =>
            ClassificationOutcome.Conflict(0, "tx", ["only-one"], [], "life"));
        Assert.Throws<ArgumentException>(() =>
            ClassificationOutcome.Suggestion(0, "tx", "cat", [], [], "life"));
        Assert.Throws<ArgumentException>(() =>
            ClassificationOutcome.Stale(0, "tx", [], "life"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ClassificationEvaluationResult Evaluate(
        IReadOnlyList<ClassificationEvaluationItem> items,
        IReadOnlyList<ActiveRuleVersion> rules,
        IEnumerable<string> activeCategories,
        IReadOnlyList<string>? evaluationStale = null) =>
        ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(),
            items,
            rules,
            activeCategories.ToHashSet(StringComparer.Ordinal),
            evaluationStale));

    private static EvaluationFingerprint Fingerprint(
        string ruleSet = "rs-v1",
        string snapshot = "snap-1") =>
        EvaluationFingerprint.Create(
            ledgerContractVersion: "1.0",
            projectionVersion: "classification_v1",
            storeGenerationFingerprint: "gen-1",
            snapshotId: snapshot,
            snapshotExpiresAt: "2026-07-31T23:59:59.0000000Z",
            categoryLifecycleFingerprint: "cat-fp-1",
            normalizationVersion: NormalizationDescriptor.V1.Version,
            ruleSetVersionId: ruleSet,
            orderedItemsFingerprint: "items-fp-1");

    private static ClassificationEvaluationItem Item(
        int ordinal,
        string transactionId,
        string accountId = "acct-1",
        string description = "merchant",
        string? direction = ClassificationRuleVocabulary.DirectionOutflow,
        long absoluteMinor = 100,
        IReadOnlyList<string>? itemStale = null) =>
        new(ordinal, transactionId, accountId, description, direction, absoluteMinor, "life-" + transactionId, itemStale);

    private static ActiveRuleVersion Rule(string id, string categoryId, RuleCondition condition) =>
        new(id, categoryId, [condition]);

    private static RuleCondition DescriptionEquals(int ordinal, string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.EqualsPredicate,
            value,
            null,
            null,
            null,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition DescriptionStartsWith(int ordinal, string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.StartsWithPredicate,
            value,
            null,
            null,
            null,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition DescriptionContains(int ordinal, string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.ContainsTokenSequencePredicate,
            value,
            null,
            null,
            null,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition AccountEquals(int ordinal, string accountId)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.AccountId,
            ClassificationRuleVocabulary.EqualsPredicate,
            accountId,
            null,
            null,
            null,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition DirectionEquals(int ordinal, string direction)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.AmountDirection,
            ClassificationRuleVocabulary.EqualsPredicate,
            null,
            null,
            null,
            direction,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition MinorEquals(int ordinal, long minor)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.AmountAbsoluteMinor,
            ClassificationRuleVocabulary.EqualsPredicate,
            null,
            minor,
            null,
            null,
            out var condition,
            out _));
        return condition!;
    }

    private static RuleCondition MinorBetween(int ordinal, long min, long max)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.AmountAbsoluteMinor,
            ClassificationRuleVocabulary.BetweenInclusivePredicate,
            null,
            min,
            max,
            null,
            out var condition,
            out _));
        return condition!;
    }
}
