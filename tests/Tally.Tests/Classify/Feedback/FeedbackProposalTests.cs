using System.Runtime.Versioning;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Feedback;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Xunit;

namespace Tally.Tests.Classify.Feedback;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-FEEDBACK-PROPOSALS / bd-3tzh — pure bounded proposal policy.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class FeedbackProposalTests
{
    private static readonly ClassifyRuleVersionRow SourceRule = new(
        "rv-1",
        "rule-1",
        null,
        "normalization_v1",
        "cat-source",
        new string('a', 64),
        "owner_authored",
        null,
        "draft reason",
        "draft",
        0,
        null,
        "2026-07-31T00:00:00Z",
        "human:owner");

    private static readonly IReadOnlyList<ClassifyMatchEvidenceRow> SingleConditionEvidence =
    [
        new("out-1", "rv-1", "cond-0", "description.normalized", "equals", new string('f', 64))
    ];

    private static readonly IReadOnlyList<ClassifyMatchEvidenceRow> MultiConditionEvidence =
    [
        new("out-1", "rv-1", "cond-0", "description.normalized", "equals", new string('f', 64)),
        new("out-1", "rv-1", "cond-1", "account.id", "equals", new string('e', 64))
    ];

    private static readonly IReadOnlyList<ClassifyMatchEvidenceRow> MultiRuleEvidence =
    [
        new("out-1", "rv-1", "cond-0", "description.normalized", "equals", new string('f', 64)),
        new("out-1", "rv-2", "cond-0", "description.normalized", "equals", new string('d', 64))
    ];

    [Fact]
    public void Accept_never_emits_proposal()
    {
        var result = Build(ClassifyFeedbackDecision.Accepted, true, SingleConditionEvidence, "cat-new");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal(FeedbackProposalBuilder.ProposalTypeNone, result.ProposalTypeWire);
        Assert.Null(result.SourceRuleVersionId);
        Assert.Equal(64, result.ProposedScopeFingerprint.Length);
    }

    [Fact]
    public void Reject_never_emits_proposal()
    {
        var result = Build(ClassifyFeedbackDecision.Rejected, true, SingleConditionEvidence, null);
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Contains("accept_or_reject", result.DecisionCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Correct_without_evidence_emits_none()
    {
        var result = Build(
            ClassifyFeedbackDecision.Corrected,
            evidenceAvailable: false,
            Array.Empty<ClassifyMatchEvidenceRow>(),
            "cat-new");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("missing_match_evidence", result.DecisionCode);
    }

    [Fact]
    public void Correct_empty_evidence_list_emits_none()
    {
        var result = Build(
            ClassifyFeedbackDecision.Corrected,
            evidenceAvailable: true,
            Array.Empty<ClassifyMatchEvidenceRow>(),
            "cat-new");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
    }

    [Fact]
    public void Correct_without_rebound_allocation_authority_emits_none()
    {
        var result = FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
            ClassifyFeedbackDecision.Corrected,
            ClassificationOutcomeKind.Suggestion,
            true,
            SingleConditionEvidence,
            new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal) { ["rv-1"] = SourceRule },
            "cat-target",
            CorrectionAllocationsComplete: false));
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("correction_authority_unavailable", result.DecisionCode);
    }

    [Fact]
    public void Correct_multi_rule_is_not_generalized()
    {
        var rules = new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal)
        {
            ["rv-1"] = SourceRule,
            ["rv-2"] = SourceRule with { RuleVersionId = "rv-2", RuleId = "rule-2" }
        };
        var result = FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
            ClassifyFeedbackDecision.Corrected,
            ClassificationOutcomeKind.Suggestion,
            true,
            MultiRuleEvidence,
            rules,
            "cat-new",
            true));
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("multi_rule_not_generalized", result.DecisionCode);
    }

    [Fact]
    public void Correct_single_rule_different_category_is_replace()
    {
        var result = Build(ClassifyFeedbackDecision.Corrected, true, SingleConditionEvidence, "cat-target");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.Replace, result.Kind);
        Assert.Equal(FeedbackProposalBuilder.ProposalTypeReplace, result.ProposalTypeWire);
        Assert.Equal("rv-1", result.SourceRuleVersionId);
        Assert.Equal(SourceRule.ScopeHash, result.ProposedScopeFingerprint);
        Assert.Equal("cat-target", result.ProposedCategoryId);
        Assert.Equal("replace_category_same_scope", result.DecisionCode);
    }

    [Fact]
    public void Correct_single_rule_no_resulting_category_is_retire()
    {
        var result = Build(ClassifyFeedbackDecision.Corrected, true, SingleConditionEvidence, resultingCategoryId: null);
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.Retire, result.Kind);
        Assert.Equal(FeedbackProposalBuilder.ProposalTypeRetire, result.ProposalTypeWire);
        Assert.Equal("rv-1", result.SourceRuleVersionId);
        Assert.Equal(SourceRule.ScopeHash, result.ProposedScopeFingerprint);
        Assert.Null(result.ProposedCategoryId);
    }

    [Fact]
    public void Correct_same_category_multi_condition_does_not_broaden_by_dropping_an_and_condition()
    {
        var result = Build(
            ClassifyFeedbackDecision.Corrected,
            true,
            MultiConditionEvidence,
            resultingCategoryId: "cat-source");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("narrowing_not_proven", result.DecisionCode);
    }

    [Fact]
    public void Correct_same_category_single_condition_is_transaction_specific_none()
    {
        var result = Build(
            ClassifyFeedbackDecision.Corrected,
            true,
            SingleConditionEvidence,
            resultingCategoryId: "cat-source");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("transaction_specific_same_category", result.DecisionCode);
    }

    [Fact]
    public void Correct_missing_source_rule_row_is_none()
    {
        var result = FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
            ClassifyFeedbackDecision.Corrected,
            ClassificationOutcomeKind.Suggestion,
            true,
            SingleConditionEvidence,
            new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal),
            "cat-new",
            true));
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
        Assert.Equal("source_rule_unavailable", result.DecisionCode);
    }

    [Fact]
    public void Evidence_available_requires_suggestion_with_rows()
    {
        Assert.True(FeedbackProposalBuilder.IsEvidenceAvailable(
            ClassificationOutcomeKind.Suggestion, SingleConditionEvidence));
        Assert.False(FeedbackProposalBuilder.IsEvidenceAvailable(
            ClassificationOutcomeKind.Suggestion, Array.Empty<ClassifyMatchEvidenceRow>()));
        Assert.False(FeedbackProposalBuilder.IsEvidenceAvailable(
            ClassificationOutcomeKind.NoSuggestion, SingleConditionEvidence));
        Assert.True(FeedbackProposalBuilder.IsEvidenceAvailable(
            ClassificationOutcomeKind.Conflict, MultiRuleEvidence));
        Assert.False(FeedbackProposalBuilder.IsEvidenceAvailable(
            ClassificationOutcomeKind.Conflict, SingleConditionEvidence));
    }

    [Fact]
    public void Active_proposal_kinds_are_only_retire_narrow_replace()
    {
        Assert.False(FeedbackProposalBuilder.IsActiveProposal(FeedbackProposalBuilder.ProposalKind.None));
        Assert.True(FeedbackProposalBuilder.IsActiveProposal(FeedbackProposalBuilder.ProposalKind.Retire));
        Assert.True(FeedbackProposalBuilder.IsActiveProposal(FeedbackProposalBuilder.ProposalKind.Narrow));
        Assert.True(FeedbackProposalBuilder.IsActiveProposal(FeedbackProposalBuilder.ProposalKind.Replace));
    }

    [Fact]
    public void Format_proposal_type_wire_values()
    {
        Assert.Equal("none", FeedbackProposalBuilder.FormatProposalType(FeedbackProposalBuilder.ProposalKind.None));
        Assert.Equal("retire", FeedbackProposalBuilder.FormatProposalType(FeedbackProposalBuilder.ProposalKind.Retire));
        Assert.Equal("narrow", FeedbackProposalBuilder.FormatProposalType(FeedbackProposalBuilder.ProposalKind.Narrow));
        Assert.Equal("replace", FeedbackProposalBuilder.FormatProposalType(FeedbackProposalBuilder.ProposalKind.Replace));
    }

    [Fact]
    public void Replace_never_broadens_scope_hash()
    {
        var result = Build(ClassifyFeedbackDecision.Corrected, true, SingleConditionEvidence, "cat-other");
        Assert.Equal(SourceRule.ScopeHash, result.ProposedScopeFingerprint);
    }

    [Fact]
    public void Unproven_narrowing_decision_is_deterministic()
    {
        var a = Build(ClassifyFeedbackDecision.Corrected, true, MultiConditionEvidence, "cat-source");
        var b = Build(ClassifyFeedbackDecision.Corrected, true, MultiConditionEvidence, "cat-source");
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, a.Kind);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Mapper_format_feedback_decision()
    {
        Assert.Equal("accept", ClassifyContractMapper.FormatFeedbackDecision(ClassifyFeedbackDecision.Accepted));
        Assert.Equal("reject", ClassifyContractMapper.FormatFeedbackDecision(ClassifyFeedbackDecision.Rejected));
        Assert.Equal("correct", ClassifyContractMapper.FormatFeedbackDecision(ClassifyFeedbackDecision.Corrected));
    }

    [Fact]
    public void Mapper_correction_allocations_require_exactly_two_refs()
    {
        Assert.True(ClassifyContractMapper.TryResolveCorrectionAllocations(
            ["prior-1", "result-1"], "prior-1", "result-1", out var prior, out var resulting, out var error));
        Assert.Equal("prior-1", prior);
        Assert.Equal("result-1", resulting);
        Assert.Null(error);

        Assert.False(ClassifyContractMapper.TryResolveCorrectionAllocations(
            ["only-one"], "prior-1", "result-1", out _, out _, out error));
        Assert.Equal(ClassifyErrors.InvalidInput, error);

        Assert.False(ClassifyContractMapper.TryResolveCorrectionAllocations(
            ["a", "a"], "prior-1", "result-1", out _, out _, out error));
        Assert.Equal(ClassifyErrors.InvalidInput, error);
    }

    [Fact]
    public void Mapper_fingerprint_preserves_prior_resulting_order()
    {
        var forward = ClassifyContractMapper.ToFeedbackFingerprintElement(
            ClassifyOperationIds.ContractVersion,
            "out-1",
            ClassifyFeedbackDecision.Corrected,
            "reason",
            ["prior-1", "result-1"]);
        var reversed = ClassifyContractMapper.ToFeedbackFingerprintElement(
            ClassifyOperationIds.ContractVersion,
            "out-1",
            ClassifyFeedbackDecision.Corrected,
            "reason",
            ["result-1", "prior-1"]);

        Assert.NotEqual(forward.GetRawText(), reversed.GetRawText());
    }

    [Fact]
    public void Mapper_correction_allocations_from_apply_item()
    {
        Assert.True(ClassifyContractMapper.TryResolveCorrectionAllocations(
            null, "prior-x", "result-y", out var prior, out var resulting, out _));
        Assert.Equal("prior-x", prior);
        Assert.Equal("result-y", resulting);

        Assert.False(ClassifyContractMapper.TryResolveCorrectionAllocations(
            null, null, "result-y", out _, out _, out var error));
        Assert.Equal(ClassifyErrors.InvalidInput, error);

        Assert.False(ClassifyContractMapper.TryResolveCorrectionAllocations(
            ["other", "result-y"], "prior-x", "result-y", out _, out _, out error));
        Assert.Equal(ClassifyErrors.InvalidInput, error);
    }

    [Fact]
    public void Mapper_to_proposal_row_null_for_none()
    {
        var none = Build(ClassifyFeedbackDecision.Accepted, true, SingleConditionEvidence, null);
        Assert.Null(ClassifyContractMapper.ToProposalRow("p1", "f1", none, "2026-07-31T00:00:00Z"));
    }

    [Fact]
    public void Mapper_to_proposal_row_stays_draft_feedback_derived()
    {
        var replace = Build(ClassifyFeedbackDecision.Corrected, true, SingleConditionEvidence, "cat-t");
        var row = ClassifyContractMapper.ToProposalRow("p1", "f1", replace, "2026-07-31T00:00:00Z");
        Assert.NotNull(row);
        Assert.Equal("draft", row!.LifecycleState);
        Assert.Equal("feedback_derived", row.RuleOrigin);
        Assert.Equal("replace", row.ProposalType);
        Assert.Equal("rv-1", row.SourceRuleVersionId);
    }

    [Fact]
    public void No_proposal_never_activates()
    {
        var result = Build(ClassifyFeedbackDecision.Accepted, true, SingleConditionEvidence, null);
        Assert.False(FeedbackProposalBuilder.IsActiveProposal(result.Kind));
        Assert.NotEqual("active", result.ProposalTypeWire);
    }

    [Fact]
    public void Conflict_outcome_with_multi_rule_evidence_still_not_generalized_on_correct()
    {
        var rules = new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal)
        {
            ["rv-1"] = SourceRule,
            ["rv-2"] = SourceRule with { RuleVersionId = "rv-2" }
        };
        var result = FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
            ClassifyFeedbackDecision.Corrected,
            ClassificationOutcomeKind.Conflict,
            true,
            MultiRuleEvidence,
            rules,
            "cat-new",
            true));
        Assert.Equal(FeedbackProposalBuilder.ProposalKind.None, result.Kind);
    }

    private static FeedbackProposalBuilder.Result Build(
        ClassifyFeedbackDecision decision,
        bool evidenceAvailable,
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence,
        string? resultingCategoryId) =>
        FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
            decision,
            ClassificationOutcomeKind.Suggestion,
            evidenceAvailable,
            evidence,
            new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal) { ["rv-1"] = SourceRule },
            resultingCategoryId,
            CorrectionAllocationsComplete: true));
}
