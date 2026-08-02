using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// UC-CLASSIFY-005 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-005 / bd-1xok
/// VerifiedClassifyUc005 — published-boundary acceptance for bounded correction feedback.
///
/// All CLASSIFY operations under test go through TallyProcess
/// (evaluate / outcome.get / apply.preview / apply.run / feedback.record / rule.* / status).
/// Ledger allocations are the authoritative mutation oracle. Feedback provenance is proven
/// via public feedback results + classify.status — never private storage or private fixtures.
/// </summary>
[Collection(ClassifyUc005Collection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc005FeedbackTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc005-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        ledger = new LedgerContractClient(registry, bootstrap);
        var classify = await ClassifyOperationBundle.CreateServicesAsync(
            root, ledger, cancellationToken: CancellationToken.None);
        services = services with { Classify = classify.Operations };
        process = new TallyProcess(registry, services);
        accountId = await CreateAccountAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Accept / reject provenance ───────────────────────────────────────────

    [Fact]
    public async Task UC005_accept_records_exact_outcome_and_rule_provenance_without_proposal()
    {
        var seeded = await SeedSuggestionAsync("uc005 accept shop");
        var before = await CaptureImmutabilityAsync(seeded);

        var feedback = await FeedbackAsync(seeded.OutcomeId, "accepted", reason: "uc005 accept");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var body = doc.RootElement.GetProperty("result_or_error");
        var feedbackId = body.GetProperty("feedbackId").GetString()!;
        Assert.Equal(seeded.OutcomeId, body.GetProperty("outcomeId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("proposalId").ValueKind);

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var fb = statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback");
        Assert.Equal(feedbackId, fb.GetProperty("feedbackId").GetString());
        Assert.Equal(seeded.OutcomeId, fb.GetProperty("outcomeId").GetString());
        Assert.Equal("accept", fb.GetProperty("decisionType").GetString());
        Assert.Equal(JsonValueKind.Null, fb.GetProperty("proposalId").ValueKind);
        var ruleVersions = fb.GetProperty("ruleVersionIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(seeded.RuleVersionId, ruleVersions);
        Assert.False(string.IsNullOrWhiteSpace(fb.GetProperty("actorId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(fb.GetProperty("occurredAt").GetString()));

        // Outcome remains the exact evaluation/transaction provenance anchor.
        var outcome = await OutcomeGetAsync(seeded.EvaluationId, seeded.TransactionId);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var outDoc = ParseResult(outcome);
        var outBody = outDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(seeded.OutcomeId, outBody.GetProperty("outcomeId").GetString());
        Assert.Equal(seeded.EvaluationId, outBody.GetProperty("evaluationId").GetString());
        Assert.Equal(seeded.TransactionId, outBody.GetProperty("transactionId").GetString());
        Assert.Equal(NormalizationDescriptor.V1.Version, outBody.GetProperty("normalizationVersion").GetString());
        Assert.Equal(seeded.RuleSetVersionId, outBody.GetProperty("ruleSetVersionId").GetString());
        var contributing = outBody.GetProperty("contributingRuleVersionIds").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(seeded.RuleVersionId, contributing);

        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC005_reject_records_without_proposal_and_without_mutation()
    {
        var seeded = await SeedSuggestionAsync("uc005 reject shop");
        var before = await CaptureImmutabilityAsync(seeded);

        var feedback = await FeedbackAsync(seeded.OutcomeId, "rejected", reason: "uc005 reject");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("proposalId").ValueKind);
        var feedbackId = body.GetProperty("feedbackId").GetString()!;

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        Assert.Equal(
            "reject",
            statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("decisionType").GetString());

        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC005_accept_never_invents_rule_generalization_from_one_outcome()
    {
        var seeded = await SeedSuggestionAsync("uc005 no generalize");
        var before = await CaptureImmutabilityAsync(seeded);

        var feedback = await FeedbackAsync(seeded.OutcomeId, "accepted", reason: "uc005 single accept");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result_or_error").GetProperty("proposalId").ValueKind);

        // Active pointer and rule identity unchanged — no automatic draft/replacement.
        await AssertUnchangedAsync(before);
        var ruleStatus = await StatusAsync("rule", seeded.RuleVersionId);
        AssertClassifySuccess(ruleStatus, ClassifyOperationIds.Status);
        using var ruleDoc = ParseResult(ruleStatus);
        Assert.Equal(
            before.ActiveRuleSetVersionId,
            ruleDoc.RootElement.GetProperty("result_or_error").GetProperty("rule")
                .GetProperty("activeRuleSetVersionId").GetString());
    }

    // ── Correction + allocations ─────────────────────────────────────────────

    [Fact]
    public async Task UC005_correction_retains_authoritative_prior_and_resulting_allocations()
    {
        var seeded = await SeedAppliedSuggestionAsync("uc005 corr alloc");
        Assert.False(string.IsNullOrWhiteSpace(seeded.ResultingAllocationId));
        var priorAlloc = seeded.ResultingAllocationId!;

        // Explicit CLASSIFY correction to a different category through public apply.
        var catB = await CreateCategoryAsync("Uc005CorrB");
        var (newAlloc, _) = await ApplyExplicitCorrectionAsync(
            seeded, priorAlloc, catB, "uc005 owner correction");

        var ledgerBeforeFeedback = await CurrentAllocationAsync(seeded.TransactionId);
        Assert.Equal(newAlloc, ledgerBeforeFeedback);
        var pointerBefore = await RequireActiveRuleSetVersionIdAsync(seeded.RuleVersionId);

        // Feedback records correction with authoritative Ledger allocation refs (not rewritten).
        var feedback = await FeedbackAsync(
            seeded.OutcomeId,
            "corrected",
            reason: "uc005 correction provenance",
            allocationRefs: [priorAlloc, newAlloc]);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var body = doc.RootElement.GetProperty("result_or_error");
        var feedbackId = body.GetProperty("feedbackId").GetString()!;
        Assert.Equal(seeded.OutcomeId, body.GetProperty("outcomeId").GetString());

        // Ledger allocation is unchanged by feedback recording.
        Assert.Equal(newAlloc, await CurrentAllocationAsync(seeded.TransactionId));
        Assert.Equal(pointerBefore, await RequireActiveRuleSetVersionIdAsync(seeded.RuleVersionId));

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var fb = statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback");
        Assert.Equal("correct", fb.GetProperty("decisionType").GetString());
        Assert.Contains(
            seeded.RuleVersionId,
            fb.GetProperty("ruleVersionIds").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public async Task UC005_one_correction_yields_no_proposal_or_one_smallest_draft()
    {
        var seeded = await SeedAppliedSuggestionAsync("uc005 one proposal");
        var priorAlloc = seeded.ResultingAllocationId!;
        var catB = await CreateCategoryAsync("Uc005PropB");
        var (newAlloc, _) = await ApplyExplicitCorrectionAsync(
            seeded, priorAlloc, catB, "uc005 proposal correction");

        var pointerBefore = await RequireActiveRuleSetVersionIdAsync(seeded.RuleVersionId);
        var ledgerBefore = await LedgerFingerprintAsync();

        var feedback = await FeedbackAsync(
            seeded.OutcomeId,
            "corrected",
            reason: "uc005 one correction",
            allocationRefs: [priorAlloc, newAlloc]);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var body = doc.RootElement.GetProperty("result_or_error");
        var feedbackId = body.GetProperty("feedbackId").GetString()!;
        var proposalId = body.GetProperty("proposalId");

        if (proposalId.ValueKind != JsonValueKind.Null)
        {
            var status = await StatusAsync("feedback", feedbackId);
            AssertClassifySuccess(status, ClassifyOperationIds.Status);
            using var statusDoc = ParseResult(status);
            var fb = statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback");
            Assert.Equal(proposalId.GetString(), fb.GetProperty("proposalId").GetString());
            // Exactly one non-active draft proposal (never active / never broadened).
            Assert.Equal("draft", fb.GetProperty("proposalLifecycleState").GetString());
            Assert.NotEqual("active", fb.GetProperty("proposalLifecycleState").GetString());
        }

        Assert.Equal(pointerBefore, await RequireActiveRuleSetVersionIdAsync(seeded.RuleVersionId));
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
        Assert.Equal(newAlloc, await CurrentAllocationAsync(seeded.TransactionId));
    }

    [Fact]
    public async Task UC005_proposal_when_present_never_activates_or_broadens()
    {
        var seeded = await SeedAppliedSuggestionAsync("uc005 no auto");
        var priorAlloc = seeded.ResultingAllocationId!;
        var catB = await CreateCategoryAsync("Uc005NoAutoB");
        var (newAlloc, _) = await ApplyExplicitCorrectionAsync(
            seeded, priorAlloc, catB, "uc005 no auto corr");
        var before = await CaptureImmutabilityAsync(seeded);

        var feedback = await FeedbackAsync(
            seeded.OutcomeId,
            "corrected",
            reason: "uc005 no auto activate",
            allocationRefs: [priorAlloc, newAlloc]);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var feedbackId = doc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;

        // Feedback alone never moves the active pointer and never broadens.
        await AssertUnchangedAsync(before);
        var ruleStatus = await StatusAsync("rule", seeded.RuleVersionId);
        AssertClassifySuccess(ruleStatus, ClassifyOperationIds.Status);
        using var ruleDoc = ParseResult(ruleStatus);
        // Activated rule remains non-broad unless explicitly activated with broad authority earlier.
        // Baseline seed uses broadApply=false.
        Assert.Equal(
            before.ActiveRuleSetVersionId,
            ruleDoc.RootElement.GetProperty("result_or_error").GetProperty("rule")
                .GetProperty("activeRuleSetVersionId").GetString());

        var fbStatus = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(fbStatus, ClassifyOperationIds.Status);
        using var fbDoc = ParseResult(fbStatus);
        var proposalState = fbDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback")
            .GetProperty("proposalLifecycleState");
        if (proposalState.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal("draft", proposalState.GetString());
        }
    }

    // ── Missing evidence / reject paths ──────────────────────────────────────

    [Fact]
    public async Task UC005_missing_match_evidence_records_decision_without_reconstructed_proposal()
    {
        // Active rule matches one merchant; feedback on unmatched no_suggestion has no MatchEvidence.
        var category = await CreateCategoryAsync("Uc005Miss");
        var versionId = await SaveRuleVersionIdAsync(category, "uc005 matched only");
        await ActivateRulesAsync([versionId], [("uc005 matched only", "suggestion", category)], broadApply: false);
        _ = await RecordTransactionAsync("uc005 matched only");
        var unmatched = await RecordTransactionAsync("uc005 unmatched token xyz");
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, unmatched);
        Assert.Equal("no_suggestion", outcome.Kind);

        var seedProbe = new ActiveSeed(versionId, (await RequireActiveRuleSetVersionIdAsync(versionId))!);
        var before = await CaptureImmutabilityAsync(seedProbe);

        var feedback = await FeedbackAsync(
            outcome.OutcomeId,
            "corrected",
            reason: "uc005 missing evidence",
            allocationRefs: ["prior-manual", "result-manual"]);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("proposalId").ValueKind);
        var feedbackId = body.GetProperty("feedbackId").GetString()!;

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var fb = statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback");
        Assert.Equal("correct", fb.GetProperty("decisionType").GetString());
        Assert.Equal(JsonValueKind.Null, fb.GetProperty("proposalId").ValueKind);

        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC005_unknown_outcome_feedback_is_rejected_without_mutation()
    {
        var seeded = await SeedSuggestionAsync("uc005 unknown outcome");
        var before = await CaptureImmutabilityAsync(seeded);

        var feedback = await FeedbackAsync(
            "01MISSINGOUTCOME000000000000",
            "accepted",
            reason: "uc005 missing outcome");
        Assert.NotEqual(0, feedback.ExitCode);
        using var doc = ParseResult(feedback);
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.True(
            code is ClassifyErrors.OutcomeNotFound or ClassifyErrors.NotFound,
            code);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC005_archived_category_candidate_rejects_without_pointer_or_ledger_mutation()
    {
        var seeded = await SeedSuggestionAsync("uc005 arch cand");
        var before = await CaptureImmutabilityAsync(seeded);

        // Feedback-derived path re-enters normal save: archived category creates no activatable draft.
        var archived = await CreateCategoryAsync("Uc005ArchivedTarget");
        await ArchiveCategoryAsync(archived);
        var afterArchive = await CaptureImmutabilityAsync(seeded);

        var save = await SaveRuleAsync(archived, "uc005 arch draft");
        Assert.NotEqual(0, save.ExitCode);
        using var doc = ParseResult(save);
        Assert.Equal(
            ClassifyErrors.Lifecycle,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(afterArchive);
        // Archive itself is a Ledger catalogue change; pointer still the baseline seed.
        Assert.Equal(before.ActiveRuleSetVersionId, afterArchive.ActiveRuleSetVersionId);
    }

    // ── Re-enter normal validation / activation ──────────────────────────────

    [Fact]
    public async Task UC005_complete_provenance_candidate_reenters_private_validation_and_explicit_activation()
    {
        // After correction feedback, an owner-authored successor for the new category follows
        // the normal validate + grant + activate path (never auto-activated by feedback).
        var seeded = await SeedAppliedSuggestionAsync("uc005 reenter");
        var priorAlloc = seeded.ResultingAllocationId!;
        var catB = await CreateCategoryAsync("Uc005ReenterB");
        var (newAlloc, _) = await ApplyExplicitCorrectionAsync(
            seeded, priorAlloc, catB, "uc005 reenter corr");

        var feedback = await FeedbackAsync(
            seeded.OutcomeId,
            "corrected",
            reason: "uc005 reenter feedback",
            allocationRefs: [priorAlloc, newAlloc]);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);

        var pointerAfterFeedback = await RequireActiveRuleSetVersionIdAsync(seeded.RuleVersionId);
        Assert.Equal(seeded.RuleSetVersionId, pointerAfterFeedback);

        // Explicit normal lifecycle for a new owner-authored draft targeting catB.
        var successor = await SaveRuleVersionIdAsync(catB, "uc005 reenter", ruleId: "rule-uc005-reenter");
        var path = await WriteBoundCorpusAsync([("uc005 reenter", "suggestion", catB)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([successor], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
        using var actDoc = ParseResult(activated);
        var newSet = actDoc.RootElement.GetProperty("result_or_error").GetProperty("ruleSetVersionId").GetString()!;
        Assert.NotEqual(seeded.RuleSetVersionId, newSet);
        Assert.Equal(newSet, await RequireActiveRuleSetVersionIdAsync(successor));

        // Original feedback remains queryable and attributable to original outcome.
        using var fbDoc = ParseResult(feedback);
        var feedbackId = fbDoc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;
        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        Assert.Equal(
            seeded.OutcomeId,
            statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("outcomeId").GetString());
    }

    // ── History retention ────────────────────────────────────────────────────

    [Fact]
    public async Task UC005_feedback_remains_attributable_after_rule_retirement()
    {
        var keepCat = await CreateCategoryAsync("Uc005Keep");
        var dropCat = await CreateCategoryAsync("Uc005Drop");
        var keep = await SaveRuleVersionIdAsync(keepCat, "uc005 keep me", ruleId: "rule-uc005-keep");
        var drop = await SaveRuleVersionIdAsync(dropCat, "uc005 drop me", ruleId: "rule-uc005-drop");
        await ActivateRulesAsync(
            [keep, drop],
            [("uc005 keep me", "suggestion", keepCat), ("uc005 drop me", "suggestion", dropCat)],
            broadApply: false);

        var tx = await RecordTransactionAsync("uc005 drop me");
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);

        var feedback = await FeedbackAsync(outcome.OutcomeId, "accepted", reason: "uc005 pre-retire");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var fbDoc = ParseResult(feedback);
        var feedbackId = fbDoc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;

        var retired = await process.RunAsync(
            ["classify", "rule", "retire", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","ruleVersionId":{{JsonSerializer.Serialize(drop)}},"reason":"uc005 retire after feedback"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(retired, ClassifyOperationIds.RuleRetire);

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var fb = statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback");
        Assert.Equal(outcome.OutcomeId, fb.GetProperty("outcomeId").GetString());
        Assert.Equal("accept", fb.GetProperty("decisionType").GetString());
        // Original rule identity remains addressable in status history of the feedback subject.
        var rules = fb.GetProperty("ruleVersionIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.True(rules.Length >= 1);
    }

    [Fact]
    public async Task UC005_feedback_remains_attributable_after_rule_supersession()
    {
        var category = await CreateCategoryAsync("Uc005Super");
        var v1 = await SaveRuleVersionIdAsync(category, "uc005 super v1", ruleId: "rule-uc005-super");
        await ActivateRulesAsync([v1], [("uc005 super v1", "suggestion", category)], broadApply: false);

        var tx = await RecordTransactionAsync("uc005 super v1");
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);
        var feedback = await FeedbackAsync(outcome.OutcomeId, "rejected", reason: "uc005 pre-super");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var fbDoc = ParseResult(feedback);
        var feedbackId = fbDoc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;

        var v2 = await SaveRuleVersionIdAsync(category, "uc005 super v2", ruleId: "rule-uc005-super");
        await ActivateRulesAsync([v2], [("uc005 super v2", "suggestion", category)], broadApply: false);
        Assert.NotEqual(v1, v2);

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        Assert.Equal(
            outcome.OutcomeId,
            statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("outcomeId").GetString());
        Assert.Equal(
            "reject",
            statusDoc.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("decisionType").GetString());
    }

    // ── Status / guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task UC005_status_feedback_exposes_decision_proposal_state_and_rule_versions()
    {
        var seeded = await SeedSuggestionAsync("uc005 status shop");
        var feedback = await FeedbackAsync(seeded.OutcomeId, "accepted", reason: "uc005 status");
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        using var doc = ParseResult(feedback);
        var feedbackId = doc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;

        var status = await StatusAsync("feedback", feedbackId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var body = statusDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal("feedback", body.GetProperty("subjectType").GetString());
        Assert.Equal(feedbackId, body.GetProperty("subjectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("nextSafeOperationId").GetString()));
        var fb = body.GetProperty("feedback");
        Assert.Equal("accept", fb.GetProperty("decisionType").GetString());
        Assert.Contains(
            seeded.RuleVersionId,
            fb.GetProperty("ruleVersionIds").EnumerateArray().Select(e => e.GetString()!));
        // No private description material.
        Assert.DoesNotContain("uc005 status shop", status.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UC005_unknown_feedback_status_is_not_found_without_mutation()
    {
        var seeded = await SeedSuggestionAsync("uc005 status miss");
        var before = await CaptureImmutabilityAsync(seeded);
        var status = await StatusAsync("feedback", "01MISSINGFEEDBACK00000000000");
        AssertClassifyError(status, ClassifyErrors.NotFound);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC005_append_only_second_feedback_preserves_original_record()
    {
        var seeded = await SeedSuggestionAsync("uc005 append");
        var first = await FeedbackAsync(seeded.OutcomeId, "accepted", reason: "uc005 first");
        AssertClassifySuccess(first, ClassifyOperationIds.FeedbackRecord);
        using var firstDoc = ParseResult(first);
        var firstId = firstDoc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;

        var second = await FeedbackAsync(seeded.OutcomeId, "rejected", reason: "uc005 second thought");
        AssertClassifySuccess(second, ClassifyOperationIds.FeedbackRecord);
        using var secondDoc = ParseResult(second);
        var secondId = secondDoc.RootElement.GetProperty("result_or_error").GetProperty("feedbackId").GetString()!;
        Assert.NotEqual(firstId, secondId);

        var firstStatus = await StatusAsync("feedback", firstId);
        AssertClassifySuccess(firstStatus, ClassifyOperationIds.Status);
        using var fs = ParseResult(firstStatus);
        Assert.Equal(
            "accept",
            fs.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("decisionType").GetString());

        var secondStatus = await StatusAsync("feedback", secondId);
        AssertClassifySuccess(secondStatus, ClassifyOperationIds.Status);
        using var ss = ParseResult(secondStatus);
        Assert.Equal(
            "reject",
            ss.RootElement.GetProperty("result_or_error").GetProperty("feedback")
                .GetProperty("decisionType").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record ActiveSeed(string RuleVersionId, string RuleSetVersionId);

    private sealed record ImmutabilitySnapshot(
        string ProbeRuleVersionId,
        string ActiveRuleSetVersionId,
        string LedgerFingerprint);

    private sealed record SeededSuggestion(
        string EvaluationId,
        string OutcomeId,
        string TransactionId,
        string CategoryId,
        string RuleVersionId,
        string RuleSetVersionId,
        string? ResultingAllocationId = null);

    private async Task<SeededSuggestion> SeedSuggestionAsync(string description)
    {
        var category = await CreateCategoryAsync("Uc005Cat");
        var versionId = await SaveRuleVersionIdAsync(category, description);
        await ActivateRulesAsync([versionId], [(description, "suggestion", category)], broadApply: false);
        var ruleSetId = await RequireActiveRuleSetVersionIdAsync(versionId);
        var tx = await RecordTransactionAsync(description);
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);
        return new SeededSuggestion(
            evalId,
            outcome.OutcomeId,
            tx,
            category,
            versionId,
            ruleSetId);
    }

    private async Task<SeededSuggestion> SeedAppliedSuggestionAsync(string description)
    {
        var seeded = await SeedSuggestionAsync(description);
        var preview = await PreviewSelectedAsync(seeded.EvaluationId, [seeded.OutcomeId], NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var previewDoc = ParseResult(preview);
        var previewId = previewDoc.RootElement.GetProperty("result_or_error").GetProperty("previewId").GetString()!;
        var applyId = "apply-uc005-" + Guid.NewGuid().ToString("N")[..8];
        var run = await ApplyRunAsync(previewId, applyId, NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var runDoc = ParseResult(run);
        var item = runDoc.RootElement.GetProperty("result_or_error").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("transactionId").GetString() == seeded.TransactionId);
        var alloc = item.GetProperty("allocationEventId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(alloc));
        return seeded with { ResultingAllocationId = alloc };
    }

    private async Task<(string NewAllocationId, string ApplyId)> ApplyExplicitCorrectionAsync(
        SeededSuggestion seeded,
        string currentAllocationId,
        string targetCategoryId,
        string reason)
    {
        // currentCategoryId must match live allocation category.
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc005", "run-01"),
            CancellationToken.None,
            transactionIds: [seeded.TransactionId]);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var live = page.Value!.ClassificationItems!.Single();
        var currentCat = live.CurrentCategoryId!;
        Assert.Equal(currentAllocationId, live.CurrentAllocationId);

        var preview = await PreviewCorrectionsAsync(
            seeded.EvaluationId,
            seeded.TransactionId,
            seeded.OutcomeId,
            currentCat,
            targetCategoryId,
            reason,
            NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var previewDoc = ParseResult(preview);
        var previewId = previewDoc.RootElement.GetProperty("result_or_error").GetProperty("previewId").GetString()!;
        var applyId = "apply-corr-" + Guid.NewGuid().ToString("N")[..8];
        var run = await ApplyRunAsync(previewId, applyId, NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var runDoc = ParseResult(run);
        var item = runDoc.RootElement.GetProperty("result_or_error").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("transactionId").GetString() == seeded.TransactionId);
        var newAlloc = item.GetProperty("allocationEventId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(newAlloc));
        Assert.NotEqual(currentAllocationId, newAlloc);
        return (newAlloc, applyId);
    }

    private async Task ActivateRulesAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows,
        bool broadApply)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var (validationId, receiptId) = await ValidateAndGrantAsync(versionIds, path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
    }

    private async Task<(string ValidationId, string ReceiptId)> ValidateAndGrantAsync(
        IReadOnlyList<string> versionIds,
        string path)
    {
        var candidates = CandidateJson(versionIds);
        var rep = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var repBody = repDoc.RootElement.GetProperty("result_or_error");
        Assert.True(repBody.GetProperty("activationEligible").GetBoolean(), rep.Stdout);
        var validationId = repBody.GetProperty("validationId").GetString()!;

        var replay = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(replay, ClassifyOperationIds.RuleValidate);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

        var hold = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}},"representativeValidationId":{{JsonSerializer.Serialize(validationId)}},"independentReplayValidationId":{{JsonSerializer.Serialize(replayId)}},"ownerDecisionCountBefore":10,"ownerDecisionCountAfter":2,"explicitBenefitDecision":"approve-broad"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error")
            .GetProperty("ownerRulebookGateReceiptId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(receiptId), hold.Stdout);
        return (validationId, receiptId!);
    }

    private Task<ProcessResult> ActivateAsync(string validationId, string receiptId, bool broadApply) =>
        process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":{{(broadApply ? "true" : "false")}},"reason":"uc005 activate"}""",
                NextKey()),
            CancellationToken.None);

    private Task<ProcessResult> FeedbackAsync(
        string outcomeId,
        string decision,
        string reason,
        IReadOnlyList<string>? allocationRefs = null)
    {
        var refsJson = allocationRefs is null
            ? "null"
            : "[" + string.Join(",", allocationRefs.Select(r => JsonSerializer.Serialize(r))) + "]";
        var input =
            $$"""{"contractVersion":"1.0","outcomeId":{{JsonSerializer.Serialize(outcomeId)}},"decision":{{JsonSerializer.Serialize(decision)}},"ledgerAllocationRefs":{{refsJson}},"reason":{{JsonSerializer.Serialize(reason)}}}""";
        return process.RunAsync(
            ["classify", "feedback", "record", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
    }

    private async Task<string> SaveRuleVersionIdAsync(string categoryId, string description, string? ruleId = null)
    {
        var saved = await SaveRuleAsync(categoryId, description, ruleId);
        AssertClassifySuccess(saved, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(saved);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
    }

    private Task<ProcessResult> SaveRuleAsync(string categoryId, string description, string? ruleId = null)
    {
        var id = ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc005 draft"}
            """;
        return process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            created.Add((await RecordTransactionAsync(row.Description), row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc005", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<string> EvaluateSuccessAsync()
    {
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"1.0"}""", NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString()!;
    }

    private sealed record OutcomeInfo(string OutcomeId, string Kind);

    private async Task<OutcomeInfo> OutcomeGetBodyAsync(string evaluationId, string transactionId)
    {
        var result = await OutcomeGetAsync(evaluationId, transactionId);
        AssertClassifySuccess(result, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        return new OutcomeInfo(
            body.GetProperty("outcomeId").GetString()!,
            body.GetProperty("kind").GetString()!);
    }

    private Task<ProcessResult> OutcomeGetAsync(string evaluationId, string transactionId) =>
        process.RunAsync(
            ["classify", "outcome", "get", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","evaluationId":{{JsonSerializer.Serialize(evaluationId)}},"transactionId":{{JsonSerializer.Serialize(transactionId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    private Task<ProcessResult> PreviewSelectedAsync(string evaluationId, IReadOnlyList<string> outcomeIds, string key)
    {
        var ids = "[" + string.Join(",", outcomeIds.Select(id => JsonSerializer.Serialize(id))) + "]";
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"selection\":{\"mode\":\"selected_outcomes\",\"outcomeIds\":" + ids + "}}";
        return process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private Task<ProcessResult> PreviewCorrectionsAsync(
        string evaluationId,
        string transactionId,
        string outcomeId,
        string currentCategoryId,
        string targetCategoryId,
        string reason,
        string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"selection\":{\"mode\":\"explicit_corrections\",\"correctionItems\":[{"
            + "\"transactionId\":" + JsonSerializer.Serialize(transactionId)
            + ",\"outcomeId\":" + JsonSerializer.Serialize(outcomeId)
            + ",\"currentCategoryId\":" + JsonSerializer.Serialize(currentCategoryId)
            + ",\"targetCategoryId\":" + JsonSerializer.Serialize(targetCategoryId)
            + ",\"reason\":" + JsonSerializer.Serialize(reason)
            + "}]}}";
        return process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private Task<ProcessResult> ApplyRunAsync(string previewId, string applyId, string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"previewId\":" + JsonSerializer.Serialize(previewId)
            + ",\"applyId\":" + JsonSerializer.Serialize(applyId) + "}";
        return process.RunAsync(
            ["classify", "apply", "run", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private async Task<ImmutabilitySnapshot> CaptureImmutabilityAsync(SeededSuggestion seeded) =>
        await CaptureImmutabilityAsync(new ActiveSeed(seeded.RuleVersionId, seeded.RuleSetVersionId));

    private async Task<ImmutabilitySnapshot> CaptureImmutabilityAsync(ActiveSeed baseline)
    {
        var pointer = await RequireActiveRuleSetVersionIdAsync(baseline.RuleVersionId);
        Assert.Equal(baseline.RuleSetVersionId, pointer);
        var ledger = await LedgerFingerprintAsync();
        Assert.False(string.IsNullOrWhiteSpace(ledger));
        return new ImmutabilitySnapshot(baseline.RuleVersionId, pointer, ledger);
    }

    private async Task AssertUnchangedAsync(ImmutabilitySnapshot before)
    {
        Assert.Equal(
            before.ActiveRuleSetVersionId,
            await RequireActiveRuleSetVersionIdAsync(before.ProbeRuleVersionId));
        Assert.Equal(before.LedgerFingerprint, await LedgerFingerprintAsync());
    }

    private async Task<string> RequireActiveRuleSetVersionIdAsync(string probeRuleVersionId)
    {
        Assert.False(string.IsNullOrWhiteSpace(probeRuleVersionId));
        var status = await StatusAsync("rule", probeRuleVersionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var doc = ParseResult(status);
        var active = doc.RootElement.GetProperty("result_or_error")
            .GetProperty("rule")
            .GetProperty("activeRuleSetVersionId");
        Assert.NotEqual(JsonValueKind.Null, active.ValueKind);
        var pointer = active.GetString();
        Assert.False(string.IsNullOrWhiteSpace(pointer));
        return pointer!;
    }

    private Task<ProcessResult> StatusAsync(string subjectType, string subjectId) =>
        process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":{{JsonSerializer.Serialize(subjectType)}},"subjectId":{{JsonSerializer.Serialize(subjectId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    private async Task<string?> CurrentAllocationAsync(string transactionId)
    {
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc005", "run-01"),
            CancellationToken.None,
            transactionIds: [transactionId]);
        Assert.True(page.IsSuccess, page.Error?.Code);
        return page.Value!.ClassificationItems!.Single().CurrentAllocationId;
    }

    private async Task<string> LedgerFingerprintAsync()
    {
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc005", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var items = page.Value!.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
        var material = string.Join('|', items
            .OrderBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(i => string.Concat(
                i.TransactionId, ':',
                i.CurrentCategoryId ?? "", ':',
                i.CurrentAllocationId ?? "", ':',
                i.AllocationRevision)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private async Task<string> CreateAccountAsync()
    {
        ProcessResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var unique = Guid.NewGuid().ToString("N");
            result = await process.RunAsync(
                ["ledger", "account", "create", "--input", "-"],
                LedgerEnvelope(
                    $$"""{"institutionName":"Uc005 Bank {{unique[..12]}}","displayName":"Primary-{{unique[..12]}}","accountType":"cheque","maskedIdentifier":"****{{(Math.Abs(unique.GetHashCode()) % 9000 + 1000)}}","currencyCode":"ZAR"}""",
                    NextKey()),
                CancellationToken.None);
            if (result.ExitCode == 0)
            {
                using var doc = JsonDocument.Parse(result.Stdout);
                return doc.RootElement.GetProperty("result").GetProperty("accountId").GetString()!;
            }
        }

        Assert.Fail(result!.Stdout + "\n" + result.Stderr);
        return "";
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var full = name + "-" + Guid.NewGuid().ToString("N")[..6];
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            LedgerEnvelope($$"""{"name":{{JsonSerializer.Serialize(full)}}}""", NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task ArchiveCategoryAsync(string categoryId)
    {
        var result = await process.RunAsync(
            ["ledger", "category", "archive", "--input", "-"],
            LedgerEnvelope(
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":"uc005-archive"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task<string> RecordTransactionAsync(string description, string amount = "-12.34")
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(accountId)}},
              "signedAmount":{{JsonSerializer.Serialize(amount)}},
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(description)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc005:" + Guid.NewGuid().ToString("N")[..8])}}
              }
            }
            """;
        var result = await process.RunAsync(
            ["ledger", "transaction", "record", "--input", "-"],
            LedgerEnvelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("transactionId").GetString()!;
    }

    private static string CandidateJson(IReadOnlyList<string> versionIds) =>
        "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";

    private static void AssertClassifySuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + "\n" + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
    }

    private static void AssertClassifyError(ProcessResult result, string errorCode)
    {
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            errorCode,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
    }

    private static JsonDocument ParseResult(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout);

    private static string ClassifyEnvelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc005","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc005","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc005","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc005-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClassifyUc005Collection
{
    public const string Name = "ClassifyUc005";
}
