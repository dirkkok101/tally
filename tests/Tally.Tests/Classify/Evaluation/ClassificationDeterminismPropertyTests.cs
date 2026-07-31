using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TC-CLASSIFY-DETERMINISTIC-PROPERTY-MATRIX / NFR-CLASSIFY-DETERMINISTIC-INTEGRITY / bd-2sde
/// Repeated and permuted evaluation yields identical ordered outcomes; exact row accounting;
/// zero selected incompatible conflicts.
/// </summary>
public sealed class ClassificationDeterminismPropertyTests
{
    // ── Determinism under repetition ─────────────────────────────────────────

    // FR-CLASSIFY-DETERMINISTIC-EVALUATION / AC: identical fingerprint → identical outcomes
    [Fact]
    public void Identical_request_repeated_yields_byte_identical_outcomes_hash()
    {
        var request = BuildMixedRequest();
        var first = ClassificationEngine.Evaluate(request);
        var second = ClassificationEngine.Evaluate(request);
        Assert.Equal(first.OutcomesCanonicalHash, second.OutcomesCanonicalHash);
        Assert.Equal(first.Fingerprint.CanonicalHash, second.Fingerprint.CanonicalHash);
        Assert.Equal(
            first.Outcomes.Select(o => o.ToCanonicalJson()),
            second.Outcomes.Select(o => o.ToCanonicalJson()));
    }

    [Fact]
    public void Ten_repetitions_never_diverge()
    {
        var request = BuildMixedRequest();
        var baseline = ClassificationEngine.Evaluate(request).OutcomesCanonicalHash;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(baseline, ClassificationEngine.Evaluate(request).OutcomesCanonicalHash);
        }
    }

    // ── Rule input order permutation ─────────────────────────────────────────

    [Fact]
    public void Rule_input_order_permutation_does_not_change_outcomes()
    {
        var items = ContiguousItems(5);
        var rules = new[]
        {
            MakeRule("rv-z", "cat-z", "merchant z"),
            MakeRule("rv-a", "cat-a", "merchant a"),
            MakeRule("rv-m", "cat-m", "merchant m")
        };
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat-z", "cat-a", "cat-m" };
        var fp = Fingerprint();

        var forward = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(fp, items, rules, active));
        var reverse = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(fp, items, rules.Reverse().ToArray(), active));
        var shuffled = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            fp, items, [rules[1], rules[2], rules[0]], active));

        Assert.Equal(forward.OutcomesCanonicalHash, reverse.OutcomesCanonicalHash);
        Assert.Equal(forward.OutcomesCanonicalHash, shuffled.OutcomesCanonicalHash);
    }

    [Fact]
    public void Item_input_order_permutation_does_not_change_ordered_outcomes()
    {
        var items = ContiguousItems(4);
        var rules = new[] { MakeRule("rv-1", "cat-1", "merchant 1") };
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat-1" };
        var fp = Fingerprint();

        var natural = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(fp, items, rules, active));
        var reversed = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            fp, items.Reverse().ToArray(), rules, active));

        Assert.Equal(natural.OutcomesCanonicalHash, reversed.OutcomesCanonicalHash);
        Assert.Equal([0, 1, 2, 3], reversed.Outcomes.Select(o => o.Ordinal).ToArray());
    }

    // ── Exact row accounting ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    public void Every_input_ordinal_accounted_exactly_once(int count)
    {
        // Descriptions encode parity: even → "even N", odd → "odd N".
        var items = ContiguousParityItems(count);
        var rules = new[]
        {
            new ActiveRuleVersion("rv-even", "cat-even", [DescriptionStartsWith(0, "even")]),
            new ActiveRuleVersion("rv-odd-a", "cat-odd", [DescriptionStartsWith(0, "odd")]),
            new ActiveRuleVersion("rv-odd-b", "cat-other", [DescriptionStartsWith(0, "odd")])
        };
        // Even ordinals: only rv-even matches → suggestion
        // Odd ordinals: rv-odd-a and rv-odd-b match different cats → conflict
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat-even", "cat-odd", "cat-other" };
        var result = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(Fingerprint(), items, rules, active));

        Assert.Equal(count, result.InputCount);
        Assert.Equal(count, result.Outcomes.Count);
        Assert.Equal(count, result.SuggestionCount + result.NoSuggestionCount + result.ConflictCount + result.StaleCount);
        Assert.Equal(Enumerable.Range(0, count), result.Outcomes.Select(o => o.Ordinal));
        Assert.Equal(count, result.Outcomes.Select(o => o.TransactionId).Distinct(StringComparer.Ordinal).Count());

        foreach (var outcome in result.Outcomes)
        {
            if (outcome.Ordinal % 2 == 0)
            {
                Assert.Equal(ClassificationOutcomeKind.Suggestion, outcome.Kind);
                Assert.Equal("cat-even", outcome.CategoryId);
            }
            else
            {
                Assert.Equal(ClassificationOutcomeKind.Conflict, outcome.Kind);
                Assert.Null(outcome.CategoryId);
            }
        }
    }

    [Fact]
    public void Empty_projection_accounts_zero_rows()
    {
        var result = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(),
            Array.Empty<ClassificationEvaluationItem>(),
            [MakeRule("rv-1", "cat", "x")],
            new HashSet<string>(StringComparer.Ordinal) { "cat" }));
        Assert.Equal(0, result.InputCount);
        Assert.Empty(result.Outcomes);
        Assert.Equal(0, result.SuggestionCount + result.NoSuggestionCount + result.ConflictCount + result.StaleCount);
    }

    // ── Conflict selection invariant ─────────────────────────────────────────

    [Fact]
    public void Zero_incompatible_conflicts_select_a_category_across_property_matrix()
    {
        var rng = new DeterministicSequence(seed: 42);
        for (var trial = 0; trial < 20; trial++)
        {
            var itemCount = 3 + (rng.Next() % 5);
            var items = ContiguousItems(itemCount);
            var rules = new List<ActiveRuleVersion>();
            for (var r = 0; r < 4; r++)
            {
                var cat = "cat-" + (rng.Next() % 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                rules.Add(MakeRule(
                    "rv-" + trial.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + r.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cat,
                    "merchant " + (rng.Next() % itemCount).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var active = rules.Select(r => r.CategoryId).ToHashSet(StringComparer.Ordinal);
            var result = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
                Fingerprint(), items, rules, active));

            Assert.Equal(itemCount, result.InputCount);
            foreach (var outcome in result.Outcomes)
            {
                if (outcome.Kind == ClassificationOutcomeKind.Conflict)
                {
                    Assert.Null(outcome.CategoryId);
                    Assert.True(outcome.ContributingRuleVersionIds.Count >= 2);
                }

                if (outcome.Kind == ClassificationOutcomeKind.Suggestion)
                {
                    Assert.False(string.IsNullOrWhiteSpace(outcome.CategoryId));
                    Assert.NotEmpty(outcome.ContributingRuleVersionIds);
                }
            }
        }
    }

    [Fact]
    public void Compatible_overlap_never_emits_conflict()
    {
        var items = ContiguousItems(6);
        var rules = new[]
        {
            MakeRule("rv-1", "cat-same", descriptionPrefix: null, matchAll: true),
            MakeRule("rv-2", "cat-same", descriptionPrefix: null, matchAll: true)
        };
        var result = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(),
            items,
            rules,
            new HashSet<string>(StringComparer.Ordinal) { "cat-same" }));

        Assert.Equal(6, result.SuggestionCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.All(result.Outcomes, o =>
        {
            Assert.Equal(ClassificationOutcomeKind.Suggestion, o.Kind);
            Assert.Equal("cat-same", o.CategoryId);
            Assert.Equal(["rv-1", "rv-2"], o.ContributingRuleVersionIds.ToArray());
        });
    }

    // ── Fingerprint stability and change detection ───────────────────────────

    [Fact]
    public void Fingerprint_canonical_hash_stable_across_create_calls()
    {
        var a = Fingerprint();
        var b = Fingerprint();
        Assert.Equal(a.CanonicalHash, b.CanonicalHash);
        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
    }

    [Theory]
    [InlineData(EvaluationFingerprint.DimensionLedgerContractVersion)]
    [InlineData(EvaluationFingerprint.DimensionProjectionVersion)]
    [InlineData(EvaluationFingerprint.DimensionStoreGeneration)]
    [InlineData(EvaluationFingerprint.DimensionSnapshotId)]
    [InlineData(EvaluationFingerprint.DimensionSnapshotExpiresAt)]
    [InlineData(EvaluationFingerprint.DimensionCategoryLifecycle)]
    [InlineData(EvaluationFingerprint.DimensionNormalizationVersion)]
    [InlineData(EvaluationFingerprint.DimensionRuleSetVersion)]
    [InlineData(EvaluationFingerprint.DimensionOrderedItems)]
    public void Changing_each_fingerprint_dimension_is_detectable(string dimension)
    {
        var baseline = Fingerprint();
        var mutated = MutateDimension(baseline, dimension);
        var diffs = baseline.DiffDimensions(mutated);
        Assert.Contains(dimension, diffs);
        Assert.NotEqual(baseline.CanonicalHash, mutated.CanonicalHash);
    }

    [Fact]
    public void Evaluation_stale_on_snapshot_dimension_accounts_all_rows_as_stale()
    {
        var items = ContiguousItems(7);
        var rules = new[] { MakeRule("rv-1", "cat", descriptionPrefix: null, matchAll: true) };
        var result = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(),
            items,
            rules,
            new HashSet<string>(StringComparer.Ordinal) { "cat" },
            evaluationStaleDimensions: [EvaluationFingerprint.DimensionSnapshotId]));

        Assert.Equal(7, result.StaleCount);
        Assert.Equal(7, result.InputCount);
        Assert.Equal(0, result.SuggestionCount);
        Assert.All(result.Outcomes, o => Assert.Null(o.CategoryId));
    }

    // ── Evidence and hasher properties ───────────────────────────────────────

    [Fact]
    public void Evidence_order_is_stable_under_rule_permutation()
    {
        var item = new ClassificationEvaluationItem(
            0, "tx-0", "acct", "merchant 0 and more", ClassificationRuleVocabulary.DirectionOutflow, 10, "life-0");
        var rules = new[]
        {
            MakeRule("rv-b", "cat", "merchant 0"),
            MakeRule("rv-a", "cat", "merchant 0")
        };
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat" };
        var left = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(Fingerprint(), [item], rules, active));
        var right = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(), [item], rules.Reverse().ToArray(), active));
        Assert.Equal(
            left.Outcomes[0].Evidence.Select(e => e.ToCanonicalJson()),
            right.Outcomes[0].Evidence.Select(e => e.ToCanonicalJson()));
    }

    [Fact]
    public void Canonical_hasher_is_deterministic_and_order_sensitive_for_parts()
    {
        Assert.Equal(
            CanonicalClassificationHasher.HashUtf8("abc"),
            CanonicalClassificationHasher.HashUtf8("abc"));
        Assert.NotEqual(
            CanonicalClassificationHasher.HashParts("a", "b"),
            CanonicalClassificationHasher.HashParts("b", "a"));
        Assert.Equal(64, CanonicalClassificationHasher.HashInt64(42).Length);
        Assert.Equal(
            CanonicalClassificationHasher.HashOrderedLines(["x", "y"]),
            CanonicalClassificationHasher.HashOrderedLines(["x", "y"]));
    }

    [Fact]
    public void Canonical_hasher_frames_values_without_delimiter_aliases()
    {
        Assert.NotEqual(
            CanonicalClassificationHasher.HashParts("a|b", "c"),
            CanonicalClassificationHasher.HashParts("a", "b|c"));
        Assert.NotEqual(
            CanonicalClassificationHasher.HashParts((string?)null),
            CanonicalClassificationHasher.HashParts("null"));
        Assert.NotEqual(
            CanonicalClassificationHasher.HashOrderedLines(["a\nb", "c"]),
            CanonicalClassificationHasher.HashOrderedLines(["a", "b\nc"]));
    }

    [Fact]
    public void Match_evidence_rejects_non_hex64_value_hash()
    {
        Assert.Throws<ArgumentException>(() =>
            new MatchEvidence("rv", "c", "account.id", "equals", "too-short"));
        Assert.Throws<ArgumentException>(() =>
            new MatchEvidence("rv", "c", "account.id", "equals", new string('z', 64)));
        Assert.Throws<ArgumentException>(() =>
            new MatchEvidence("rv", "c", "account.id", "equals", new string('A', 64)));
    }

    [Fact]
    public void Outcomes_canonical_hash_changes_when_any_outcome_kind_changes()
    {
        var items = ContiguousItems(2);
        var rulesSuggest = new[] { MakeRule("rv-1", "cat", descriptionPrefix: null, matchAll: true) };
        var rulesNone = Array.Empty<ActiveRuleVersion>();
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat" };
        var withRules = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(Fingerprint(), items, rulesSuggest, active));
        var without = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(Fingerprint(), items, rulesNone, active));
        Assert.NotEqual(withRules.OutcomesCanonicalHash, without.OutcomesCanonicalHash);
    }

    [Fact]
    public void Active_category_filter_is_deterministic_when_catalogue_permuted()
    {
        var items = ContiguousItems(3);
        var rules = new[]
        {
            MakeRule("rv-1", "cat-a", descriptionPrefix: null, matchAll: true),
            MakeRule("rv-2", "cat-b", descriptionPrefix: null, matchAll: true)
        };
        // Only cat-a active → only rv-1 participates → suggestion cat-a
        var left = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(), items, rules, new HashSet<string>(StringComparer.Ordinal) { "cat-a", "cat-x" }));
        var right = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(), items, rules.Reverse().ToArray(), new HashSet<string>(StringComparer.Ordinal) { "cat-x", "cat-a" }));
        Assert.Equal(left.OutcomesCanonicalHash, right.OutcomesCanonicalHash);
        Assert.All(left.Outcomes, o => Assert.Equal("cat-a", o.CategoryId));
    }

    [Fact]
    public void Semantic_identity_of_outcomes_ignores_rule_list_permutation_for_conflicts()
    {
        var item = new ClassificationEvaluationItem(
            0, "tx-0", "acct", "shared", ClassificationRuleVocabulary.DirectionOutflow, 1, "life");
        var rules = new[]
        {
            MakeRule("rv-2", "cat-2", "shared"),
            MakeRule("rv-1", "cat-1", "shared")
        };
        var active = new HashSet<string>(StringComparer.Ordinal) { "cat-1", "cat-2" };
        var a = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(Fingerprint(), [item], rules, active));
        var b = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            Fingerprint(), [item], rules.Reverse().ToArray(), active));
        Assert.Equal(ClassificationOutcomeKind.Conflict, a.Outcomes[0].Kind);
        Assert.Equal(a.Outcomes[0].ToCanonicalJson(), b.Outcomes[0].ToCanonicalJson());
        Assert.Null(a.Outcomes[0].CategoryId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ClassificationEvaluationRequest BuildMixedRequest()
    {
        var items = new[]
        {
            new ClassificationEvaluationItem(0, "tx-0", "acct", "merchant 0", ClassificationRuleVocabulary.DirectionOutflow, 10, "life-0"),
            new ClassificationEvaluationItem(1, "tx-1", "acct", "merchant 1", ClassificationRuleVocabulary.DirectionOutflow, 20, "life-1"),
            new ClassificationEvaluationItem(2, "tx-2", "acct", "other", ClassificationRuleVocabulary.DirectionInflow, 30, "life-2"),
            new ClassificationEvaluationItem(
                3, "tx-3", "acct", "merchant 0", ClassificationRuleVocabulary.DirectionOutflow, 40, "life-3",
                itemStaleDimensions: [EvaluationFingerprint.DimensionStoreGeneration])
        };
        var rules = new[]
        {
            MakeRule("rv-a", "cat-a", "merchant 0"),
            MakeRule("rv-b", "cat-b", "merchant 0"),
            MakeRule("rv-c", "cat-c", "merchant 1")
        };
        return new ClassificationEvaluationRequest(
            Fingerprint(),
            items,
            rules,
            new HashSet<string>(StringComparer.Ordinal) { "cat-a", "cat-b", "cat-c" });
    }

    private static IReadOnlyList<ClassificationEvaluationItem> ContiguousItems(int count)
    {
        var items = new ClassificationEvaluationItem[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = new ClassificationEvaluationItem(
                i,
                "tx-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "acct",
                "merchant " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClassificationRuleVocabulary.DirectionOutflow,
                100 + i,
                "life-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return items;
    }

    private static IReadOnlyList<ClassificationEvaluationItem> ContiguousParityItems(int count)
    {
        var items = new ClassificationEvaluationItem[count];
        for (var i = 0; i < count; i++)
        {
            var parity = i % 2 == 0 ? "even" : "odd";
            items[i] = new ClassificationEvaluationItem(
                i,
                "tx-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "acct",
                parity + " " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClassificationRuleVocabulary.DirectionOutflow,
                100 + i,
                "life-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return items;
    }

    private static ActiveRuleVersion MakeRule(
        string id,
        string categoryId,
        string? descriptionPrefix,
        bool matchAll = false)
    {
        if (matchAll)
        {
            return new ActiveRuleVersion(id, categoryId, [MinorBetween(0, 0, long.MaxValue)]);
        }

        Assert.False(string.IsNullOrWhiteSpace(descriptionPrefix));
        return new ActiveRuleVersion(id, categoryId, [DescriptionContains(0, descriptionPrefix!)]);
    }

    private static RuleCondition DescriptionContains(int ordinal, string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            ordinal,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.ContainsTokenSequencePredicate,
            value,
            null, null, null,
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
            null, null, null,
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
            null, min, max, null,
            out var condition,
            out _));
        return condition!;
    }

    private static EvaluationFingerprint Fingerprint() =>
        EvaluationFingerprint.Create(
            "1.0",
            "classification_v1",
            "gen-1",
            "snap-1",
            "2026-07-31T23:59:59.0000000Z",
            "cat-fp",
            NormalizationDescriptor.V1.Version,
            "rs-1",
            "items-fp");

    private static EvaluationFingerprint MutateDimension(EvaluationFingerprint baseline, string dimension) =>
        dimension switch
        {
            EvaluationFingerprint.DimensionLedgerContractVersion => EvaluationFingerprint.Create(
                "2.0", baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionProjectionVersion => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, "classification_v0", baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionStoreGeneration => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, "gen-other", baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionSnapshotId => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, "snap-other",
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionSnapshotExpiresAt => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                "2099-01-01T00:00:00.0000000Z", baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionCategoryLifecycle => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, "cat-fp-other", baseline.NormalizationVersion,
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionNormalizationVersion => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, "normalization_v0",
                baseline.RuleSetVersionId, baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionRuleSetVersion => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                "rs-other", baseline.OrderedItemsFingerprint),
            EvaluationFingerprint.DimensionOrderedItems => EvaluationFingerprint.Create(
                baseline.LedgerContractVersion, baseline.ProjectionVersion, baseline.StoreGenerationFingerprint, baseline.SnapshotId,
                baseline.SnapshotExpiresAt, baseline.CategoryLifecycleFingerprint, baseline.NormalizationVersion,
                baseline.RuleSetVersionId, "items-fp-other"),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

    /// <summary>Deterministic LCG — no System.Random (host entropy forbidden in domain tests' expectations).</summary>
    private sealed class DeterministicSequence(int seed)
    {
        private int state = seed;
        public int Next()
        {
            state = unchecked((state * 1103515245) + 12345);
            return state & 0x7FFF_FFFF;
        }
    }
}
