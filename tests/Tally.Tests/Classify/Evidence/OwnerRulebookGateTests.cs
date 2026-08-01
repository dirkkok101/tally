using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Evidence;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evidence;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-GATE-OWNER-RULEBOOK / TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE / bd-56yx
/// Named gate families for scripts/verify-classify-owner-rulebook.sh discovery.
/// Synthetic owner-only corpora bound to public classification_v1 projection — never personal values.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class OwnerRulebookGateTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-owner-gate-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "owner-rulebook-gate", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private ClassificationValidationStore validationStore = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
    private PrivateCorpusReader corpusReader = null!;
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
        var classify = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = classify.Store;
        ruleStore = new ClassificationRuleStore();
        validationStore = new ClassificationValidationStore();
        corpusReader = ClassifyCorpusExtensions.CreateReader();
        save = new SaveClassificationRuleCommand(store, ruleStore, ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store, ruleStore, validationStore, corpusReader, ledger, classify.Idempotency);
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

    // ── Named gate families ──────────────────────────────────────────────────

    [Fact]
    public async Task Gate_permission_rejects_group_readable_and_symlink_corpus()
    {
        var category = await CreateCategoryAsync("Perm");
        var versionId = await SaveDraftAsync(category.CategoryId, "shop");
        var path = await WriteBoundCorpusAsync([("shop", category.CategoryId, "suggestion")]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var group = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, group.ErrorCode);

        var target = await WriteBoundCorpusAsync([("shop", category.CategoryId, "suggestion")]);
        var link = Path.Combine(root, "link.jsonl");
        File.CreateSymbolicLink(link, target);
        var sym = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], link),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.SymlinkRejected, sym.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Gate_public_contract_uses_ledger_classification_projection_surface()
    {
        Assert.NotNull(typeof(LedgerContractClient).GetMethod(
            nameof(LedgerContractClient.QueryClassificationProjectionAsync)));
        Assert.NotNull(typeof(LedgerContractClient).GetMethod(
            nameof(LedgerContractClient.ListClassificationCategoriesAsync)));

        var listed = await ledger.ListClassificationCategoriesAsync(
            CategoryContractVersions.Current, actor, CancellationToken.None, status: CategoryStatus.Active);
        Assert.True(listed.IsSuccess, listed.Error?.Code);

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.Value!.ProjectionVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.Value.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(page.Value.StoreGenerationFingerprint));
        Assert.DoesNotContain("SELECT", page.StandardError ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gate_projection_mismatch_rejects_before_authority()
    {
        var category = await CreateCategoryAsync("Mismatch");
        var versionId = await SaveDraftAsync(category.CategoryId, "real desc");
        var tx = await RecordAsync("real desc", "-10.00");
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess);
        var item = page.Value!.ClassificationItems!.Single(i => i.TransactionId == tx.TransactionId);
        var life = ValidateClassificationRuleCommand.ComputeItemLifecycleFingerprint(item);
        // Wrong description vs public projection.
        var path = WriteOwnerFile(CorpusLine(
            0, tx.TransactionId, item.AccountId, "WRONG DESCRIPTION", "outflow", 1000, life,
            "suggestion", category.CategoryId));

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM validation_run;"));
    }

    [Fact]
    public async Task Gate_projection_provenance_binds_snapshot_and_store_generation()
    {
        var category = await CreateCategoryAsync("Prov");
        var versionId = await SaveDraftAsync(category.CategoryId, "prov merchant");
        var path = await WriteBoundCorpusAsync([("prov merchant", category.CategoryId, "suggestion")]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, result.Value!.ProjectionVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.CandidateFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ExpectedOutcomeFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ReportFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.OutcomesCanonicalHash));
        Assert.Equal(64, result.Value.OutcomesCanonicalHash.Length);
    }

    [Fact]
    public async Task Gate_90_day_corpus_window_metadata_is_aggregate_only()
    {
        var category = await CreateCategoryAsync("D90");
        var versionId = await SaveDraftAsync(category.CategoryId, "month");
        var path = await WriteBoundCorpusAsync([
            ("month", category.CategoryId, "suggestion"),
            ("other", category.CategoryId, "no_suggestion")
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var benefit = new OwnerBenefitEvidenceReceipt(10, 4, 40.0, 18.0);
        var value = result.Value!;
        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            value,
            replay: value,
            holdOut: value,
            benefit: benefit,
            explicitBenefitDecision: null);
        Assert.Equal(value.TotalRows, receipt.EligibleRows);
        Assert.Equal(value.CandidateFingerprint, receipt.CandidateFingerprint);
        Assert.False(receipt.AuthorityGranted);
        Assert.True(receipt.RequiresExplicitOwnerBenefitDecision);
        Assert.Equal(VerifiedOwnerRulebookGateReceipt.BlockBenefitDecisionRequired, receipt.BlockCode);
        AssertNoPrivate(JsonSerializer.Serialize(receipt, ClassifyJsonContext.Default.VerifiedOwnerRulebookGateReceipt));
    }

    [Fact]
    public async Task Gate_hold_out_partition_is_separately_accounted()
    {
        var category = await CreateCategoryAsync("Hold");
        var versionId = await SaveDraftAsync(category.CategoryId, "train");
        var train = await WriteBoundCorpusAsync([("train", category.CategoryId, "suggestion")]);
        var hold = await WriteBoundCorpusAsync([("holdout-merchant", category.CategoryId, "no_suggestion")]);

        var trainResult = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], train),
            actor, NextKey(), CancellationToken.None);
        var holdResult = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], hold),
            actor, NextKey(), CancellationToken.None);
        Assert.True(trainResult.IsSuccess && holdResult.IsSuccess, trainResult.ErrorCode + holdResult.ErrorCode);
        Assert.NotEqual(trainResult.Value!.CorpusFingerprint, holdResult.Value!.CorpusFingerprint);
        Assert.Equal(1, trainResult.Value.TotalRows);
        Assert.Equal(1, holdResult.Value.TotalRows);
    }

    [Fact]
    public async Task Gate_recurrence_equals_rule_is_deterministic_across_replays()
    {
        var category = await CreateCategoryAsync("Recur");
        var versionId = await SaveDraftAsync(category.CategoryId, "recurring merchant");
        var path = await WriteBoundCorpusAsync([
            ("recurring merchant", category.CategoryId, "suggestion"),
            ("recurring merchant", category.CategoryId, "suggestion")
        ]);
        var a = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, "recur-key-1", CancellationToken.None);
        var b = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, "recur-key-1", CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess, a.ErrorCode);
        Assert.Equal(a.Value!.ValidationId, b.Value!.ValidationId);
        Assert.Equal(a.Value.OutcomesCanonicalHash, b.Value.OutcomesCanonicalHash);
        Assert.Equal(a.Value.ReportFingerprint, b.Value.ReportFingerprint);
    }

    [Fact]
    public async Task Gate_determinism_fresh_key_replay_matches_outcomes_hash()
    {
        var category = await CreateCategoryAsync("Det");
        var versionId = await SaveDraftAsync(category.CategoryId, "stable");
        var path = await WriteBoundCorpusAsync([
            ("stable", category.CategoryId, "suggestion"),
            ("zzz", category.CategoryId, "no_suggestion")
        ]);
        var a = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        var b = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess, a.ErrorCode);
        Assert.Equal(a.Value!.OutcomesCanonicalHash, b.Value!.OutcomesCanonicalHash);
        Assert.NotEqual(a.Value.ValidationId, b.Value.ValidationId);

        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            a.Value, b.Value, b.Value,
            new OwnerBenefitEvidenceReceipt(2, 1),
            explicitBenefitDecision: "approve-broad");
        Assert.True(receipt.DeterministicReplayPassed);
        Assert.True(receipt.SafetyPassed);
        Assert.True(receipt.AuthorityGranted);
    }

    [Fact]
    public void Gate_timing_benefit_fields_are_aggregate_only()
    {
        var benefit = ClassifyCorpusExtensions.CreateBenefitReceipt(12, 5, 55.0, 22.5);
        var json = JsonSerializer.Serialize(benefit, ClassifyJsonContext.Default.OwnerBenefitEvidenceReceipt);
        AssertNoPrivate(json);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_decision_reduction_does_not_invent_fifty_percent_threshold()
    {
        var benefit = new OwnerBenefitEvidenceReceipt(10, 6, 30.0, 20.0);
        // Synthetic validation with safety pass but no benefit decision.
        var synthetic = new ClassifyRuleValidateResult(
            ClassifyOperationIds.ContractVersion,
            "vid",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            ClassificationProjectionVersions.ClassificationV1,
            "snap",
            "2099-01-01T00:00:00Z",
            new string('d', 64),
            new string('e', 64),
            NormalizationDescriptor.V1.Version,
            new string('f', 64),
            new string('0', 64),
            TotalRows: 1,
            AccountedRows: 1,
            SuggestionCount: 1,
            NoSuggestionCount: 0,
            ConflictCount: 0,
            StaleCount: 0,
            CoverageBasisPoints: 10000,
            DriftCanaryCount: 0,
            IncorrectApplicationCanaries: 0,
            UnexplainedConflictCount: 0,
            ActivationEligible: true);
        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            synthetic, synthetic, synthetic, benefit, explicitBenefitDecision: null);
        Assert.True(receipt.SafetyPassed);
        Assert.False(receipt.BenefitSufficient);
        Assert.True(receipt.RequiresExplicitOwnerBenefitDecision);
        Assert.False(receipt.AuthorityGranted);
        Assert.Equal(VerifiedOwnerRulebookGateReceipt.BlockBenefitDecisionRequired, receipt.BlockCode);
        Assert.DoesNotContain("50", receipt.BlockCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_row_accounting_partitions_eligible_suggested_conflict_no_suggestion()
    {
        var catA = await CreateCategoryAsync("AccA");
        var catB = await CreateCategoryAsync("AccB");
        var vA = await SaveDraftAsync(catA.CategoryId, "alpha", "rule-acc-a");
        var vB = await SaveDraftAsync(catB.CategoryId, "beta", "rule-acc-b");
        var vClashA = await SaveDraftAsync(catA.CategoryId, "clash", "rule-clash-a");
        var vClashB = await SaveDraftAsync(catB.CategoryId, "clash", "rule-clash-b");
        var path = await WriteBoundCorpusAsync([
            ("alpha", catA.CategoryId, "suggestion"),
            ("beta", catB.CategoryId, "suggestion"),
            ("none", null, "no_suggestion"),
            ("clash", null, "conflict")
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                [vA, vB, vClashA, vClashB],
                path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(4, result.Value!.TotalRows);
        Assert.Equal(result.Value.TotalRows, result.Value.AccountedRows);
        Assert.Equal(
            result.Value.TotalRows,
            result.Value.SuggestionCount + result.Value.NoSuggestionCount
            + result.Value.ConflictCount + result.Value.StaleCount);

        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            result.Value, result.Value, result.Value,
            new OwnerBenefitEvidenceReceipt(4, 2),
            explicitBenefitDecision: "approve-broad");
        Assert.Equal(result.Value.SuggestionCount, receipt.SuggestedRows);
        Assert.Equal(result.Value.NoSuggestionCount, receipt.NoSuggestionRows);
        Assert.Equal(result.Value.ConflictCount, receipt.ConflictRows);
    }

    [Fact]
    public async Task Gate_incorrect_apply_blocks_authority()
    {
        var category = await CreateCategoryAsync("Wrong");
        var other = await CreateCategoryAsync("Other");
        var versionId = await SaveDraftAsync(category.CategoryId, "target");
        var path = await WriteBoundCorpusAsync([
            ("target", other.CategoryId, "suggestion")
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
        Assert.True(result.Value.IncorrectApplicationCanaries >= 1);

        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            result.Value, result.Value, result.Value,
            new OwnerBenefitEvidenceReceipt(1, 0),
            explicitBenefitDecision: "approve-broad");
        Assert.False(receipt.AuthorityGranted);
        Assert.False(receipt.SafetyPassed);
        Assert.True(receipt.IncorrectApplicationCanaries >= 1);
    }

    [Fact]
    public async Task Gate_conflict_expected_is_explained_unexplained_blocks()
    {
        var catA = await CreateCategoryAsync("CXA");
        var catB = await CreateCategoryAsync("CXB");
        var vA = await SaveDraftAsync(catA.CategoryId, "both", "rule-cx-a");
        var vB = await SaveDraftAsync(catB.CategoryId, "both", "rule-cx-b");

        var explainedPath = await WriteBoundCorpusAsync([("both", null, "conflict")]);
        var explained = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], explainedPath),
            actor, NextKey(), CancellationToken.None);
        Assert.True(explained.IsSuccess, explained.ErrorCode);
        Assert.True(explained.Value!.ActivationEligible);

        var unexplainedPath = await WriteBoundCorpusAsync([("both", catA.CategoryId, "suggestion")]);
        var unexplained = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], unexplainedPath),
            actor, NextKey(), CancellationToken.None);
        Assert.True(unexplained.IsSuccess, unexplained.ErrorCode);
        Assert.False(unexplained.Value!.ActivationEligible);
    }

    [Fact]
    public async Task Gate_drift_stale_item_fails_safety()
    {
        var category = await CreateCategoryAsync("Drift");
        var versionId = await SaveDraftAsync(category.CategoryId, "ok");
        var life = Hex64("life-drift");
        var items = new[]
        {
            new ClassificationEvaluationItem(
                0, "tx-0", "acct", "ok", ClassificationRuleVocabulary.DirectionOutflow, 1, life,
                itemStaleDimensions: [EvaluationFingerprint.DimensionOrderedItems])
        };
        var rules = await LoadActiveRulesAsync([versionId]);
        var fp = EvaluationFingerprint.Create(
            "1.0", "classification_v1", Hex64("g"), "snap", "2099-01-01T00:00:00.0000000Z",
            Hex64("c"), NormalizationDescriptor.V1.Version, Hex64("r"), Hex64("i"));
        var evaluation = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
            fp, items, rules, new HashSet<string>(StringComparer.Ordinal) { category.CategoryId }));
        Assert.Equal(1, evaluation.StaleCount);
        var syntheticRows = new[]
        {
            new PrivateCorpusRow(0, "tx-0", "acct", "ok", "outflow", 1, life, "suggestion", category.CategoryId)
        };
        var built = ValidationReportBuilder.Build("val-drift", syntheticRows, evaluation);
        Assert.False(built.ActivationEligible);
        Assert.True(built.Report.DriftCanaryCount >= 1);
    }

    [Fact]
    public async Task Gate_locality_uses_disposable_data_root_without_active_set_mutation()
    {
        Assert.Contains(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), store.Paths.DataRoot, StringComparison.Ordinal);
        var category = await CreateCategoryAsync("Local");
        var versionId = await SaveDraftAsync(category.CategoryId, "local");
        var path = await WriteBoundCorpusAsync([("local", category.CategoryId, "suggestion")]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
    }

    [Fact]
    public async Task Gate_disclosure_receipt_and_errors_exclude_paths_and_payloads()
    {
        const string canary = "CANARY_PRIVATE_OWNER_DESC";
        var category = await CreateCategoryAsync("Disc");
        var versionId = await SaveDraftAsync(category.CategoryId, canary);
        var path = await WriteBoundCorpusAsync([(canary, category.CategoryId, "suggestion")]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var value = result.Value!;

        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            value, value, value,
            new OwnerBenefitEvidenceReceipt(1, 0),
            explicitBenefitDecision: "approve-broad");
        var json = JsonSerializer.Serialize(receipt, ClassifyJsonContext.Default.VerifiedOwnerRulebookGateReceipt);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(path, json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);

        var bad = Path.Combine(root, "bad-" + canary + ".jsonl");
        File.WriteAllText(bad, "{bad\n");
        File.SetUnixFileMode(bad, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var fail = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], bad),
            actor, NextKey(), CancellationToken.None);
        Assert.False(fail.IsSuccess);
        Assert.DoesNotContain(canary, fail.ErrorCode!, StringComparison.Ordinal);
        Assert.DoesNotContain(bad, fail.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_missing_owner_inputs_yield_stable_blocked_receipt()
    {
        var receipt = VerifiedOwnerRulebookGateReceipt.MissingOwnerInputs();
        Assert.False(receipt.AuthorityGranted);
        Assert.False(receipt.SafetyPassed);
        Assert.Equal(VerifiedOwnerRulebookGateReceipt.BlockInputMissing, receipt.BlockCode);
        Assert.Equal(0, receipt.EligibleRows);
        Assert.Null(receipt.CandidateFingerprint);
        Assert.Null(receipt.CorpusFingerprint);
        Assert.True(receipt.DisclosurePassed);
        Assert.True(receipt.LocalityPassed);
        Assert.Equal(VerifiedOwnerRulebookGateReceipt.Kind, receipt.ReceiptKind);
        AssertNoPrivate(JsonSerializer.Serialize(receipt, ClassifyJsonContext.Default.VerifiedOwnerRulebookGateReceipt));
    }

    [Fact]
    public async Task Gate_mixed_sign_account_fee_transfer_refund_medical_canaries_are_labels_only()
    {
        var labels = new[]
        {
            "mixed-shape", "sign-inflow", "account-bound", "fee-like",
            "transfer-like", "refund-like", "shared-medical"
        };
        var category = await CreateCategoryAsync("CanaryBag");
        var versionId = await SaveDraftAsync(category.CategoryId, "account-bound");
        var specs = labels.Select(label => (
            label,
            label == "account-bound" ? category.CategoryId : (string?)null,
            label == "account-bound" ? "suggestion" : "no_suggestion")).ToArray();
        var path = await WriteBoundCorpusAsync(specs);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(labels.Length, result.Value!.TotalRows);
        var value = result.Value!;
        var receipt = VerifiedOwnerRulebookGateReceipt.Derive(
            value, value, value,
            new OwnerBenefitEvidenceReceipt(labels.Length, labels.Length - 1),
            explicitBenefitDecision: null,
            descriptionInferredRelationshipCount: 0);
        Assert.Equal(0, receipt.DescriptionInferredRelationshipCount);
        Assert.False(receipt.AuthorityGranted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Record uncategorized ledger transactions and write a private corpus bound to the
    /// frozen public evaluation projection (account/description/direction/amount/lifecycle).
    /// </summary>
    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string? ExpectedCategory, string ExpectedKind)> rows)
    {
        var created = new List<(string TxId, string Description, string Amount)>();
        foreach (var row in rows)
        {
            var amount = row.Description.Contains("inflow", StringComparison.Ordinal) ? "15.00" : "-12.34";
            var tx = await RecordAsync(row.Description, amount);
            created.Add((tx.TransactionId, row.Description, amount));
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
            var (txId, description, _) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item), "projection missing recorded transaction");
            Assert.True(ValidateClassificationRuleCommand.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ValidateClassificationRuleCommand.ComputeItemLifecycleFingerprint(item);
            var expected = rows[i];
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                expected.ExpectedKind, expected.ExpectedCategory));
        }

        return WriteOwnerFile(string.Join('\n', lines) + "\n");
    }

    private async Task<IReadOnlyList<ActiveRuleVersion>> LoadActiveRulesAsync(IReadOnlyList<string> versionIds)
    {
        var rules = new List<ActiveRuleVersion>();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        foreach (var id in versionIds)
        {
            var version = await ruleStore.GetRuleVersionAsync(connection, null, id, CancellationToken.None)
                ?? throw new InvalidOperationException("missing version");
            var conditions = await ruleStore.ListConditionsAsync(connection, null, id, CancellationToken.None);
            rules.Add(new ActiveRuleVersion(version.RuleVersionId, version.CategoryId, conditions));
        }

        return rules;
    }

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
                "owner-rulebook gate draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private string WriteOwnerFile(string content)
    {
        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
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
        if (direction is null)
        {
            sb.Append(",\"amountDirection\":null");
        }
        else
        {
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
        }

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

    private static string Hex64(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    [Fact]
    public async Task Finalize_persists_trusted_receipt_and_rejects_caller_forged_authority()
    {
        var category = await CreateCategoryAsync("GateFinalize");
        var versionId = await SaveDraftAsync(category.CategoryId, "gate finalize");
        var path = await WriteBoundCorpusAsync([("gate finalize", category.CategoryId, "suggestion")]);
        var rep = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                [versionId],
                path,
                rep.Value!.ValidationId,
                replay.Value!.ValidationId,
                OwnerDecisionCountBefore: 8,
                OwnerDecisionCountAfter: 1,
                ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(hold.Value!.OwnerRulebookGateReceiptId));
        Assert.False(string.IsNullOrWhiteSpace(hold.Value.OwnerRulebookGateReceiptFingerprint));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var receiptStore = new OwnerRulebookGateReceiptStore();
        var stored = await receiptStore.GetAsync(
            connection, null, hold.Value.OwnerRulebookGateReceiptId!, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.AuthorityGranted);
        Assert.Null(stored.BlockCode);
        Assert.Equal(rep.Value.ValidationId, stored.RepresentativeValidationRunId);
        Assert.Equal(replay.Value.ValidationId, stored.IndependentReplayValidationRunId);
        Assert.Equal(hold.Value.ValidationId, stored.HoldOutValidationRunId);
        Assert.Equal(hold.Value.OwnerRulebookGateReceiptFingerprint, stored.ReceiptFingerprint);
        // No private path/candidate IDs in durable receipt identity fields.
        Assert.DoesNotContain('/', stored.ReceiptId);
        AssertNoPrivate(JsonSerializer.Serialize(
            OwnerRulebookGateReceiptStore.ToContract(stored),
            ClassifyJsonContext.Default.VerifiedOwnerRulebookGateReceipt));
    }

    [Fact]
    public async Task Finalization_inputs_are_bound_to_validate_idempotency_fingerprint()
    {
        var category = await CreateCategoryAsync("GateIdempotency");
        var versionId = await SaveDraftAsync(category.CategoryId, "gate idempotency");
        var path = await WriteBoundCorpusAsync([("gate idempotency", category.CategoryId, "suggestion")]);
        var rep = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        var replay = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess && replay.IsSuccess, rep.ErrorCode ?? replay.ErrorCode);

        const string reusedKey = "validate-finalization-idempotency";
        var plainHoldOut = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, reusedKey, CancellationToken.None);
        Assert.True(plainHoldOut.IsSuccess, plainHoldOut.ErrorCode);
        Assert.Null(plainHoldOut.Value!.OwnerRulebookGateReceiptId);

        var changedRequest = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                [versionId],
                path,
                rep.Value!.ValidationId,
                replay.Value!.ValidationId,
                OwnerDecisionCountBefore: 8,
                OwnerDecisionCountAfter: 1,
                ExplicitBenefitDecision: "approve-broad"),
            actor, reusedKey, CancellationToken.None);

        Assert.Equal(ClassifyErrors.IdempotencyConflict, changedRequest.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM owner_rulebook_gate_receipt;"));
    }

    private static void AssertNoPrivate(string text)
    {
        Assert.DoesNotContain("CANARY_PRIVATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", text, StringComparison.OrdinalIgnoreCase);
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
        var input = new CreateAccountInput(
            "Gate Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR");
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            input,
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

    private async Task<TransactionDetail> RecordAsync(string description, string amount)
    {
        var digestText = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = new RecordTransactionInput(
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
                "gate-capture:" + Guid.NewGuid().ToString("N")[..8],
                null,
                null));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            input,
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
            ?? throw new InvalidOperationException("No result");
    }

    private string NextKey() => $"gate-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
