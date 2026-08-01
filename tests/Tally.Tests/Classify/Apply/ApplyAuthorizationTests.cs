using System.Runtime.Versioning;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Rules;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Xunit;

namespace Tally.Tests.Classify.Apply;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW / bd-gv0z — pure selection and authority matrix.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ApplyAuthorizationTests
{
    private static readonly ClassifyEvaluationRunRow Run = new(
        "eval-1",
        null,
        "rsv-1",
        "normalization_v1",
        "1.0",
        "classification_v1",
        new string('a', 64),
        "snap-1",
        "2099-01-01T00:00:00Z",
        new string('b', 64),
        new string('c', 64),
        4,
        2,
        1,
        1,
        0,
        ClassifyContractMapper.EvaluationLifecycleCompleted,
        "human:owner",
        "2026-07-31T00:00:00Z");

    private static readonly ClassifyOutcomeRow SuggestionA = Outcome("out-sug-a", 0, "tx-a", "suggestion", "cat-1");
    private static readonly ClassifyOutcomeRow SuggestionB = Outcome("out-sug-b", 1, "tx-b", "suggestion", "cat-1");
    private static readonly ClassifyOutcomeRow NoSuggestion = Outcome("out-ns", 2, "tx-c", "no_suggestion", null);
    private static readonly ClassifyOutcomeRow Conflict = Outcome("out-cf", 3, "tx-d", "conflict", null);
    private static readonly ClassifyOutcomeRow StaleKind = Outcome("out-st", 4, "tx-e", "stale", null);

    private static readonly IReadOnlyList<ClassifyOutcomeRow> AllOutcomes =
    [
        SuggestionA, SuggestionB, NoSuggestion, Conflict, StaleKind
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> Evidence =
        new Dictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>>(StringComparer.Ordinal)
        {
            ["out-sug-a"] = [EvidenceRow("out-sug-a", "rv-1")],
            ["out-sug-b"] = [EvidenceRow("out-sug-b", "rv-1"), EvidenceRow("out-sug-b", "rv-2")],
            ["out-ns"] = Array.Empty<ClassifyMatchEvidenceRow>(),
            ["out-cf"] =
            [
                EvidenceRow("out-cf", "rv-1"),
                EvidenceRow("out-cf", "rv-2")
            ],
            ["out-st"] = Array.Empty<ClassifyMatchEvidenceRow>()
        };

    // ── Mode shape ──────────────────────────────────────────────────────────

    [Fact]
    public void Mode_shape_rejects_mixed_outcomes_and_rule()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.SelectedOutcomes,
            OutcomeIds: ["out-sug-a"],
            RuleVersionId: "rv-1");
        Assert.False(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
    }

    [Fact]
    public void Mode_shape_rejects_mixed_rule_and_corrections()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExactRule,
            RuleVersionId: "rv-1",
            CorrectionItems:
            [
                new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "reason")
            ]);
        Assert.False(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
        Assert.True(ApplyAuthorizationPolicy.IsBroadCorrectionAttempt(selection));
    }

    [Fact]
    public void Mode_shape_rejects_incomplete_correction()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExplicitCorrections,
            CorrectionItems:
            [
                new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "")
            ]);
        Assert.False(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
    }

    [Fact]
    public void Mode_shape_accepts_selected_outcomes_only()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.SelectedOutcomes,
            OutcomeIds: ["out-sug-a"]);
        Assert.True(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Mode_shape_accepts_exact_rule_only()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExactRule,
            RuleVersionId: "rv-1");
        Assert.True(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out _));
    }

    [Fact]
    public void Mode_shape_accepts_complete_corrections_only()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExplicitCorrections,
            CorrectionItems:
            [
                new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "owner reason")
            ]);
        Assert.True(ApplyAuthorizationPolicy.TryValidateModeShape(selection, out _));
    }

    // ── Selected outcomes ───────────────────────────────────────────────────

    [Fact]
    public void Selected_outcomes_accepts_current_suggestions_only()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-a", "out-sug-b"]));
        Assert.True(result.IsAuthorized);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.Equal(ApplyAuthorizationPolicy.ModeAssign, c.Mode));
        Assert.Equal(
            result.Candidates.Select(c => c.TransactionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            result.Candidates.Select(c => c.TransactionId).ToArray());
    }

    [Fact]
    public void Selected_outcomes_excludes_no_suggestion_conflict_stale_kinds()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-a", "out-ns", "out-cf", "out-st"]));
        Assert.True(result.IsAuthorized);
        Assert.Single(result.Candidates);
        Assert.Equal("out-sug-a", result.Candidates[0].OutcomeId);
        Assert.Equal(3, result.ExclusionCount);
        Assert.Equal(1, result.ExcludedNoSuggestionCount);
        Assert.Equal(1, result.ExcludedConflictCount);
        Assert.Equal(1, result.ExcludedStaleKindCount);
    }

    [Fact]
    public void Selected_outcomes_rejects_when_only_excluded_partitions_selected()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-ns", "out-cf"]));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Selected_outcomes_rejects_unknown_outcome_id()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-missing"]));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Selected_outcomes_rejects_empty_outcome_list()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: Array.Empty<string>()));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Selected_outcomes_is_deterministic_by_transaction_id()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-b", "out-sug-a"]));
        Assert.True(result.IsAuthorized);
        Assert.Equal(["tx-a", "tx-b"], result.Candidates.Select(c => c.TransactionId).ToArray());
    }

    // ── Exact rule / broad authority ────────────────────────────────────────

    [Fact]
    public void Exact_rule_rejects_without_broad_apply_authority()
    {
        var result = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"),
            broadApply: new HashSet<string>(StringComparer.Ordinal),
            active: new HashSet<string>(StringComparer.Ordinal) { "rv-1" });
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
    }

    [Fact]
    public void Exact_rule_rejects_when_rule_not_in_active_set()
    {
        var result = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"),
            broadApply: new HashSet<string>(StringComparer.Ordinal) { "rv-1" },
            active: new HashSet<string>(StringComparer.Ordinal));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
    }

    [Fact]
    public void Exact_rule_with_broad_authority_selects_only_its_assignment_suggestions()
    {
        var result = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"),
            broadApply: new HashSet<string>(StringComparer.Ordinal) { "rv-1" },
            active: new HashSet<string>(StringComparer.Ordinal) { "rv-1", "rv-2" });
        Assert.True(result.IsAuthorized, result.ErrorCode);
        Assert.True(result.BroadAuthorityGranted);
        Assert.Equal("rv-1", result.ExactRuleVersionId);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(ApplyAuthorizationPolicy.ModeAssign, c.Mode);
            Assert.Equal("rv-1", c.RuleVersionId);
        });
        Assert.True(result.ExclusionCount >= 3); // ns + conflict + stale
    }

    [Fact]
    public void Exact_rule_rv2_only_matches_suggestion_b()
    {
        var result = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-2"),
            broadApply: new HashSet<string>(StringComparer.Ordinal) { "rv-2" },
            active: new HashSet<string>(StringComparer.Ordinal) { "rv-1", "rv-2" });
        Assert.True(result.IsAuthorized);
        Assert.Single(result.Candidates);
        Assert.Equal("out-sug-b", result.Candidates[0].OutcomeId);
    }

    [Fact]
    public void Exact_rule_never_emits_correction_mode()
    {
        var result = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"),
            broadApply: new HashSet<string>(StringComparer.Ordinal) { "rv-1" },
            active: new HashSet<string>(StringComparer.Ordinal) { "rv-1" });
        Assert.True(result.IsAuthorized);
        Assert.DoesNotContain(result.Candidates, c => c.Mode == ApplyAuthorizationPolicy.ModeCorrect);
    }

    // ── Explicit corrections ────────────────────────────────────────────────

    [Fact]
    public void Explicit_corrections_require_complete_binding()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.ExplicitCorrections,
                CorrectionItems:
                [
                    new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "fix")
                ]));
        Assert.True(result.IsAuthorized);
        Assert.Single(result.Candidates);
        Assert.Equal(ApplyAuthorizationPolicy.ModeCorrect, result.Candidates[0].Mode);
        Assert.Equal("cat-new", result.Candidates[0].TargetCategoryId);
        Assert.Equal("cat-old", result.Candidates[0].ExpectedCurrentCategoryId);
        Assert.Equal("fix", result.Candidates[0].CorrectionReason);
        Assert.Null(result.Candidates[0].RuleVersionId);
    }

    [Fact]
    public void Explicit_corrections_reject_transaction_outcome_mismatch()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.ExplicitCorrections,
                CorrectionItems:
                [
                    new ClassifyExplicitCorrectionItem("tx-wrong", "out-sug-a", "cat-old", "cat-new", "fix")
                ]));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Explicit_corrections_reject_same_current_and_target()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.ExplicitCorrections,
                CorrectionItems:
                [
                    new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-1", "cat-1", "noop")
                ]));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Explicit_corrections_reject_duplicate_transaction()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.ExplicitCorrections,
                CorrectionItems:
                [
                    new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "r1"),
                    new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-other", "r2")
                ]));
        Assert.False(result.IsAuthorized);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Explicit_corrections_order_by_transaction_id()
    {
        var result = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.ExplicitCorrections,
                CorrectionItems:
                [
                    new ClassifyExplicitCorrectionItem("tx-b", "out-sug-b", "cat-old", "cat-new", "r2"),
                    new ClassifyExplicitCorrectionItem("tx-a", "out-sug-a", "cat-old", "cat-new", "r1")
                ]));
        Assert.True(result.IsAuthorized);
        Assert.Equal(["tx-a", "tx-b"], result.Candidates.Select(c => c.TransactionId).ToArray());
    }

    // ── Broad-apply lifecycle authority ─────────────────────────────────────

    [Fact]
    public void Broad_apply_authority_false_by_default_without_events()
    {
        Assert.False(ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(
            Array.Empty<ClassifyRuleLifecycleEventRow>()));
    }

    [Fact]
    public void Broad_apply_authority_true_when_latest_state_is_active_with_broad_apply()
    {
        var events = new[]
        {
            new ClassifyRuleLifecycleEventRow(
                "e1", "rv-1", "draft", RuleLifecyclePolicy.StateActiveBroadApply, null,
                "activate", "human:owner", "2026-07-31T00:00:00Z")
        };
        Assert.True(ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(events));
    }

    [Fact]
    public void Broad_apply_authority_false_when_latest_state_is_plain_active()
    {
        var events = new[]
        {
            new ClassifyRuleLifecycleEventRow(
                "e1", "rv-1", "draft", RuleLifecyclePolicy.StateActive, null,
                "activate", "human:owner", "2026-07-31T00:00:00Z")
        };
        Assert.False(ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(events));
    }

    [Fact]
    public void Broad_apply_authority_false_after_retire_supersedes_broad_grant()
    {
        var events = new[]
        {
            new ClassifyRuleLifecycleEventRow(
                "e1", "rv-1", "draft", RuleLifecyclePolicy.StateActiveBroadApply, null,
                "activate", "human:owner", "2026-07-31T00:00:00Z"),
            new ClassifyRuleLifecycleEventRow(
                "e2", "rv-1", RuleLifecyclePolicy.StateActiveBroadApply, RuleLifecyclePolicy.StateRetired, "rsv-2",
                "retire", "human:owner", "2026-07-31T01:00:00Z")
        };
        Assert.False(ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(events));
    }

    // ── Mapper pure helpers ─────────────────────────────────────────────────

    [Fact]
    public void Selection_hash_is_stable_and_64_hex()
    {
        var selection = new ClassifyApplySelection(
            ClassifyApplySelectionMode.SelectedOutcomes,
            OutcomeIds: ["out-sug-b", "out-sug-a"]);
        var h1 = ClassifyContractMapper.ComputeSelectionHash(selection);
        var h2 = ClassifyContractMapper.ComputeSelectionHash(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-a", "out-sug-b"]));
        Assert.Equal(64, h1.Length);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Selection_hash_differs_by_mode()
    {
        var outcomes = ClassifyContractMapper.ComputeSelectionHash(
            new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ["out-sug-a"]));
        var rule = ClassifyContractMapper.ComputeSelectionHash(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"));
        Assert.NotEqual(outcomes, rule);
    }

    [Fact]
    public void Rule_authority_fingerprint_marks_broad_exact_rule()
    {
        var auth = Authorize(
            new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: "rv-1"),
            broadApply: new HashSet<string>(StringComparer.Ordinal) { "rv-1" },
            active: new HashSet<string>(StringComparer.Ordinal) { "rv-1" });
        var fp = ClassifyContractMapper.ComputeRuleAuthorityFingerprint(auth);
        Assert.Equal(64, fp.Length);
        var noAuth = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-a"]));
        Assert.NotEqual(fp, ClassifyContractMapper.ComputeRuleAuthorityFingerprint(noAuth));
    }

    [Fact]
    public void Target_category_fingerprint_orders_by_transaction()
    {
        var auth = Authorize(
            new ClassifyApplySelection(
                ClassifyApplySelectionMode.SelectedOutcomes,
                OutcomeIds: ["out-sug-a", "out-sug-b"]));
        var fp = ClassifyContractMapper.ComputeTargetCategoryFingerprint(auth.Candidates);
        Assert.Equal(64, fp.Length);
    }

    [Fact]
    public void Format_selection_mode_wire_values()
    {
        Assert.Equal("selected_outcomes", ClassifyContractMapper.FormatSelectionMode(ClassifyApplySelectionMode.SelectedOutcomes));
        Assert.Equal("exact_rule", ClassifyContractMapper.FormatSelectionMode(ClassifyApplySelectionMode.ExactRule));
        Assert.Equal("explicit_corrections", ClassifyContractMapper.FormatSelectionMode(ClassifyApplySelectionMode.ExplicitCorrections));
    }

    [Fact]
    public void Preflight_match_assignable_requires_lifecycle_and_assignable_state()
    {
        var candidate = new ApplyAuthorizationPolicy.AuthorizedCandidate(
            "out-sug-a", "tx-a", ApplyAuthorizationPolicy.ModeAssign, "cat-1", "rv-1",
            null, null, "life-1", 0);
        var item = new Contracts.Ledger.Actuals.ClassificationProjectionItem(
            0, "tx-a", "acct", "2026-07-15", "-1.00", "desc",
            Contracts.Ledger.Actuals.ClassificationAmountDirection.Expense,
            Contracts.Ledger.Actuals.CategoryMutationState.Assignable,
            null, null, "tr", "rr", "ar");
        // lifecycle of item = hash(tr,rr,ar) — force match by computing
        var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
        candidate = candidate with { RetainedItemLifecycleFingerprint = life };
        Assert.True(ClassifyContractMapper.TryMatchPreflightItem(
            candidate, item, false, new HashSet<string>(StringComparer.Ordinal) { "cat-1" }, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Preflight_match_rejects_ineligible()
    {
        var item = new Contracts.Ledger.Actuals.ClassificationProjectionItem(
            0, "tx-a", "acct", "2026-07-15", "-1.00", "desc",
            Contracts.Ledger.Actuals.ClassificationAmountDirection.Expense,
            Contracts.Ledger.Actuals.CategoryMutationState.Ineligible,
            null, null, "tr", "rr", "ar");
        var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
        var candidate = new ApplyAuthorizationPolicy.AuthorizedCandidate(
            "out-sug-a", "tx-a", ApplyAuthorizationPolicy.ModeAssign, "cat-1", "rv-1",
            null, null, life, 0);
        Assert.False(ClassifyContractMapper.TryMatchPreflightItem(
            candidate, item, false, new HashSet<string>(StringComparer.Ordinal) { "cat-1" }, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
    }

    [Fact]
    public void Preflight_match_rejects_missing()
    {
        var candidate = new ApplyAuthorizationPolicy.AuthorizedCandidate(
            "out-sug-a", "tx-a", ApplyAuthorizationPolicy.ModeAssign, "cat-1", "rv-1",
            null, null, "life", 0);
        Assert.False(ClassifyContractMapper.TryMatchPreflightItem(
            candidate, null, true, new HashSet<string>(StringComparer.Ordinal) { "cat-1" }, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
    }

    [Fact]
    public void Preflight_match_correct_requires_current_category_and_allocation()
    {
        var item = new Contracts.Ledger.Actuals.ClassificationProjectionItem(
            0, "tx-a", "acct", "2026-07-15", "-1.00", "desc",
            Contracts.Ledger.Actuals.ClassificationAmountDirection.Expense,
            Contracts.Ledger.Actuals.CategoryMutationState.Correctable,
            "cat-old", "alloc-1", "tr", "rr", "ar");
        var candidate = new ApplyAuthorizationPolicy.AuthorizedCandidate(
            "out-sug-a", "tx-a", ApplyAuthorizationPolicy.ModeCorrect, "cat-new", null,
            "cat-old", "reason", "ignored-life", 0);
        Assert.True(ClassifyContractMapper.TryMatchPreflightItem(
            candidate, item, false, new HashSet<string>(StringComparer.Ordinal) { "cat-new" }, out _));
    }

    [Fact]
    public void Preflight_match_correct_rejects_current_category_mismatch()
    {
        var item = new Contracts.Ledger.Actuals.ClassificationProjectionItem(
            0, "tx-a", "acct", "2026-07-15", "-1.00", "desc",
            Contracts.Ledger.Actuals.ClassificationAmountDirection.Expense,
            Contracts.Ledger.Actuals.CategoryMutationState.Correctable,
            "cat-other", "alloc-1", "tr", "rr", "ar");
        var candidate = new ApplyAuthorizationPolicy.AuthorizedCandidate(
            "out-sug-a", "tx-a", ApplyAuthorizationPolicy.ModeCorrect, "cat-new", null,
            "cat-old", "reason", "life", 0);
        Assert.False(ClassifyContractMapper.TryMatchPreflightItem(
            candidate, item, false, new HashSet<string>(StringComparer.Ordinal) { "cat-new" }, out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ApplyAuthorizationPolicy.AuthorizationResult Authorize(
        ClassifyApplySelection selection,
        IReadOnlySet<string>? broadApply = null,
        IReadOnlySet<string>? active = null) =>
        ApplyAuthorizationPolicy.Authorize(
            selection,
            Run,
            AllOutcomes,
            Evidence,
            broadApply ?? new HashSet<string>(StringComparer.Ordinal),
            active ?? new HashSet<string>(StringComparer.Ordinal) { "rv-1", "rv-2" });

    private static ClassifyOutcomeRow Outcome(
        string id, int ordinal, string tx, string type, string? category) =>
        new(id, "eval-1", ordinal, tx, type, category, "life-" + id, type);

    private static ClassifyMatchEvidenceRow EvidenceRow(string outcomeId, string ruleVersionId) =>
        new(outcomeId, ruleVersionId, "cond-0", "description.normalized", "equals", new string('f', 64));
}
