using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE / FR-CLASSIFY-RULE-LIFECYCLE / bd-3e6o
/// Activation evidence, atomicity, broad authority, rename, archive, stale/missing evidence.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RuleActivationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-rule-activate-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-activate", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private ClassificationValidationStore validationStore = null!;
    private RuleSetStore ruleSetStore = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
    private ActivateClassificationRuleCommand activate = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);
        var services = await ClassifyRuleExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        store = services.State.Store;
        ruleStore = services.RuleStore;
        validationStore = services.ValidationStore;
        ruleSetStore = services.RuleSetStore;
        save = services.Save;
        activate = services.Activate;
        validate = new ValidateClassificationRuleCommand(
            store, ruleStore, validationStore, ClassifyCorpusExtensions.CreateReader(), ledger, services.State.Idempotency);
        accountId = (await CreateAccountAsync()).AccountId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Evidence / success ───────────────────────────────────────────────────

    [Fact]
    public async Task Activate_with_eligible_validation_creates_immutable_rule_set_and_pointer()
    {
        var category = await CreateCategoryAsync("Groceries");
        var versionId = await SaveDraftAsync(category.CategoryId, "whole foods");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "whole foods");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;

        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "owner activate"),
            actor, NextKey(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(validationId, result.Value!.ValidationId);
        Assert.False(result.Value.BroadApplyAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.RuleSetVersionId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.NotNull(pointer);
        Assert.Equal(result.Value.RuleSetVersionId, pointer!.RuleSetVersionId);
        var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, pointer.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([versionId], members);
        var set = await ruleSetStore.GetRuleSetVersionAsync(
            connection, null, pointer.RuleSetVersionId, CancellationToken.None);
        Assert.Equal(validationId, set!.ValidationRunId);
        Assert.Equal(receiptId, set.OwnerRulebookGateReceiptId);
        Assert.False(string.IsNullOrWhiteSpace(set.OwnerRulebookGateReceiptFingerprint));
        Assert.Null(set.PriorRuleSetVersionId);
        Assert.Equal(1L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
        Assert.True(await ruleSetStore.CountLifecycleEventsAsync(connection, null, CancellationToken.None) >= 1);

        // Rule version row remains immutable draft (authority is membership + events).
        var version = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        Assert.Equal(ClassificationRuleStore.LifecycleDraft, version!.LifecycleState);
        Assert.Equal(0, version.BroadApplyAllowed);
    }

    [Fact]
    public async Task Activate_requires_exact_completed_validation_evidence()
    {
        var missing = await activate.HandleAsync(
            ActivateRequest("missing-validation", "missing-receipt", false, "no evidence"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ValidationNotFound, missing.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_incorrect_application_canaries()
    {
        var category = await CreateCategoryAsync("Wrong");
        var other = await CreateCategoryAsync("Other");
        var versionId = await SaveDraftAsync(category.CategoryId, "shop");
        // Engine suggests `category`, expected is `other` → incorrect canary.
        var path = await WriteBoundCorpusAsync([("shop", other.CategoryId, "suggestion")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.False(validation.Value!.ActivationEligible);
        Assert.True(validation.Value.IncorrectApplicationCanaries >= 1);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", false, "block incorrect"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_unexplained_conflict_canaries()
    {
        var catA = await CreateCategoryAsync("CA");
        var catB = await CreateCategoryAsync("CB");
        var vA = await SaveDraftAsync(catA.CategoryId, "clash", ruleId: "rule-ca");
        var vB = await SaveDraftAsync(catB.CategoryId, "clash", ruleId: "rule-cb");
        var path = await WriteBoundCorpusAsync([("clash", catA.CategoryId, "suggestion")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.False(validation.Value!.ActivationEligible);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", false, "block conflict"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_drift_canaries()
    {
        var category = await CreateCategoryAsync("Drift");
        var versionId = await SaveDraftAsync(category.CategoryId, "different");
        var path = await WriteBoundCorpusAsync([("no match", null, "conflict")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.False(validation.Value!.ActivationEligible);
        Assert.True(validation.Value.DriftCanaryCount >= 1);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", false, "block drift"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_empty_corpus_evidence_as_not_eligible()
    {
        var category = await CreateCategoryAsync("Empty");
        var versionId = await SaveDraftAsync(category.CategoryId, "empty");
        var path = Path.Combine(root, "empty.jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.False(validation.Value!.ActivationEligible);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", false, "empty block"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    // ── Broad apply ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Broad_apply_defaults_false_even_when_evidence_is_eligible()
    {
        var category = await CreateCategoryAsync("BroadOff");
        var versionId = await SaveDraftAsync(category.CategoryId, "broad off");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "broad off");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "no broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.BroadApplyAllowed);
    }

    [Fact]
    public async Task Broad_apply_true_requires_eligible_evidence_and_explicit_request()
    {
        var category = await CreateCategoryAsync("BroadOn");
        var versionId = await SaveDraftAsync(category.CategoryId, "broad on");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "broad on");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, true, "grant broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.BroadApplyAllowed);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var events = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
            connection, null, result.Value.RuleSetVersionId, CancellationToken.None);
        Assert.Contains(events, e => e.ResultingState == RuleLifecyclePolicy.StateActiveBroadApply);
    }

    [Fact]
    public async Task Broad_apply_request_fails_closed_on_ineligible_evidence_without_pointer_change()
    {
        var category = await CreateCategoryAsync("BroadBad");
        var other = await CreateCategoryAsync("OtherBroad");
        var versionId = await SaveDraftAsync(category.CategoryId, "bad broad");
        var path = await WriteBoundCorpusAsync([("bad broad", other.CategoryId, "suggestion")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.False(validation.Value!.ActivationEligible);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", true, "deny broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    // ── Atomicity / immutability ─────────────────────────────────────────────

    [Fact]
    public async Task Activate_is_atomic_and_preserves_prior_pointer_on_failure()
    {
        var category = await CreateCategoryAsync("Atomic");
        var versionId = await SaveDraftAsync(category.CategoryId, "atomic");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "atomic");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        var first = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "first"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var priorPointer = first.Value!.RuleSetVersionId;

        var failed = await activate.HandleAsync(
            ActivateRequest("no-such-validation", "missing-receipt", false, "fail"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ValidationNotFound, failed.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.Equal(priorPointer, pointer!.RuleSetVersionId);
        Assert.Equal(1L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Activate_never_mutates_rule_version_rows_in_place()
    {
        var category = await CreateCategoryAsync("Immutable");
        var versionId = await SaveDraftAsync(category.CategoryId, "immutable");
        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        {
            var before = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
            Assert.NotNull(before);
            var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "immutable");
            var validationId = granted.ValidationId;
            var receiptId = granted.ReceiptId;
            var result = await activate.HandleAsync(
                ActivateRequest(validationId, receiptId, false, "keep draft"),
                actor, NextKey(), CancellationToken.None);
            Assert.True(result.IsSuccess, result.ErrorCode);
            var after = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
            Assert.Equal(before!.ScopeHash, after!.ScopeHash);
            Assert.Equal(before.CategoryId, after.CategoryId);
            Assert.Equal(before.Reason, after.Reason);
            Assert.Equal(before.LifecycleState, after.LifecycleState);
            Assert.Equal(before.BroadApplyAllowed, after.BroadApplyAllowed);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
        }
    }

    [Fact]
    public async Task Activate_idempotent_replay_returns_same_rule_set_without_duplication()
    {
        var category = await CreateCategoryAsync("Idem");
        var versionId = await SaveDraftAsync(category.CategoryId, "idem");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "idem");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        const string key = "activate-idem-1";
        var first = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "idem"),
            actor, key, CancellationToken.None);
        var second = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "idem"),
            actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.RuleSetVersionId, second.Value!.RuleSetVersionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_conflict_on_idempotency_key_reuse_with_different_payload()
    {
        var category = await CreateCategoryAsync("IdemConflict");
        var versionId = await SaveDraftAsync(category.CategoryId, "conflict key");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "conflict key");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        const string key = "activate-conflict-1";
        var first = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "reason-a"),
            actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "reason-b"),
            actor, key, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, second.ErrorCode);
    }

    // ── Category identity ────────────────────────────────────────────────────

    [Fact]
    public async Task Same_id_category_rename_does_not_block_activation()
    {
        var category = await CreateCategoryAsync("RenameMe");
        var versionId = await SaveDraftAsync(category.CategoryId, "rename me");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "rename me");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        await RenameCategoryAsync(category.CategoryId, "Renamed Display " + Guid.NewGuid().ToString("N")[..6]);

        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "rename ok"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var version = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        Assert.Equal(category.CategoryId, version!.CategoryId);
    }

    [Fact]
    public async Task Archived_category_blocks_activation_and_preserves_empty_pointer()
    {
        var category = await CreateCategoryAsync("ArchiveMe");
        var versionId = await SaveDraftAsync(category.CategoryId, "archive me");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "archive me");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        await ArchiveCategoryAsync(category.CategoryId);

        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "archive block"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Category_catalogue_identity_drift_marks_evidence_stale()
    {
        var category = await CreateCategoryAsync("DriftCat");
        var versionId = await SaveDraftAsync(category.CategoryId, "drift cat");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "drift cat");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        // Adding another active category changes the catalogue lifecycle fingerprint.
        _ = await CreateCategoryAsync("ExtraActive");

        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "stale catalogue"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    // ── Replacement / multi-candidate ────────────────────────────────────────

    [Fact]
    public async Task Activate_successor_references_prior_rule_set_without_deleting_history()
    {
        var category = await CreateCategoryAsync("Replace");
        var v1 = await SaveDraftAsync(category.CategoryId, "first rule", ruleId: "rule-first");
        var granted1 = await ValidateAndGrantAsync(v1, category.CategoryId, "first rule");
        var validation1 = granted1.ValidationId;
        var receiptId1 = granted1.ReceiptId;
        var first = await activate.HandleAsync(
            ActivateRequest(validation1, receiptId1, false, "first set"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var v2 = await SaveDraftAsync(category.CategoryId, "second rule", ruleId: "rule-second");
        // Validate only v2 so candidate fingerprint resolves uniquely to the successor.
        var granted2 = await ValidateAndGrantAsync(v2, category.CategoryId, "second rule");
        var validation2 = granted2.ValidationId;
        var receiptId2 = granted2.ReceiptId;
        var second = await activate.HandleAsync(
            ActivateRequest(validation2, receiptId2, false, "replace set"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.NotEqual(first.Value!.RuleSetVersionId, second.Value!.RuleSetVersionId);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var successor = await ruleSetStore.GetRuleSetVersionAsync(
            connection, null, second.Value.RuleSetVersionId, CancellationToken.None);
        Assert.Equal(first.Value.RuleSetVersionId, successor!.PriorRuleSetVersionId);
        Assert.Equal(2L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
        var priorMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, first.Value.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([v1], priorMembers);
        var nextMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, second.Value.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([v2], nextMembers);
    }

    [Fact]
    public async Task Multi_candidate_validation_activates_exact_member_set()
    {
        var catA = await CreateCategoryAsync("MultiA");
        var catB = await CreateCategoryAsync("MultiB");
        var vA = await SaveDraftAsync(catA.CategoryId, "alpha", ruleId: "rule-alpha");
        var vB = await SaveDraftAsync(catB.CategoryId, "beta", ruleId: "rule-beta");
        var path = await WriteBoundCorpusAsync([
            ("alpha", catA.CategoryId, "suggestion"),
            ("beta", catB.CategoryId, "suggestion")
        ]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.True(validation.Value!.ActivationEligible);

        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value.ValidationId, "missing-receipt", false, "multi"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, result.Value!.RuleSetVersionId, CancellationToken.None);
        Assert.Equal(2, members.Count);
        Assert.Contains(vA, members);
        Assert.Contains(vB, members);
    }

    // ── Boundary ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Activate_requires_actor_idempotency_and_reason()
    {
        var category = await CreateCategoryAsync("Env");
        var versionId = await SaveDraftAsync(category.CategoryId, "env");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "env");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        var noActor = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "x"),
            null, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, noActor.ErrorCode);
        var noKey = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "x"),
            actor, null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, noKey.ErrorCode);
        var noReason = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "  "),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, noReason.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_never_mutates_ledger_categories()
    {
        var category = await CreateCategoryAsync("LedgerSafe");
        var versionId = await SaveDraftAsync(category.CategoryId, "ledger safe");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "ledger safe");
        var validationId = granted.ValidationId;
        var receiptId = granted.ReceiptId;
        var beforeName = category.Name;
        var result = await activate.HandleAsync(
            ActivateRequest(validationId, receiptId, false, "no ledger write"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var listed = await ledger.ListClassificationCategoriesAsync("1.0", actor, CancellationToken.None, status: null);
        var after = Assert.Single(listed.Value!.Items, x => x.CategoryId == category.CategoryId);
        Assert.Equal(beforeName, after.Name);
        Assert.Equal(CategoryStatus.Active, after.Status);
    }

    [Fact]
    public async Task Policy_authorize_broad_apply_is_false_by_default()
    {
        var report = new ClassificationValidationReportRow(
            "vid", 1, 1, 1, 0, 0, 0, 10000, 0, 0, 0, 0, 0, null, null, new string('a', 64));
        Assert.False(RuleLifecyclePolicy.AuthorizeBroadApply(false, report, null));
        Assert.True(RuleLifecyclePolicy.AuthorizeBroadApply(true, report, null));
        Assert.False(RuleLifecyclePolicy.AuthorizeBroadApply(true, report, ClassifyErrors.Lifecycle));
    }

    [Fact]
    public async Task Activate_rejects_missing_owner_gate_receipt_without_pointer_change()
    {
        var category = await CreateCategoryAsync("NoReceipt");
        var versionId = await SaveDraftAsync(category.CategoryId, "no receipt");
        var path = await WriteBoundCorpusAsync([("no receipt", category.CategoryId, "suggestion")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        var result = await activate.HandleAsync(
            ActivateRequest(validation.Value!.ValidationId, "missing-receipt", false, "no receipt"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_blocked_receipt_without_authority()
    {
        var category = await CreateCategoryAsync("BlockedReceipt");
        var versionId = await SaveDraftAsync(category.CategoryId, "blocked receipt");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "blocked receipt", benefitDecision: "defer-broad");
        var result = await activate.HandleAsync(
            ActivateRequest(granted.ValidationId, granted.ReceiptId, false, "blocked"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Activate_rejects_validation_id_not_bound_to_receipt_representative()
    {
        var category = await CreateCategoryAsync("BindMismatch");
        var versionId = await SaveDraftAsync(category.CategoryId, "bind mismatch");
        var granted = await ValidateAndGrantAsync(versionId, category.CategoryId, "bind mismatch");
        // Hold-out validation id is not the representative run bound on the receipt.
        var path = await WriteBoundCorpusAsync([("bind mismatch", category.CategoryId, "suggestion")]);
        var holdOnly = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(holdOnly.IsSuccess, holdOnly.ErrorCode);
        var result = await activate.HandleAsync(
            ActivateRequest(holdOnly.Value!.ValidationId, granted.ReceiptId, false, "mismatch"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> ValidateEligibleAsync(string versionId, string categoryId, string description)
    {
        var granted = await ValidateAndGrantAsync(versionId, categoryId, description);
        return granted.ValidationId;
    }

    /// <summary>
    /// Production path: representative + independent replay + hold-out finalize through rule.validate.
    /// Returns the representative validation id (activation binding) and trusted receipt id.
    /// </summary>
    private async Task<(string ValidationId, string ReceiptId)> ValidateAndGrantAsync(
        string versionId,
        string categoryId,
        string description,
        string? benefitDecision = "approve-broad")
    {
        var path = await WriteBoundCorpusAsync([(description, categoryId, "suggestion")]);
        var rep = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        Assert.True(rep.Value!.ActivationEligible);

        var replay = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);

        var hold = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                [versionId],
                path,
                rep.Value.ValidationId,
                replay.Value!.ValidationId,
                OwnerDecisionCountBefore: 10,
                OwnerDecisionCountAfter: 2,
                ExplicitBenefitDecision: benefitDecision),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(hold.Value!.OwnerRulebookGateReceiptId));
        Assert.False(string.IsNullOrWhiteSpace(hold.Value.OwnerRulebookGateReceiptFingerprint));
        return (rep.Value.ValidationId, hold.Value.OwnerRulebookGateReceiptId!);
    }

    private static ClassifyRuleActivateRequest ActivateRequest(
        string validationId,
        string receiptId,
        bool broadApply,
        string reason) =>
        new(ClassifyOperationIds.ContractVersion, validationId, receiptId, broadApply, reason);

    private async Task<string> SaveDraftAsync(string categoryId, string description, string? ruleId = null)
    {
        var result = await save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "activation draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string? ExpectedCategory, string ExpectedKind)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description, "-12.34");
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ValidateClassificationRuleCommand.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ValidateClassificationRuleCommand.ComputeItemLifecycleFingerprint(item);
            var expected = rows[i];
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                expected.ExpectedKind, expected.ExpectedCategory));
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static string CorpusLine(
        int ordinal,
        string transactionId,
        string accountId,
        string description,
        string? direction,
        long absoluteMinor,
        string lifecycle,
        string expectedKind,
        string? expectedCategory)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ordinal\":").Append(ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(transactionId));
        sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(accountId));
        sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
        sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
        sb.Append(",\"amountAbsoluteMinor\":").Append(absoluteMinor.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(lifecycle));
        sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expectedKind));
        if (expectedCategory is not null)
        {
            sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expectedCategory));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Act Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "activation-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "activation-test"),
            NextKey(),
            LedgerJsonContext.Default.RenameCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> RecordAsync(string description, string amount)
    {
        var digestText = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                amount,
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(
                    EvidenceKind.AgentCapture,
                    digestText,
                    "activate-capture:" + Guid.NewGuid().ToString("N")[..8],
                    null,
                    null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? idempotencyKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)
            ?? throw new InvalidOperationException($"Missing {operationId}");
        var inputElement = JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope("1.0", actor, inputElement, idempotencyKey);
        var requestJson = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = descriptor.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        var processResult = await process.RunAsync(arguments, requestJson, CancellationToken.None);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)
            ?? throw new InvalidOperationException("No envelope");
        Assert.Equal(0, processResult.ExitCode);
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"act-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
