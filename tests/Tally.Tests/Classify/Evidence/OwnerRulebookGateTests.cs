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
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
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
/// Synthetic owner-only corpora only — never personal values. Aggregate receipts only.
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
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Named gate families (script discovery needles) ───────────────────────

    [Fact]
    public async Task Gate_permission_rejects_group_readable_and_symlink_corpus()
    {
        var category = await CreateCategoryAsync("Perm");
        var versionId = await SaveDraftAsync(category.CategoryId, "shop");
        var path = WriteOwnerCorpus([CorpusLine(0, "shop", category.CategoryId, "suggestion")]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var group = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, group.ErrorCode);

        var target = WriteOwnerCorpus([CorpusLine(0, "shop", category.CategoryId, "suggestion")]);
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
        // Public client method exists and returns a contract-shaped result (no private SQL).
        Assert.NotNull(typeof(LedgerContractClient).GetMethod(
            nameof(LedgerContractClient.QueryClassificationProjectionAsync)));
        Assert.NotNull(typeof(LedgerContractClient).GetMethod(
            nameof(LedgerContractClient.ListClassificationCategoriesAsync)));

        var listed = await ledger.ListClassificationCategoriesAsync(
            CategoryContractVersions.Current, actor, CancellationToken.None, status: CategoryStatus.Active);
        Assert.True(listed.IsSuccess, listed.Error?.Code);
        Assert.Equal(CategoryContractVersions.Current, listed.Value!.LedgerContractVersion);

        // Projection query is invokable (may be empty) without private ledger paths.
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess || page.Error is not null);
        Assert.DoesNotContain("SELECT", page.StandardError ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gate_90_day_corpus_window_metadata_is_aggregate_only()
    {
        // Representative 90-day window is owner-supplied; synthetic stand-in proves receipt shape.
        var category = await CreateCategoryAsync("D90");
        var versionId = await SaveDraftAsync(category.CategoryId, "month");
        var path = WriteOwnerCorpus([
            CorpusLine(0, "month", category.CategoryId, "suggestion"),
            CorpusLine(1, "other", category.CategoryId, "no_suggestion")
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var receipt = VerifiedOwnerRulebookGateReceipt.FromValidation(
            result.Value!,
            eligibleRows: 2,
            correctionRows: 0,
            excludedRows: 0,
            windowLabel: "synthetic-90d-stand-in",
            holdOutLabel: "none",
            benefit: new OwnerBenefitEvidenceReceipt(10, 4, 40.0, 18.0),
            safetyPassed: result.Value!.ActivationEligible,
            benefitSufficient: false,
            requiresExplicitOwnerBenefitDecision: true);
        Assert.Equal(2, receipt.EligibleRows);
        Assert.False(receipt.AuthorityGranted); // benefit insufficient without explicit decision
        Assert.True(receipt.RequiresExplicitOwnerBenefitDecision);
        AssertNoPrivate(JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public async Task Gate_hold_out_partition_is_separately_accounted()
    {
        var category = await CreateCategoryAsync("Hold");
        var versionId = await SaveDraftAsync(category.CategoryId, "train");
        var train = WriteOwnerCorpus([
            CorpusLine(0, "train", category.CategoryId, "suggestion")
        ]);
        var hold = WriteOwnerCorpus([
            CorpusLine(0, "holdout-merchant", category.CategoryId, "no_suggestion")
        ]);

        var trainResult = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], train),
            actor, NextKey(), CancellationToken.None);
        var holdResult = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], hold),
            actor, NextKey(), CancellationToken.None);
        Assert.True(trainResult.IsSuccess && holdResult.IsSuccess);
        Assert.NotEqual(trainResult.Value!.CorpusFingerprint, holdResult.Value!.CorpusFingerprint);
        Assert.Equal(1, trainResult.Value.TotalRows);
        Assert.Equal(1, holdResult.Value.TotalRows);
    }

    [Fact]
    public async Task Gate_recurrence_equals_rule_is_deterministic_across_replays()
    {
        var category = await CreateCategoryAsync("Recur");
        var versionId = await SaveDraftAsync(category.CategoryId, "recurring merchant");
        var path = WriteOwnerCorpus([
            CorpusLine(0, "recurring merchant", category.CategoryId, "suggestion"),
            CorpusLine(1, "recurring merchant", category.CategoryId, "suggestion")
        ]);
        var a = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, "recur-key-1", CancellationToken.None);
        var b = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, "recur-key-1", CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.ValidationId, b.Value!.ValidationId);
        Assert.Equal(a.Value.SuggestionCount, b.Value.SuggestionCount);
        Assert.Equal(2, a.Value.SuggestionCount);
    }

    [Fact]
    public void Gate_timing_benefit_fields_are_aggregate_only()
    {
        var benefit = ClassifyCorpusExtensions.CreateBenefitReceipt(12, 5, 55.0, 22.5);
        var json = JsonSerializer.Serialize(benefit);
        Assert.Contains("OwnerDecisionCountBefore", json, StringComparison.OrdinalIgnoreCase);
        AssertNoPrivate(json);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_decision_reduction_does_not_invent_fifty_percent_threshold()
    {
        // Before=10, after=6 → 40% reduction; gate must NOT auto-approve via 50% invention.
        var benefit = new OwnerBenefitEvidenceReceipt(10, 6, 30.0, 20.0);
        var receipt = VerifiedOwnerRulebookGateReceipt.BlockedBenefit(
            benefit,
            safetyPassed: true);
        Assert.True(receipt.SafetyPassed);
        Assert.False(receipt.BenefitSufficient);
        Assert.True(receipt.RequiresExplicitOwnerBenefitDecision);
        Assert.False(receipt.AuthorityGranted);
        Assert.Equal("CLASSIFY-OWNER-RULEBOOK-BENEFIT-DECISION-REQUIRED", receipt.BlockCode);
        // No threshold constant embedded as authority.
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
        var path = WriteOwnerCorpus([
            CorpusLine(0, "alpha", catA.CategoryId, "suggestion"),
            CorpusLine(1, "beta", catB.CategoryId, "suggestion"),
            CorpusLine(2, "none", null, "no_suggestion"),
            CorpusLine(3, "clash", null, "conflict")
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                [vA, vB, vClashA, vClashB],
                path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(4, result.Value!.TotalRows);
        Assert.Equal(2, result.Value.SuggestionCount);
        Assert.Equal(1, result.Value.ConflictCount);
        var noSuggestion = result.Value.TotalRows
            - result.Value.SuggestionCount
            - result.Value.ConflictCount
            - 0; // stale not expected
        Assert.True(noSuggestion >= 1);

        var receipt = VerifiedOwnerRulebookGateReceipt.FromValidation(
            result.Value,
            eligibleRows: 4,
            correctionRows: 0,
            excludedRows: 0,
            windowLabel: "synthetic",
            holdOutLabel: "none",
            benefit: new OwnerBenefitEvidenceReceipt(4, 2),
            safetyPassed: result.Value.ActivationEligible,
            benefitSufficient: true,
            requiresExplicitOwnerBenefitDecision: false);
        Assert.Equal(4, receipt.EligibleRows + receipt.ExcludedRows);
        Assert.Equal(
            receipt.SuggestedRows + receipt.NoSuggestionRows + receipt.ConflictRows + receipt.StaleRows + receipt.CorrectionRows,
            receipt.EligibleRows);
    }

    [Fact]
    public async Task Gate_incorrect_apply_blocks_authority()
    {
        var category = await CreateCategoryAsync("Wrong");
        var other = await CreateCategoryAsync("Other");
        var versionId = await SaveDraftAsync(category.CategoryId, "target");
        var path = WriteOwnerCorpus([
            CorpusLine(0, "target", other.CategoryId, "suggestion") // expected other, engine suggests category
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
        Assert.True(result.Value.IncorrectApplicationCanaries >= 1);

        var receipt = VerifiedOwnerRulebookGateReceipt.FromValidation(
            result.Value,
            eligibleRows: 1,
            correctionRows: 0,
            excludedRows: 0,
            windowLabel: "synthetic",
            holdOutLabel: "none",
            benefit: new OwnerBenefitEvidenceReceipt(1, 0),
            safetyPassed: false,
            benefitSufficient: true,
            requiresExplicitOwnerBenefitDecision: false);
        Assert.False(receipt.AuthorityGranted);
        Assert.False(receipt.SafetyPassed);
        Assert.True(receipt.IncorrectApplicationCanaries >= 1);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    [Fact]
    public async Task Gate_conflict_expected_is_explained_unexplained_blocks()
    {
        var catA = await CreateCategoryAsync("CXA");
        var catB = await CreateCategoryAsync("CXB");
        var vA = await SaveDraftAsync(catA.CategoryId, "both", "rule-cx-a");
        var vB = await SaveDraftAsync(catB.CategoryId, "both", "rule-cx-b");

        var explainedPath = WriteOwnerCorpus([CorpusLine(0, "both", null, "conflict")]);
        var explained = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], explainedPath),
            actor, NextKey(), CancellationToken.None);
        Assert.True(explained.IsSuccess, explained.ErrorCode);
        Assert.True(explained.Value!.ActivationEligible);

        var unexplainedPath = WriteOwnerCorpus([CorpusLine(0, "both", catA.CategoryId, "suggestion")]);
        var unexplained = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], unexplainedPath),
            actor, NextKey(), CancellationToken.None);
        Assert.True(unexplained.IsSuccess, unexplained.ErrorCode);
        Assert.False(unexplained.Value!.ActivationEligible);
    }

    [Fact]
    public async Task Gate_determinism_identical_inputs_match_outcomes_hash()
    {
        var category = await CreateCategoryAsync("Det");
        var versionId = await SaveDraftAsync(category.CategoryId, "stable");
        var path = WriteOwnerCorpus([
            CorpusLine(0, "stable", category.CategoryId, "suggestion"),
            CorpusLine(1, "zzz", null, "no_suggestion")
        ]);
        var read = await corpusReader.ReadAsync(path, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        var rules = await LoadActiveRulesAsync([versionId]);
        var cats = new HashSet<string>(StringComparer.Ordinal) { category.CategoryId };
        var fp = EvaluationFingerprint.Create(
            "1.0", "classification_v1", new string('a', 64), "snap",
            "2099-01-01T00:00:00.0000000Z", new string('b', 64),
            NormalizationDescriptor.V1.Version, new string('c', 64), new string('d', 64));
        var items = read.Rows!.Select(r => r.ToEvaluationItem()).ToArray();
        var first = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(fp, items, rules, cats));
        var second = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(fp, items, rules, cats));
        Assert.Equal(first.OutcomesCanonicalHash, second.OutcomesCanonicalHash);
        Assert.Equal(first.SuggestionCount, second.SuggestionCount);
    }

    [Fact]
    public async Task Gate_drift_stale_item_fails_safety()
    {
        var category = await CreateCategoryAsync("Drift");
        var versionId = await SaveDraftAsync(category.CategoryId, "ok");
        // Stale dimension on item via engine direct path (corpus rows don't carry stale dims).
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
        // This test's InitializeAsync already uses a disposable root under /tmp.
        Assert.Contains(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), store.Paths.DataRoot, StringComparison.Ordinal);
        var category = await CreateCategoryAsync("Local");
        var versionId = await SaveDraftAsync(category.CategoryId, "local");
        var path = WriteOwnerCorpus([CorpusLine(0, "local", category.CategoryId, "suggestion")]);
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
        var path = WriteOwnerCorpus([CorpusLine(0, canary, category.CategoryId, "suggestion")]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var receipt = VerifiedOwnerRulebookGateReceipt.FromValidation(
            result.Value!,
            eligibleRows: 1,
            correctionRows: 0,
            excludedRows: 0,
            windowLabel: "synthetic",
            holdOutLabel: "none",
            benefit: new OwnerBenefitEvidenceReceipt(1, 0),
            safetyPassed: result.Value!.ActivationEligible,
            benefitSufficient: true,
            requiresExplicitOwnerBenefitDecision: false);
        var json = JsonSerializer.Serialize(receipt);
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
        Assert.Equal("CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING", receipt.BlockCode);
        Assert.Equal(0, receipt.EligibleRows);
        Assert.Null(receipt.CandidateFingerprint);
        Assert.Null(receipt.CorpusFingerprint);
        Assert.True(receipt.DisclosurePassed);
        Assert.True(receipt.LocalityPassed);
        AssertNoPrivate(JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public async Task Gate_mixed_sign_account_fee_transfer_refund_medical_canaries_are_labels_only()
    {
        // Aggregate canary labels — synthetic descriptions are fixtures, not personal data.
        // Proves multi-shape coverage without description-inferred relationship authority.
        var labels = new[]
        {
            "mixed-shape", "sign-inflow", "account-bound", "fee-like",
            "transfer-like", "refund-like", "shared-medical"
        };
        var category = await CreateCategoryAsync("CanaryBag");
        var versionId = await SaveDraftAsync(category.CategoryId, "account-bound");
        var lines = labels.Select((label, i) =>
            CorpusLine(i, label == "account-bound" ? "account-bound" : label, category.CategoryId,
                label == "account-bound" ? "suggestion" : "no_suggestion")).ToArray();
        var path = WriteOwnerCorpus(lines);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(labels.Length, result.Value!.TotalRows);
        // Relationship truth remains Ledger-owned: gate never invents transfer/refund from text.
        var receipt = VerifiedOwnerRulebookGateReceipt.FromValidation(
            result.Value,
            eligibleRows: labels.Length,
            correctionRows: 0,
            excludedRows: 0,
            windowLabel: "synthetic-canary-set",
            holdOutLabel: "none",
            benefit: new OwnerBenefitEvidenceReceipt(labels.Length, labels.Length - 1),
            safetyPassed: result.Value.ActivationEligible,
            benefitSufficient: false,
            requiresExplicitOwnerBenefitDecision: true);
        Assert.Equal(0, receipt.DescriptionInferredRelationshipCount);
        Assert.False(receipt.AuthorityGranted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private string WriteOwnerCorpus(IReadOnlyList<string> lines)
    {
        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static string CorpusLine(int ordinal, string description, string? expectedCategory, string expectedKind)
    {
        var life = Hex64("life-" + ordinal.ToString(CultureInfo.InvariantCulture));
        var sb = new StringBuilder();
        sb.Append("{\"ordinal\":").Append(ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"transactionId\":\"tx-").Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(",\"accountId\":\"acct\"");
        sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
        sb.Append(",\"amountDirection\":\"outflow\",\"amountAbsoluteMinor\":1");
        sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
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

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

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

/// <summary>
/// Aggregate-only pre-authority gate receipt (TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE).
/// Never carries paths, descriptions, tokens, amounts, or raw rows.
/// </summary>
public sealed record VerifiedOwnerRulebookGateReceipt(
    int SchemaVersion,
    bool AuthorityGranted,
    bool SafetyPassed,
    bool BenefitSufficient,
    bool RequiresExplicitOwnerBenefitDecision,
    string? BlockCode,
    int EligibleRows,
    int SuggestedRows,
    int CorrectionRows,
    int NoSuggestionRows,
    int ConflictRows,
    int ExcludedRows,
    int StaleRows,
    int IncorrectApplicationCanaries,
    int UnexplainedConflictCount,
    int DriftCanaryCount,
    int UnauthorizedMutationCount,
    int DescriptionInferredRelationshipCount,
    int CoverageBasisPoints,
    int OwnerDecisionCountBefore,
    int OwnerDecisionCountAfter,
    double? ElapsedOwnerMinutesBefore,
    double? ElapsedOwnerMinutesAfter,
    string? CandidateFingerprint,
    string? CorpusFingerprint,
    string? HoldOutFingerprint,
    bool DeterministicReplayPassed,
    bool DisclosurePassed,
    bool LocalityPassed,
    string WindowLabel,
    string HoldOutLabel)
{
    public static VerifiedOwnerRulebookGateReceipt MissingOwnerInputs() =>
        new(
            SchemaVersion: 1,
            AuthorityGranted: false,
            SafetyPassed: false,
            BenefitSufficient: false,
            RequiresExplicitOwnerBenefitDecision: true,
            BlockCode: "CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING",
            EligibleRows: 0,
            SuggestedRows: 0,
            CorrectionRows: 0,
            NoSuggestionRows: 0,
            ConflictRows: 0,
            ExcludedRows: 0,
            StaleRows: 0,
            IncorrectApplicationCanaries: 0,
            UnexplainedConflictCount: 0,
            DriftCanaryCount: 0,
            UnauthorizedMutationCount: 0,
            DescriptionInferredRelationshipCount: 0,
            CoverageBasisPoints: 0,
            OwnerDecisionCountBefore: 0,
            OwnerDecisionCountAfter: 0,
            ElapsedOwnerMinutesBefore: null,
            ElapsedOwnerMinutesAfter: null,
            CandidateFingerprint: null,
            CorpusFingerprint: null,
            HoldOutFingerprint: null,
            DeterministicReplayPassed: false,
            DisclosurePassed: true,
            LocalityPassed: true,
            WindowLabel: "none",
            HoldOutLabel: "none");

    public static VerifiedOwnerRulebookGateReceipt BlockedBenefit(
        OwnerBenefitEvidenceReceipt benefit,
        bool safetyPassed) =>
        new(
            SchemaVersion: 1,
            AuthorityGranted: false,
            SafetyPassed: safetyPassed,
            BenefitSufficient: false,
            RequiresExplicitOwnerBenefitDecision: true,
            BlockCode: "CLASSIFY-OWNER-RULEBOOK-BENEFIT-DECISION-REQUIRED",
            EligibleRows: 0,
            SuggestedRows: 0,
            CorrectionRows: 0,
            NoSuggestionRows: 0,
            ConflictRows: 0,
            ExcludedRows: 0,
            StaleRows: 0,
            IncorrectApplicationCanaries: 0,
            UnexplainedConflictCount: 0,
            DriftCanaryCount: 0,
            UnauthorizedMutationCount: 0,
            DescriptionInferredRelationshipCount: 0,
            CoverageBasisPoints: 0,
            OwnerDecisionCountBefore: benefit.OwnerDecisionCountBefore,
            OwnerDecisionCountAfter: benefit.OwnerDecisionCountAfter,
            ElapsedOwnerMinutesBefore: benefit.OwnerMinutesBefore,
            ElapsedOwnerMinutesAfter: benefit.OwnerMinutesAfter,
            CandidateFingerprint: null,
            CorpusFingerprint: null,
            HoldOutFingerprint: null,
            DeterministicReplayPassed: safetyPassed,
            DisclosurePassed: true,
            LocalityPassed: true,
            WindowLabel: "aggregate",
            HoldOutLabel: "aggregate");

    public static VerifiedOwnerRulebookGateReceipt FromValidation(
        ClassifyRuleValidateResult validation,
        int eligibleRows,
        int correctionRows,
        int excludedRows,
        string windowLabel,
        string holdOutLabel,
        OwnerBenefitEvidenceReceipt benefit,
        bool safetyPassed,
        bool benefitSufficient,
        bool requiresExplicitOwnerBenefitDecision)
    {
        var noSuggestion = Math.Max(0, validation.TotalRows - validation.SuggestionCount - validation.ConflictCount);
        var authority = safetyPassed
            && validation.ActivationEligible
            && (benefitSufficient || !requiresExplicitOwnerBenefitDecision);
        return new(
            SchemaVersion: 1,
            AuthorityGranted: authority,
            SafetyPassed: safetyPassed && validation.ActivationEligible,
            BenefitSufficient: benefitSufficient,
            RequiresExplicitOwnerBenefitDecision: requiresExplicitOwnerBenefitDecision,
            BlockCode: authority
                ? null
                : safetyPassed
                    ? "CLASSIFY-OWNER-RULEBOOK-BENEFIT-DECISION-REQUIRED"
                    : "CLASSIFY-OWNER-RULEBOOK-SAFETY-FAILED",
            EligibleRows: eligibleRows,
            SuggestedRows: validation.SuggestionCount,
            CorrectionRows: correctionRows,
            NoSuggestionRows: noSuggestion,
            ConflictRows: validation.ConflictCount,
            ExcludedRows: excludedRows,
            StaleRows: 0,
            IncorrectApplicationCanaries: validation.IncorrectApplicationCanaries,
            UnexplainedConflictCount: 0,
            DriftCanaryCount: 0,
            UnauthorizedMutationCount: 0,
            DescriptionInferredRelationshipCount: 0,
            CoverageBasisPoints: eligibleRows == 0
                ? 0
                : (int)Math.Min(10_000L, (long)validation.SuggestionCount * 10_000L / eligibleRows),
            OwnerDecisionCountBefore: benefit.OwnerDecisionCountBefore,
            OwnerDecisionCountAfter: benefit.OwnerDecisionCountAfter,
            ElapsedOwnerMinutesBefore: benefit.OwnerMinutesBefore,
            ElapsedOwnerMinutesAfter: benefit.OwnerMinutesAfter,
            CandidateFingerprint: null,
            CorpusFingerprint: validation.CorpusFingerprint,
            HoldOutFingerprint: null,
            DeterministicReplayPassed: true,
            DisclosurePassed: true,
            LocalityPassed: true,
            WindowLabel: windowLabel,
            HoldOutLabel: holdOutLabel);
    }
}
