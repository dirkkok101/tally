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
/// UC-CLASSIFY-004 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-004 / bd-2rf7
/// VerifiedClassifyUc004 — published-boundary acceptance for immutable owner-authored
/// rule lifecycle with fail-closed private evidence and no transaction assignment.
///
/// All CLASSIFY operations under test go through TallyProcess
/// (rule.save / rule.validate / rule.activate / rule.retire / status / abandon / evaluate /
/// feedback.record). Failure paths prove no active-pointer or Ledger mutation via public
/// status and Ledger projections — never private fixtures or payload content.
/// </summary>
[Collection(ClassifyUc004Collection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc004RulesTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc004-{Guid.NewGuid():N}");
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

    // ── Draft save ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UC004_save_supported_active_category_creates_immutable_owner_authored_draft()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004Draft");
        var before = await CaptureImmutabilityAsync(baseline);

        var saved = await SaveRuleAsync(category, "uc004 draft shop", ruleId: "rule-uc004-draft");
        AssertClassifySuccess(saved, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(saved);
        var body = doc.RootElement.GetProperty("result_or_error");
        var versionId = body.GetProperty("ruleVersionId").GetString()!;
        Assert.Equal(category, body.GetProperty("categoryId").GetString());
        Assert.Equal(NormalizationDescriptor.V1.Version, body.GetProperty("normalizationVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(versionId));

        // Draft is inspectable; no activation of a new pointer.
        var status = await StatusAsync("rule", versionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var statusBody = statusDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal("draft", statusBody.GetProperty("lifecycleState").GetString());
        // Public status reports the live active pointer (baseline), not null-probe tautology.
        Assert.Equal(
            before.ActiveRuleSetVersionId,
            statusBody.GetProperty("rule").GetProperty("activeRuleSetVersionId").GetString());
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_save_unsupported_predicate_creates_no_activatable_version()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004BadPred");
        var before = await CaptureImmutabilityAsync(baseline);

        // amount.direction does not allow starts_with.
        var input = $$"""
            {"contractVersion":"1.0","ruleId":"rule-bad-pred","categoryId":{{JsonSerializer.Serialize(category)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"amount.direction","predicateKind":"starts_with","valueText":"out"}],"reason":"uc004 unsupported predicate"}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = ParseResult(result);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.False(doc.RootElement.GetProperty("result_or_error").TryGetProperty("ruleVersionId", out _));
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_save_missing_category_creates_no_activatable_version()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        var missing = "01MISSINGCATEGORYID000000000";
        var result = await SaveRuleAsync(missing, "uc004 missing cat");
        AssertClassifyError(result, ClassifyErrors.NotFound);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_save_archived_category_creates_no_activatable_version()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004ArchSave");
        await ArchiveCategoryAsync(category);
        var before = await CaptureImmutabilityAsync(baseline);

        var result = await SaveRuleAsync(category, "uc004 archived cat");
        AssertClassifyError(result, ClassifyErrors.Lifecycle);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_save_does_not_activate_and_does_not_mutate_ledger()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004NoAct");
        var before = await CaptureImmutabilityAsync(baseline);
        var saved = await SaveRuleAsync(category, "uc004 no auto");
        AssertClassifySuccess(saved, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(saved);
        var versionId = doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;

        Assert.Equal(before.ActiveRuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
        await AssertUnchangedAsync(before);
    }

    // ── Validation / private evidence ────────────────────────────────────────

    [Fact]
    public async Task UC004_validate_current_private_evidence_is_activation_eligible_with_fingerprints()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004ValOk");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 val ok");
        var path = await WriteBoundCorpusAsync([("uc004 val ok", "suggestion", category)]);
        var before = await CaptureImmutabilityAsync(baseline);

        var result = await ValidateAsync([versionId], path);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleValidate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("activationEligible").GetBoolean());
        Assert.Equal(1, body.GetProperty("totalRows").GetInt32());
        Assert.Equal(1, body.GetProperty("accountedRows").GetInt32());
        Assert.Equal(1, body.GetProperty("suggestionCount").GetInt32());
        Assert.Equal(0, body.GetProperty("incorrectApplicationCanaries").GetInt32());
        Assert.Equal(64, body.GetProperty("corpusFingerprint").GetString()!.Length);
        Assert.Equal(64, body.GetProperty("candidateFingerprint").GetString()!.Length);
        Assert.Equal(64, body.GetProperty("reportFingerprint").GetString()!.Length);
        Assert.Equal(64, body.GetProperty("outcomesCanonicalHash").GetString()!.Length);
        // Aggregate-only: no private description/path material in the public result.
        var stdout = result.Stdout;
        Assert.DoesNotContain("uc004 val ok", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(path, stdout, StringComparison.Ordinal);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_validate_absent_corpus_rejects_without_active_pointer_change()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004NoCorp");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 no corp");
        var before = await CaptureImmutabilityAsync(baseline);

        var missing = Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var result = await ValidateAsync([versionId], missing);
        Assert.NotEqual(0, result.ExitCode);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_incorrect_canary_blocks_activation_and_preserves_prior_active_set()
    {
        var baseline = await SeedActiveRuleSetAsync();

        var category = await CreateCategoryAsync("Uc004Canary");
        var other = await CreateCategoryAsync("Uc004CanaryOther");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 canary shop");
        // Engine will suggest `category`, expected label is `other` → incorrect-application canary.
        var path = await WriteBoundCorpusAsync([("uc004 canary shop", "suggestion", other)]);

        var rep = await ValidateAsync([versionId], path);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var repBody = repDoc.RootElement.GetProperty("result_or_error");
        Assert.False(repBody.GetProperty("activationEligible").GetBoolean());
        Assert.True(repBody.GetProperty("incorrectApplicationCanaries").GetInt32() >= 1);
        var validationId = repBody.GetProperty("validationId").GetString()!;

        // Gate finalization / activate must not replace the prior active set.
        var replay = await ValidateAsync([versionId], path);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;
        var hold = await ValidateHoldAsync([versionId], path, validationId, replayId);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error").GetProperty("ownerRulebookGateReceiptId").GetString();

        // Capture immediately before the expected-failure activation attempt.
        var before = await CaptureImmutabilityAsync(baseline);
        Assert.False(string.IsNullOrWhiteSpace(receiptId), "hold should return a receipt id (granted or blocked)");
        var activated = await ActivateAsync(validationId, receiptId!, broadApply: false);
        Assert.NotEqual(0, activated.ExitCode);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_unexplained_conflict_blocks_activation_and_preserves_prior_active_set()
    {
        var baseline = await SeedActiveRuleSetAsync();

        var catA = await CreateCategoryAsync("Uc004ConfA");
        var catB = await CreateCategoryAsync("Uc004ConfB");
        var vA = await SaveRuleVersionIdAsync(catA, "uc004 clash", ruleId: "rule-conf-a");
        var vB = await SaveRuleVersionIdAsync(catB, "uc004 clash", ruleId: "rule-conf-b");
        // Expected suggestion but engine conflicts across two categories.
        var path = await WriteBoundCorpusAsync([("uc004 clash", "suggestion", catA)]);

        var result = await ValidateAsync([vA, vB], path);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleValidate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(1, body.GetProperty("conflictCount").GetInt32());
        Assert.False(body.GetProperty("activationEligible").GetBoolean());
        Assert.True(
            body.GetProperty("unexplainedConflictCount").GetInt32() >= 1
            || body.GetProperty("incorrectApplicationCanaries").GetInt32() >= 1);
        var validationId = body.GetProperty("validationId").GetString()!;

        var replay = await ValidateAsync([vA, vB], path);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;
        var hold = await ValidateHoldAsync([vA, vB], path, validationId, replayId);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error").GetProperty("ownerRulebookGateReceiptId").GetString();

        var before = await CaptureImmutabilityAsync(baseline);
        Assert.False(string.IsNullOrWhiteSpace(receiptId), "hold should return a receipt id");
        var activated = await ActivateAsync(validationId, receiptId!, broadApply: false);
        Assert.NotEqual(0, activated.ExitCode);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_stale_validation_rejects_activation_without_pointer_change()
    {
        var baseline = await SeedActiveRuleSetAsync();

        var category = await CreateCategoryAsync("Uc004StaleAct");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 stale act");
        var path = await WriteBoundCorpusAsync([("uc004 stale act", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);

        // Archive category after grant → live currency fails at activate.
        await ArchiveCategoryAsync(category);
        var before = await CaptureImmutabilityAsync(baseline);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        Assert.NotEqual(0, activated.ExitCode);
        using var doc = ParseResult(activated);
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.True(
            code is ClassifyErrors.Stale or ClassifyErrors.Lifecycle or ClassifyErrors.NotFound,
            code);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_activate_without_receipt_is_rejected()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004NoRcpt");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 no receipt");
        var path = await WriteBoundCorpusAsync([("uc004 no receipt", "suggestion", category)]);
        var rep = await ValidateAsync([versionId], path);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var validationId = repDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

        var before = await CaptureImmutabilityAsync(baseline);
        var activated = await ActivateAsync(validationId, "missing-receipt", broadApply: false);
        AssertClassifyError(activated, ClassifyErrors.NotFound);
        await AssertUnchangedAsync(before);
        // Probe version never became the active set.
        Assert.Equal(before.ActiveRuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
    }

    // ── Activation / retirement / replacement ────────────────────────────────

    [Fact]
    public async Task UC004_explicit_activation_creates_active_rule_set_without_ledger_mutation()
    {
        var category = await CreateCategoryAsync("Uc004Act");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 activate shop");
        var path = await WriteBoundCorpusAsync([("uc004 activate shop", "suggestion", category)]);
        var ledgerBefore = await LedgerFingerprintAsync();

        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
        using var doc = ParseResult(activated);
        var body = doc.RootElement.GetProperty("result_or_error");
        var ruleSetId = body.GetProperty("ruleSetVersionId").GetString()!;
        Assert.Equal(validationId, body.GetProperty("validationId").GetString());
        Assert.False(body.GetProperty("broadApplyAllowed").GetBoolean());

        Assert.Equal(ruleSetId, await RequireActiveRuleSetVersionIdAsync(versionId));
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC004_retire_active_member_creates_successor_and_preserves_prior()
    {
        var catKeep = await CreateCategoryAsync("Uc004Keep");
        var catDrop = await CreateCategoryAsync("Uc004Drop");
        var keep = await SaveRuleVersionIdAsync(catKeep, "uc004 keep me", ruleId: "rule-keep");
        var drop = await SaveRuleVersionIdAsync(catDrop, "uc004 drop me", ruleId: "rule-drop");
        var path = await WriteBoundCorpusAsync([
            ("uc004 keep me", "suggestion", catKeep),
            ("uc004 drop me", "suggestion", catDrop)
        ]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([keep, drop], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
        using var actDoc = ParseResult(activated);
        var priorSet = actDoc.RootElement.GetProperty("result_or_error").GetProperty("ruleSetVersionId").GetString()!;
        var ledgerBefore = await LedgerFingerprintAsync();

        var retired = await process.RunAsync(
            ["classify", "rule", "retire", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","ruleVersionId":{{JsonSerializer.Serialize(drop)}},"reason":"uc004 retire drop"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(retired, ClassifyOperationIds.RuleRetire);
        using var retDoc = ParseResult(retired);
        var retBody = retDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(drop, retBody.GetProperty("retiredRuleVersionId").GetString());
        var successor = retBody.GetProperty("successorRuleSetVersionId").GetString()!;
        Assert.NotEqual(priorSet, successor);

        // Public status: successor is active; retired version remains inspectable (immutable row).
        Assert.Equal(successor, await RequireActiveRuleSetVersionIdAsync(keep));
        var dropStatus = await StatusAsync("rule", drop);
        AssertClassifySuccess(dropStatus, ClassifyOperationIds.Status);
        using var dropDoc = ParseResult(dropStatus);
        Assert.Equal(drop, dropDoc.RootElement.GetProperty("result_or_error").GetProperty("subjectId").GetString());
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC004_validated_replacement_supersedes_prior_with_immutable_provenance()
    {
        var category = await CreateCategoryAsync("Uc004Replace");
        var v1 = await SaveRuleVersionIdAsync(category, "uc004 replace v1", ruleId: "rule-replace");
        var path1 = await WriteBoundCorpusAsync([("uc004 replace v1", "suggestion", category)]);
        var grant1 = await ValidateAndGrantAsync([v1], path1);
        var first = await ActivateAsync(grant1.ValidationId, grant1.ReceiptId, broadApply: false);
        AssertClassifySuccess(first, ClassifyOperationIds.RuleActivate);
        using var firstDoc = ParseResult(first);
        var priorSet = firstDoc.RootElement.GetProperty("result_or_error").GetProperty("ruleSetVersionId").GetString()!;

        var v2 = await SaveRuleVersionIdAsync(category, "uc004 replace v2", ruleId: "rule-replace");
        Assert.NotEqual(v1, v2);
        var path2 = await WriteBoundCorpusAsync([("uc004 replace v2", "suggestion", category)]);
        var grant2 = await ValidateAndGrantAsync([v2], path2);
        var second = await ActivateAsync(grant2.ValidationId, grant2.ReceiptId, broadApply: false);
        AssertClassifySuccess(second, ClassifyOperationIds.RuleActivate);
        using var secondDoc = ParseResult(second);
        var nextSet = secondDoc.RootElement.GetProperty("result_or_error").GetProperty("ruleSetVersionId").GetString()!;
        Assert.NotEqual(priorSet, nextSet);
        Assert.Equal(nextSet, await RequireActiveRuleSetVersionIdAsync(v2));

        // Prior version remains addressable via status (immutable history).
        var priorStatus = await StatusAsync("rule", v1);
        AssertClassifySuccess(priorStatus, ClassifyOperationIds.Status);
        using var priorDoc = ParseResult(priorStatus);
        Assert.Equal(v1, priorDoc.RootElement.GetProperty("result_or_error").GetProperty("subjectId").GetString());
        var versions = priorDoc.RootElement.GetProperty("result_or_error").GetProperty("rule").GetProperty("versions");
        Assert.True(versions.GetArrayLength() >= 1);
    }

    // ── Rename / lifecycle invalidation ──────────────────────────────────────

    [Fact]
    public async Task UC004_same_id_rename_preserves_rule_category_identity_and_current_display()
    {
        var category = await CreateCategoryAsync("Uc004Rename");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 rename shop");
        var path = await WriteBoundCorpusAsync([("uc004 rename shop", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);

        const string newName = "Uc004 Renamed Display";
        await RenameCategoryAsync(category, newName);

        // Category identity unchanged; public catalogue shows current display name.
        var listed = await ledger.ListClassificationCategoriesAsync(
            "1.0",
            new SafeActor("automation", "classify-uc004", "run-01"),
            CancellationToken.None,
            status: null);
        Assert.True(listed.IsSuccess, listed.Error?.Code);
        var item = Assert.Single(listed.Value!.Items, i => i.CategoryId == category);
        Assert.Equal(newName, item.Name);

        // Active pointer and rule version still addressable.
        Assert.False(string.IsNullOrWhiteSpace(await RequireActiveRuleSetVersionIdAsync(versionId)));
        var status = await StatusAsync("rule", versionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
    }

    [Fact]
    public async Task UC004_category_archive_invalidates_activation_authority()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004LifeArch");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 life arch");
        var path = await WriteBoundCorpusAsync([("uc004 life arch", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);

        await ArchiveCategoryAsync(category);
        var before = await CaptureImmutabilityAsync(baseline);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        Assert.NotEqual(0, activated.ExitCode);
        await AssertUnchangedAsync(before);
        Assert.Equal(before.ActiveRuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
    }

    // ── No automatic activation ──────────────────────────────────────────────

    [Fact]
    public async Task UC004_validation_alone_does_not_activate()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004ValOnly");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 val only");
        var path = await WriteBoundCorpusAsync([("uc004 val only", "suggestion", category)]);
        var before = await CaptureImmutabilityAsync(baseline);
        var result = await ValidateAsync([versionId], path);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleValidate);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_feedback_does_not_activate_or_change_active_pointer()
    {
        var category = await CreateCategoryAsync("Uc004Fb");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 feedback shop");
        var path = await WriteBoundCorpusAsync([("uc004 feedback shop", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
        var seed = new ActiveSeed(versionId, (await RequireActiveRuleSetVersionIdAsync(versionId))!);

        var tx = await RecordTransactionAsync("uc004 feedback shop");
        var eval = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"1.0"}""", NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(eval, ClassifyOperationIds.Evaluate);
        using var evalDoc = ParseResult(eval);
        var evalId = evalDoc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString()!;

        var outcome = await process.RunAsync(
            ["classify", "outcome", "get", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","evaluationId":{{JsonSerializer.Serialize(evalId)}},"transactionId":{{JsonSerializer.Serialize(tx)}}}""",
                idempotencyKey: null),
            CancellationToken.None);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var outDoc = ParseResult(outcome);
        var outcomeId = outDoc.RootElement.GetProperty("result_or_error").GetProperty("outcomeId").GetString()!;

        // Capture after ledger tx create (setup), immediately before feedback (the op under test).
        var before = await CaptureImmutabilityAsync(seed);
        var feedback = await process.RunAsync(
            ["classify", "feedback", "record", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","outcomeId":{{JsonSerializer.Serialize(outcomeId)}},"decision":"accepted","reason":"uc004 feedback"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(feedback, ClassifyOperationIds.FeedbackRecord);
        await AssertUnchangedAsync(before);
    }

    // ── Status / abandon / retention ─────────────────────────────────────────

    [Fact]
    public async Task UC004_status_on_draft_and_validation_is_bounded_without_private_payload()
    {
        var category = await CreateCategoryAsync("Uc004Status");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 status shop");
        var path = await WriteBoundCorpusAsync([("uc004 status shop", "suggestion", category)]);
        var rep = await ValidateAsync([versionId], path);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var validationId = repDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

        var ruleStatus = await StatusAsync("rule", versionId);
        AssertClassifySuccess(ruleStatus, ClassifyOperationIds.Status);
        using var ruleDoc = ParseResult(ruleStatus);
        var ruleBody = ruleDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal("rule", ruleBody.GetProperty("subjectType").GetString());
        Assert.Equal(versionId, ruleBody.GetProperty("subjectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ruleBody.GetProperty("nextSafeOperationId").GetString()));
        Assert.DoesNotContain("uc004 status shop", ruleStatus.Stdout, StringComparison.Ordinal);

        var valStatus = await StatusAsync("validation", validationId);
        AssertClassifySuccess(valStatus, ClassifyOperationIds.Status);
        using var valDoc = ParseResult(valStatus);
        var valBody = valDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal("validation", valBody.GetProperty("subjectType").GetString());
        Assert.Equal(64, valBody.GetProperty("validation").GetProperty("corpusFingerprint").GetString()!.Length);
        Assert.DoesNotContain(path, valStatus.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("uc004 status shop", valStatus.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UC004_status_unknown_subject_is_not_found()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        var result = await StatusAsync("rule", "01MISSINGRULEVERSION00000000");
        AssertClassifyError(result, ClassifyErrors.NotFound);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_abandon_unreferenced_draft_preserves_active_pointer_and_ledger()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc004Abandon");
        var versionId = await SaveRuleVersionIdAsync(category, "uc004 abandon draft");
        var beforeAbandon = await CaptureImmutabilityAsync(baseline);

        var abandoned = await process.RunAsync(
            ["classify", "abandon", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":"rule","subjectId":{{JsonSerializer.Serialize(versionId)}},"reason":"uc004 abandon unreferenced draft"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(abandoned, ClassifyOperationIds.Abandon);
        using var doc = ParseResult(abandoned);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("abandoned").GetBoolean());
        Assert.Equal(versionId, body.GetProperty("subjectId").GetString());
        await AssertUnchangedAsync(beforeAbandon);

        // Abandoned draft is non-activatable (tombstone blocks activate).
        var status = await StatusAsync("rule", versionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        Assert.Equal(
            "abandoned",
            statusDoc.RootElement.GetProperty("result_or_error").GetProperty("lifecycleState").GetString());

        var path = await WriteBoundCorpusAsync([("uc004 abandon draft", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);
        var beforeActivate = await CaptureImmutabilityAsync(baseline);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        Assert.NotEqual(0, activated.ExitCode);
        await AssertUnchangedAsync(beforeActivate);
    }

    [Fact]
    public async Task UC004_abandon_referenced_active_rule_is_rejected()
    {
        var seeded = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(seeded);

        var abandoned = await process.RunAsync(
            ["classify", "abandon", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":"rule","subjectId":{{JsonSerializer.Serialize(seeded.RuleVersionId)}},"reason":"uc004 reject active abandon"}""",
                NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, abandoned.ExitCode);
        using var doc = ParseResult(abandoned);
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.True(code is ClassifyErrors.Lifecycle or ClassifyErrors.Conflict, code);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC004_validate_report_contains_no_private_fixture_content()
    {
        var category = await CreateCategoryAsync("Uc004Priv");
        var secretPhrase = "uc004 private merchant " + Guid.NewGuid().ToString("N")[..8];
        var versionId = await SaveRuleVersionIdAsync(category, secretPhrase);
        var path = await WriteBoundCorpusAsync([(secretPhrase, "suggestion", category)]);
        var result = await ValidateAsync([versionId], path);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleValidate);
        Assert.DoesNotContain(secretPhrase, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(path, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record ActiveSeed(string RuleVersionId, string RuleSetVersionId);

    /// <summary>
    /// Real published active pointer + public Ledger projection fingerprint.
    /// Never uses a null probe: ActiveRuleSetVersionId and ProbeRuleVersionId are required.
    /// </summary>
    private sealed record ImmutabilitySnapshot(
        string ProbeRuleVersionId,
        string ActiveRuleSetVersionId,
        string LedgerFingerprint);

    private async Task<ActiveSeed> SeedActiveRuleSetAsync()
    {
        var category = await CreateCategoryAsync("Uc004Base");
        var versionId = await SaveRuleVersionIdAsync(
            category,
            "uc004 baseline shop",
            ruleId: "rule-uc004-base-" + Guid.NewGuid().ToString("N")[..8]);
        var path = await WriteBoundCorpusAsync([("uc004 baseline shop", "suggestion", category)]);
        var (validationId, receiptId) = await ValidateAndGrantAsync([versionId], path);
        var activated = await ActivateAsync(validationId, receiptId, broadApply: false);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
        using var doc = ParseResult(activated);
        var ruleSetId = doc.RootElement.GetProperty("result_or_error").GetProperty("ruleSetVersionId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(ruleSetId));
        // Round-trip through published status so the probe is real public-boundary evidence.
        Assert.Equal(ruleSetId, await RequireActiveRuleSetVersionIdAsync(versionId));
        return new ActiveSeed(versionId, ruleSetId);
    }

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
        Assert.False(string.IsNullOrWhiteSpace(before.ProbeRuleVersionId));
        Assert.False(string.IsNullOrWhiteSpace(before.ActiveRuleSetVersionId));
        var afterPointer = await RequireActiveRuleSetVersionIdAsync(before.ProbeRuleVersionId);
        Assert.Equal(before.ActiveRuleSetVersionId, afterPointer);
        Assert.Equal(before.LedgerFingerprint, await LedgerFingerprintAsync());
    }

    private async Task<(string ValidationId, string ReceiptId)> ValidateAndGrantAsync(
        IReadOnlyList<string> versionIds,
        string path)
    {
        var rep = await ValidateAsync(versionIds, path);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var repBody = repDoc.RootElement.GetProperty("result_or_error");
        Assert.True(repBody.GetProperty("activationEligible").GetBoolean(), "rep not eligible: " + rep.Stdout);
        var validationId = repBody.GetProperty("validationId").GetString()!;

        var replay = await ValidateAsync(versionIds, path);
        AssertClassifySuccess(replay, ClassifyOperationIds.RuleValidate);
        using var replayDoc = ParseResult(replay);
        var replayBody = replayDoc.RootElement.GetProperty("result_or_error");
        Assert.True(replayBody.GetProperty("activationEligible").GetBoolean(), "replay not eligible: " + replay.Stdout);
        Assert.Equal(
            repBody.GetProperty("reportFingerprint").GetString(),
            replayBody.GetProperty("reportFingerprint").GetString());
        var replayId = replayBody.GetProperty("validationId").GetString()!;
        Assert.NotEqual(validationId, replayId);

        var hold = await ValidateHoldAsync(versionIds, path, validationId, replayId);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error")
            .GetProperty("ownerRulebookGateReceiptId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(receiptId), "missing receipt: " + hold.Stdout);
        return (validationId, receiptId!);
    }

    private Task<ProcessResult> ValidateAsync(IReadOnlyList<string> versionIds, string path)
    {
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";
        return process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
    }

    private Task<ProcessResult> ValidateHoldAsync(
        IReadOnlyList<string> versionIds,
        string path,
        string repId,
        string replayId)
    {
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";
        return process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}},"representativeValidationId":{{JsonSerializer.Serialize(repId)}},"independentReplayValidationId":{{JsonSerializer.Serialize(replayId)}},"ownerDecisionCountBefore":10,"ownerDecisionCountAfter":2,"explicitBenefitDecision":"approve-broad"}""",
                NextKey()),
            CancellationToken.None);
    }

    private Task<ProcessResult> ActivateAsync(string validationId, string receiptId, bool broadApply) =>
        process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":{{(broadApply ? "true" : "false")}},"reason":"uc004 activate"}""",
                NextKey()),
            CancellationToken.None);

    private async Task<string> SaveRuleVersionIdAsync(string categoryId, string description, string? ruleId = null)
    {
        var saved = await SaveRuleAsync(categoryId, description, ruleId);
        AssertClassifySuccess(saved, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(saved);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
    }

    private async Task<ProcessResult> SaveRuleAsync(string categoryId, string description, string? ruleId = null)
    {
        var id = ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc004 draft"}
            """;
        return await process.RunAsync(
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
            var txId = await RecordTransactionAsync(row.Description);
            created.Add((txId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc004", "run-01"),
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

    /// <summary>
    /// Published classify.status activeRuleSetVersionId for a real rule-version subject.
    /// Never accepts a null probe — null/empty subject is not evidence.
    /// </summary>
    private async Task<string> RequireActiveRuleSetVersionIdAsync(string probeRuleVersionId)
    {
        Assert.False(string.IsNullOrWhiteSpace(probeRuleVersionId), "probe rule version id required");
        var status = await StatusAsync("rule", probeRuleVersionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var doc = ParseResult(status);
        var active = doc.RootElement.GetProperty("result_or_error")
            .GetProperty("rule")
            .GetProperty("activeRuleSetVersionId");
        Assert.NotEqual(JsonValueKind.Null, active.ValueKind);
        var pointer = active.GetString();
        Assert.False(string.IsNullOrWhiteSpace(pointer), "expected non-null active rule set pointer");
        return pointer!;
    }

    private Task<ProcessResult> StatusAsync(string subjectType, string subjectId) =>
        process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":{{JsonSerializer.Serialize(subjectType)}},"subjectId":{{JsonSerializer.Serialize(subjectId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    private async Task<string> LedgerFingerprintAsync()
    {
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc004", "run-01"),
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
        // Fresh process/envelope bootstrap can occasionally fail schema preflight under
        // sequential fixture churn; retry with a new identity rather than weakening asserts.
        ProcessResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var unique = Guid.NewGuid().ToString("N");
            result = await process.RunAsync(
                ["ledger", "account", "create", "--input", "-"],
                LedgerEnvelope(
                    $$"""{"institutionName":"Uc004 Bank {{unique[..12]}}","displayName":"Primary-{{unique[..12]}}","accountType":"cheque","maskedIdentifier":"****{{(Math.Abs(unique.GetHashCode()) % 9000 + 1000)}}","currencyCode":"ZAR"}""",
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
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":"uc004-archive"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task RenameCategoryAsync(string categoryId, string newName)
    {
        var result = await process.RunAsync(
            ["ledger", "category", "rename", "--input", "-"],
            LedgerEnvelope(
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"newName":{{JsonSerializer.Serialize(newName)}},"reason":"uc004-rename"}""",
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
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc004:" + Guid.NewGuid().ToString("N")[..8])}}
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

    private static void AssertClassifySuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + "\n" + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("result_or_error", out _));
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
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc004","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc004","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc004","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc004-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>Serializes UC-004 acceptance fixtures so host ledger bootstrap is not contended.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClassifyUc004Collection
{
    public const string Name = "ClassifyUc004";
}
