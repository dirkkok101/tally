using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// UC-CLASSIFY-002 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-002 / bd-3cp2
/// VerifiedClassifyUc002 — published-boundary acceptance matrix.
///
/// Invokes TallyProcess for CLASSIFY outcome.get / status / evaluate / rule lifecycle
/// (never private command handlers for the UC assertions). Proves bounded
/// suggestion/no-suggestion/conflict/stale provenance, distinct unknown vs
/// evidence-unavailable, every specified fingerprint/staleness dimension naming
/// apply-block, same-ID active rename as non-stale display control, privacy, and
/// that explanation/status reads mutate neither CLASSIFY nor Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc002OutcomeTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc002-{Guid.NewGuid():N}");
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

    // ── Bounded explanation partitions (published process) ───────────────────

    [Fact]
    public async Task UC002_suggestion_exposes_bounded_provenance_and_null_next_op()
    {
        var category = await CreateCategoryAsync("Uc002Suggest");
        var ruleVersionId = await SaveAndActivateAsync(category, "uc002 whole foods");
        var tx = await RecordTransactionAsync("uc002 whole foods");
        var evalId = await EvaluateSuccessAsync();

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");

        Assert.Equal("suggestion", body.GetProperty("kind").GetString());
        Assert.Equal(evalId, body.GetProperty("evaluationId").GetString());
        Assert.Equal(tx, body.GetProperty("transactionId").GetString());
        Assert.Equal(category, body.GetProperty("suggestedCategoryId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("suggestedCategoryDisplayName").GetString()));
        Assert.Equal(NormalizationDescriptor.V1.Version, body.GetProperty("normalizationVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("ruleSetVersionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("safeReason").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("outcomeId").GetString()));
        Assert.True(body.GetProperty("ordinal").GetInt32() >= 0);
        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("permittedNextOperationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("staleDimensions").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("conflictProposals").ValueKind);

        var rules = body.GetProperty("contributingRuleVersionIds").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.NotEmpty(rules);
        Assert.Contains(ruleVersionId, rules);
        Assert.Equal(rules.OrderBy(r => r, StringComparer.Ordinal).ToArray(), rules);

        var fields = body.GetProperty("matchedFieldKeys").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.NotEmpty(fields);
        Assert.Contains("description.normalized", fields);
        Assert.Equal(fields.OrderBy(f => f, StringComparer.Ordinal).ToArray(), fields);
        Assert.DoesNotContain(fields, f => f.Contains("whole foods", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UC002_no_suggestion_exposes_versions_without_invented_category()
    {
        var category = await CreateCategoryAsync("Uc002Nomatch");
        await SaveAndActivateAsync(category, "uc002 expected phrase");
        var unmatched = await RecordTransactionAsync("uc002 totally different");
        var evalId = await EvaluateSuccessAsync();

        var outcome = await OutcomeGetAsync(evalId, unmatched);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");

        Assert.Equal("no_suggestion", body.GetProperty("kind").GetString());
        Assert.Equal(evalId, body.GetProperty("evaluationId").GetString());
        Assert.Equal(unmatched, body.GetProperty("transactionId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("suggestedCategoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("suggestedCategoryDisplayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("contributingRuleVersionIds").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("matchedFieldKeys").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("conflictProposals").ValueKind);
        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        Assert.Equal(NormalizationDescriptor.V1.Version, body.GetProperty("normalizationVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("ruleSetVersionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("safeReason").GetString()));
    }

    [Fact]
    public async Task UC002_conflict_lists_incompatible_rules_without_winner()
    {
        var catA = await CreateCategoryAsync("Uc002A");
        var catB = await CreateCategoryAsync("Uc002B");
        var vA = await SaveRuleAsync(catA, "uc002 clash", "rule-uc002-a");
        var vB = await SaveRuleAsync(catB, "uc002 clash", "rule-uc002-b");
        await ActivateRulesAsync([vA, vB], [("uc002 clash", "conflict", null)]);
        var tx = await RecordTransactionAsync("uc002 clash");
        var evalId = await EvaluateSuccessAsync();

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");

        Assert.Equal("conflict", body.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("suggestedCategoryId").ValueKind);
        var rules = body.GetProperty("contributingRuleVersionIds").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.True(rules.Length >= 2);
        Assert.Contains(vA, rules);
        Assert.Contains(vB, rules);
        Assert.Equal(rules.OrderBy(r => r, StringComparer.Ordinal).ToArray(), rules);

        var proposals = body.GetProperty("conflictProposals").EnumerateArray().ToArray();
        Assert.Equal(rules.Length, proposals.Length);
        var proposalRuleIds = proposals.Select(p => p.GetProperty("ruleVersionId").GetString()!).ToArray();
        Assert.Equal(proposalRuleIds.OrderBy(r => r, StringComparer.Ordinal).ToArray(), proposalRuleIds);
        Assert.All(proposals, p =>
        {
            var cat = p.GetProperty("proposedCategoryId").GetString()!;
            Assert.Contains(cat, new[] { catA, catB });
        });
        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
    }

    [Fact]
    public async Task UC002_stale_void_names_item_lifecycle_and_requires_re_evaluate()
    {
        var category = await CreateCategoryAsync("Uc002Void");
        await SaveAndActivateAsync(category, "uc002 void shop");
        var tx = await RecordTransactionAsync("uc002 void shop");
        var evalId = await EvaluateSuccessAsync();

        await VoidTransactionAsync(tx);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, dims);
        Assert.Equal(dims.OrderBy(d => d, StringComparer.Ordinal).ToArray(), dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        AssertBlocksApply(body.GetProperty("permittedNextOperationId").GetString()!);
    }

    [Fact]
    public async Task UC002_stale_allocation_change_names_item_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002Alloc");
        var other = await CreateCategoryAsync("Uc002Other");
        await SaveAndActivateAsync(category, "uc002 alloc shop");
        var tx = await RecordTransactionAsync("uc002 alloc shop");
        var evalId = await EvaluateSuccessAsync();

        // Assignment after evaluation mutates allocation revision → item lifecycle drift.
        await AssignCategoryAsync(tx, other, "uc002 post-eval assign");

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        AssertBlocksApply(body.GetProperty("permittedNextOperationId").GetString()!);
    }

    [Fact]
    public async Task UC002_stale_supersede_names_item_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002Supersede");
        await SaveAndActivateAsync(category, "uc002 supersede shop");
        var tx = await RecordTransactionAsync("uc002 supersede shop");
        var evalId = await EvaluateSuccessAsync();

        await SupersedeTransactionAsync(tx, "uc002 supersede shop replacement");

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
    }

    [Fact]
    public async Task UC002_stale_transfer_relationship_change_names_item_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002Transfer");
        await SaveAndActivateAsync(category, "uc002 transfer outflow");
        var otherAccount = await CreateAccountAsync();
        var outflow = await RecordTransactionAsync("uc002 transfer outflow", "-12.34");
        var inflow = await RecordTransactionAsync("uc002 transfer inflow", "12.34", otherAccount);
        var evalId = await EvaluateSuccessAsync();

        await ConfirmTransferAsync(outflow, inflow);

        var outcome = await OutcomeGetAsync(evalId, outflow);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        AssertBlocksApply(body.GetProperty("permittedNextOperationId").GetString()!);
    }

    [Fact]
    public async Task UC002_stale_refund_relationship_change_names_item_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002Refund");
        await SaveAndActivateAsync(category, "uc002 refund purchase");
        var original = await RecordTransactionAsync("uc002 refund purchase", "-12.34");
        var refund = await RecordTransactionAsync("uc002 refund credit", "12.34");
        var evalId = await EvaluateSuccessAsync();

        await ConfirmRefundAsync(original, refund);

        var outcome = await OutcomeGetAsync(evalId, original);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        AssertBlocksApply(body.GetProperty("permittedNextOperationId").GetString()!);
    }

    [Fact]
    public async Task UC002_stale_category_archive_names_suggested_category_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002Archive");
        await SaveAndActivateAsync(category, "uc002 archive shop");
        var tx = await RecordTransactionAsync("uc002 archive shop");
        var evalId = await EvaluateSuccessAsync();

        await ArchiveCategoryAsync(category);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        AssertBlocksApply(body.GetProperty("permittedNextOperationId").GetString()!);
    }

    [Fact]
    public async Task UC002_stale_category_reactivation_names_suggested_category_lifecycle()
    {
        var category = await CreateCategoryAsync("Uc002React");
        await SaveAndActivateAsync(category, "uc002 react shop");
        var tx = await RecordTransactionAsync("uc002 react shop");
        var evalId = await EvaluateSuccessAsync();

        await ArchiveCategoryAsync(category);
        await ReactivateCategoryAsync(category);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, dims);
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
    }

    [Fact]
    public async Task UC002_same_id_active_rename_is_non_stale_display_only_control()
    {
        var category = await CreateCategoryAsync("Uc002Rename");
        await SaveAndActivateAsync(category, "uc002 rename shop");
        var tx = await RecordTransactionAsync("uc002 rename shop");
        var evalId = await EvaluateSuccessAsync();

        const string newName = "Uc002 Renamed Display";
        await RenameCategoryAsync(category, newName);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(outcome);
        var body = doc.RootElement.GetProperty("result_or_error");

        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("staleDimensions").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("permittedNextOperationId").ValueKind);
        Assert.Equal(category, body.GetProperty("suggestedCategoryId").GetString());
        Assert.Equal(newName, body.GetProperty("suggestedCategoryDisplayName").GetString());
        Assert.Equal("suggestion", body.GetProperty("kind").GetString());
    }

    // ── Unknown vs evidence-unavailable ──────────────────────────────────────

    [Fact]
    public async Task UC002_unknown_evaluation_is_not_found_not_evidence_unavailable()
    {
        var result = await OutcomeGetAsync("missing-eval-uc002", "tx-missing");
        AssertClassifyError(result, ClassifyErrors.EvaluationNotFound);
        Assert.NotEqual(ClassifyContractMapper.EvidenceUnavailable, ClassifyErrors.EvaluationNotFound);
    }

    [Fact]
    public async Task UC002_unknown_outcome_is_not_found_distinct_from_evidence_unavailable()
    {
        var category = await CreateCategoryAsync("Uc002UnknownOut");
        await SaveAndActivateAsync(category, "uc002 known shop");
        await RecordTransactionAsync("uc002 known shop");
        var evalId = await EvaluateSuccessAsync();

        var missing = await OutcomeGetAsync(evalId, "ghost-tx-uc002");
        AssertClassifyError(missing, ClassifyErrors.OutcomeNotFound);
        Assert.NotEqual(ClassifyContractMapper.EvidenceUnavailable, ClassifyErrors.OutcomeNotFound);
    }

    [Fact]
    public async Task UC002_evidence_unavailable_is_distinct_from_not_found()
    {
        var category = await CreateCategoryAsync("Uc002EvUnav");
        await SaveAndActivateAsync(category, "uc002 ev merchant");
        var tx = await RecordTransactionAsync("uc002 ev merchant");
        var evalId = await EvaluateSuccessAsync();

        // Simulate durable evidence loss without reconstructing from current state.
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = ClassifyDatabasePath(),
                Mode = SqliteOpenMode.ReadWrite
            }.ToString()))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM match_evidence;";
            var deleted = await cmd.ExecuteNonQueryAsync();
            Assert.True(deleted >= 1);
        }

        var unavailable = await OutcomeGetAsync(evalId, tx);
        AssertClassifyError(unavailable, ClassifyContractMapper.EvidenceUnavailable);
        Assert.NotEqual(ClassifyErrors.OutcomeNotFound, ClassifyContractMapper.EvidenceUnavailable);
        Assert.NotEqual(ClassifyErrors.EvaluationNotFound, ClassifyContractMapper.EvidenceUnavailable);

        var unknown = await OutcomeGetAsync(evalId, "still-ghost");
        AssertClassifyError(unknown, ClassifyErrors.OutcomeNotFound);
    }

    // ── Pure policy: every fingerprint / staleness dimension names itself ────

    [Fact]
    public void UC002_policy_store_generation_change_names_dimension_and_blocks_apply()
    {
        var result = EvaluatePolicy(currentStoreGen: new string('9', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, result.PermittedNextOperationId);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_ledger_contract_version_change_names_dimension()
    {
        var result = EvaluatePolicy(currentLedgerContract: "2.0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionLedgerContractVersion, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_projection_version_change_names_dimension()
    {
        var result = EvaluatePolicy(currentProjection: "classification_v0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionProjectionVersion, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_category_catalogue_fingerprint_change_names_dimension()
    {
        var result = EvaluatePolicy(currentCategoryLifecycle: new string('c', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionCategoryLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_normalization_version_change_names_dimension()
    {
        var result = EvaluatePolicy(currentNormalization: "normalization_v0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionNormalizationVersion, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_rule_set_version_change_names_dimension()
    {
        var result = EvaluatePolicy(currentRuleSet: "rsv-other");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionRuleSetVersion, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_ordered_items_fingerprint_change_names_dimension()
    {
        var result = EvaluatePolicy(currentOrderedItems: new string('e', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionOrderedItems, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_snapshot_expiry_names_dimension()
    {
        var retained = BaseFingerprint(expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        var result = ClassificationStalenessPolicy.Evaluate(BaseInput(
            retained,
            now: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionSnapshotExpiresAt, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_item_lifecycle_change_names_dimension()
    {
        var result = EvaluatePolicy(currentItemLifecycle: new string('f', 64));
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_missing_transaction_is_item_lifecycle_stale()
    {
        var result = EvaluatePolicy(transactionFound: false, currentItemLifecycle: null);
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_archived_suggested_category_names_dimension()
    {
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: "archived");
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_missing_suggested_category_names_dimension()
    {
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: null);
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_reactivation_after_evaluation_names_dimension()
    {
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: "active",
            reactivatedAfterEvaluation: true);
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_active_rename_is_not_identity_drift()
    {
        // Same active category id with store-generation-only churn remains fresh.
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: "active",
            currentStoreGen: new string('9', 64));
        Assert.False(result.IsStale);
        Assert.Empty(result.ChangedDimensions);
    }

    [Fact]
    public void UC002_policy_multiple_dimension_drifts_are_all_named_in_order()
    {
        var result = EvaluatePolicy(
            currentStoreGen: new string('1', 64),
            currentRuleSet: "rsv-x",
            currentItemLifecycle: new string('2', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
        Assert.Contains(EvaluationFingerprint.DimensionRuleSetVersion, result.ChangedDimensions);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
        Assert.Equal(
            result.ChangedDimensions.OrderBy(d => d, StringComparer.Ordinal).ToArray(),
            result.ChangedDimensions.ToArray());
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    [Fact]
    public void UC002_policy_always_permits_only_re_evaluate_never_apply_or_correct()
    {
        var fresh = EvaluatePolicy();
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, fresh.PermittedNextOperationId);
        var stale = EvaluatePolicy(currentRuleSet: "other");
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, stale.PermittedNextOperationId);
        AssertBlocksApply(stale.PermittedNextOperationId);

        Assert.True(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.NoSuggestion));
        Assert.True(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.Conflict));
        Assert.True(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.Stale));
        Assert.False(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.Suggestion));
        Assert.Equal(
            ClassifyOperationIds.Evaluate,
            ClassifyContractMapper.ResolvePermittedNextOperationId(ClassificationOutcomeKind.Conflict, isStale: false));
        Assert.Null(
            ClassifyContractMapper.ResolvePermittedNextOperationId(ClassificationOutcomeKind.Suggestion, isStale: false));
        Assert.Equal(
            ClassifyOperationIds.Evaluate,
            ClassifyContractMapper.ResolvePermittedNextOperationId(ClassificationOutcomeKind.Suggestion, isStale: true));
    }

    [Fact]
    public void UC002_policy_unavailable_store_generation_fails_closed()
    {
        var result = EvaluatePolicy(currentStoreGen: null);
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
        AssertBlocksApply(result.PermittedNextOperationId);
    }

    // ── Privacy + no-mutation ────────────────────────────────────────────────

    [Fact]
    public async Task UC002_explanation_privacy_excludes_raw_description_and_hashes()
    {
        const string canary = "CANARY_UC002_PRIVATE_DESC_xyz";
        var category = await CreateCategoryAsync("Uc002Priv");
        await SaveAndActivateAsync(category, canary);
        var tx = await RecordTransactionAsync(canary);
        var evalId = await EvaluateSuccessAsync();

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        Assert.DoesNotContain(canary, outcome.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(canary, outcome.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedValueHash", outcome.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", outcome.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", outcome.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("predicateKind", outcome.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UC002_outcome_get_and_status_mutate_neither_classify_nor_ledger()
    {
        var category = await CreateCategoryAsync("Uc002Nomut");
        await SaveAndActivateAsync(category, "uc002 nomut shop");
        var tx = await RecordTransactionAsync("uc002 nomut shop");
        var evalId = await EvaluateSuccessAsync();

        var ledgerBefore = await LedgerFingerprintAsync();
        var classifyBefore = await ClassifyFingerprintAsync();

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);

        var status = await StatusAsync(ClassifyStatusSubjectType.Evaluation, evalId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var statusDoc = ParseResult(status);
        var statusBody = statusDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal("evaluation", statusBody.GetProperty("subjectType").GetString());
        Assert.Equal(evalId, statusBody.GetProperty("subjectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(statusBody.GetProperty("lifecycleState").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(statusBody.GetProperty("nextSafeOperationId").GetString()));
        Assert.True(statusBody.TryGetProperty("evaluation", out var evalDetail));
        Assert.False(string.IsNullOrWhiteSpace(evalDetail.GetProperty("evaluationFingerprint").GetString()));
        Assert.Equal(
            evalDetail.GetProperty("inputCount").GetInt32(),
            evalDetail.GetProperty("suggestionCount").GetInt32()
            + evalDetail.GetProperty("noSuggestionCount").GetInt32()
            + evalDetail.GetProperty("conflictCount").GetInt32()
            + evalDetail.GetProperty("staleCount").GetInt32());

        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
        Assert.Equal(classifyBefore, await ClassifyFingerprintAsync());
    }

    [Fact]
    public async Task UC002_unknown_status_subject_is_not_found_without_payload_search()
    {
        const string canary = "CANARY_STATUS_SUBJECT_PRIVATE";
        var result = await StatusAsync(ClassifyStatusSubjectType.Evaluation, canary);
        AssertClassifyError(result, ClassifyErrors.NotFound);
        Assert.DoesNotContain(canary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ── Policy helpers ───────────────────────────────────────────────────────

    private static EvaluationFingerprint BaseFingerprint(
        DateTimeOffset? expiresAt = null,
        string ruleSet = "rsv-1",
        string? storeGen = null)
    {
        var exp = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(2))
            .ToString("O", CultureInfo.InvariantCulture);
        return EvaluationFingerprint.Create(
            ActualsContractVersions.Current,
            ClassificationProjectionVersions.ClassificationV1,
            storeGen ?? new string('a', 64),
            "snap-retained",
            exp,
            new string('b', 64),
            NormalizationDescriptor.V1.Version,
            ruleSet,
            new string('c', 64));
    }

    private static ClassificationStalenessPolicy.Input BaseInput(
        EvaluationFingerprint retained,
        DateTimeOffset? now = null,
        DateTimeOffset? expiresAt = null,
        string? currentStoreGen = null,
        string? currentLedgerContract = null,
        string? currentProjection = null,
        string? currentCategoryLifecycle = null,
        string? currentNormalization = null,
        string? currentRuleSet = null,
        string? currentOrderedItems = null,
        string? currentItemLifecycle = null,
        bool transactionFound = true,
        string? suggestedCategoryId = null,
        string? suggestedCategoryLifecycle = null,
        bool reactivatedAfterEvaluation = false) =>
        new(
            RetainedEvaluation: retained,
            RetainedItemLifecycleFingerprint: new string('d', 64),
            SuggestedCategoryId: suggestedCategoryId,
            CurrentStoreGenerationFingerprint: currentStoreGen ?? retained.StoreGenerationFingerprint,
            CurrentLedgerContractVersion: currentLedgerContract ?? retained.LedgerContractVersion,
            CurrentProjectionVersion: currentProjection ?? retained.ProjectionVersion,
            CurrentCategoryLifecycleFingerprint: currentCategoryLifecycle ?? retained.CategoryLifecycleFingerprint,
            CurrentNormalizationVersion: currentNormalization ?? retained.NormalizationVersion,
            CurrentRuleSetVersionId: currentRuleSet ?? retained.RuleSetVersionId,
            CurrentOrderedItemsFingerprint: currentOrderedItems ?? retained.OrderedItemsFingerprint,
            CurrentItemLifecycleFingerprint: currentItemLifecycle ?? new string('d', 64),
            TransactionFoundInLedger: transactionFound,
            SuggestedCategoryLifecycleState: suggestedCategoryLifecycle,
            SuggestedCategoryReactivatedAfterEvaluation: reactivatedAfterEvaluation,
            NowUtc: now ?? DateTimeOffset.UtcNow,
            RetainedSnapshotExpiresAt: expiresAt
                ?? DateTimeOffset.Parse(retained.SnapshotExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static ClassificationStalenessPolicy.Result EvaluatePolicy(
        string? currentStoreGen = null,
        string? currentLedgerContract = null,
        string? currentProjection = null,
        string? currentCategoryLifecycle = null,
        string? currentNormalization = null,
        string? currentRuleSet = null,
        string? currentOrderedItems = null,
        string? currentItemLifecycle = null,
        bool transactionFound = true,
        string? suggestedCategoryId = null,
        string? suggestedCategoryLifecycle = null,
        bool reactivatedAfterEvaluation = false)
    {
        var retained = BaseFingerprint();
        return ClassificationStalenessPolicy.Evaluate(BaseInput(
            retained,
            currentStoreGen: currentStoreGen,
            currentLedgerContract: currentLedgerContract,
            currentProjection: currentProjection,
            currentCategoryLifecycle: currentCategoryLifecycle,
            currentNormalization: currentNormalization,
            currentRuleSet: currentRuleSet,
            currentOrderedItems: currentOrderedItems,
            currentItemLifecycle: currentItemLifecycle,
            transactionFound: transactionFound,
            suggestedCategoryId: suggestedCategoryId,
            suggestedCategoryLifecycle: suggestedCategoryLifecycle,
            reactivatedAfterEvaluation: reactivatedAfterEvaluation));
    }

    private static void AssertBlocksApply(string nextOperationId)
    {
        Assert.Equal(ClassifyOperationIds.Evaluate, nextOperationId);
        Assert.DoesNotContain("apply", nextOperationId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correct", nextOperationId, StringComparison.OrdinalIgnoreCase);
    }

    // ── Published process helpers ────────────────────────────────────────────

    private async Task<string> SaveAndActivateAsync(string categoryId, string description)
    {
        var versionId = await SaveRuleAsync(categoryId, description);
        await ActivateRulesAsync([versionId], [(description, "suggestion", categoryId)]);
        return versionId;
    }

    private async Task ActivateRulesAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";

        var rep = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var validationId = repDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

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
            .GetProperty("ownerRulebookGateReceiptId").GetString()!;

        var activated = await process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":false,"reason":"uc002 activate"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
    }

    private async Task<string> SaveRuleAsync(string categoryId, string description, string? ruleId = null)
    {
        var id = ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc002 draft"}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
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
            new SafeActor("automation", "classify-uc002", "run-01"),
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
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                rows[i].ExpectedKind, rows[i].ExpectedCategory));
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

    private Task<ProcessResult> OutcomeGetAsync(string evaluationId, string transactionId) =>
        process.RunAsync(
            ["classify", "outcome", "get", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","evaluationId":{{JsonSerializer.Serialize(evaluationId)}},"transactionId":{{JsonSerializer.Serialize(transactionId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    private Task<ProcessResult> StatusAsync(ClassifyStatusSubjectType subjectType, string subjectId)
    {
        var typeWire = subjectType switch
        {
            ClassifyStatusSubjectType.Evaluation => "evaluation",
            ClassifyStatusSubjectType.Rule => "rule",
            ClassifyStatusSubjectType.Validation => "validation",
            ClassifyStatusSubjectType.Preview => "preview",
            ClassifyStatusSubjectType.Apply => "apply",
            ClassifyStatusSubjectType.Feedback => "feedback",
            ClassifyStatusSubjectType.Abandonment => "abandonment",
            ClassifyStatusSubjectType.Cleanup => "cleanup",
            _ => throw new ArgumentOutOfRangeException(nameof(subjectType))
        };
        return process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":{{JsonSerializer.Serialize(typeWire)}},"subjectId":{{JsonSerializer.Serialize(subjectId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);
    }

    private async Task<string> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var result = await process.RunAsync(
            ["ledger", "account", "create", "--input", "-"],
            LedgerEnvelope(
                $$"""{"institutionName":"Uc002 Bank {{unique}}","displayName":"Primary-{{unique}}","accountType":"cheque","maskedIdentifier":"****{{unique[..4]}}","currencyCode":"ZAR"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("accountId").GetString()!;
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
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":"uc002-archive"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task ReactivateCategoryAsync(string categoryId)
    {
        var result = await process.RunAsync(
            ["ledger", "category", "reactivate", "--input", "-"],
            LedgerEnvelope(
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":"uc002-reactivate"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task RenameCategoryAsync(string categoryId, string newName)
    {
        var result = await process.RunAsync(
            ["ledger", "category", "rename", "--input", "-"],
            LedgerEnvelope(
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"newName":{{JsonSerializer.Serialize(newName)}},"reason":"uc002-rename"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task VoidTransactionAsync(string transactionId)
    {
        var result = await process.RunAsync(
            ["ledger", "transaction", "void", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"reason":"uc002-void"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task SupersedeTransactionAsync(string transactionId, string replacementDescription)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var replacement = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(accountId)}},
              "signedAmount":"-12.34",
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(replacementDescription)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc002-sup:" + Guid.NewGuid().ToString("N")[..8])}}
              }
            }
            """;
        var result = await process.RunAsync(
            ["ledger", "transaction", "supersede", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"replacement":{{replacement}},"reason":"uc002-supersede"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task AssignCategoryAsync(string transactionId, string categoryId, string reason)
    {
        var result = await process.RunAsync(
            ["ledger", "transaction", "category", "assign", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":{{JsonSerializer.Serialize(reason)}}}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task ConfirmTransferAsync(string outflowTransactionId, string inflowTransactionId)
    {
        var result = await process.RunAsync(
            ["ledger", "transfer", "confirm", "--input", "-"],
            LedgerEnvelope(
                $$"""{"outflowTransactionId":{{JsonSerializer.Serialize(outflowTransactionId)}},"inflowTransactionId":{{JsonSerializer.Serialize(inflowTransactionId)}},"reason":"uc002-transfer"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task ConfirmRefundAsync(string originalTransactionId, string refundTransactionId)
    {
        var result = await process.RunAsync(
            ["ledger", "refund", "confirm", "--input", "-"],
            LedgerEnvelope(
                $$"""{"originalTransactionId":{{JsonSerializer.Serialize(originalTransactionId)}},"refundTransactionId":{{JsonSerializer.Serialize(refundTransactionId)}},"reason":"uc002-refund"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task<string> RecordTransactionAsync(
        string description,
        string amount = "-12.34",
        string? targetAccountId = null)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(targetAccountId ?? accountId)}},
              "signedAmount":{{JsonSerializer.Serialize(amount)}},
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(description)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc002:" + Guid.NewGuid().ToString("N")[..8])}}
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
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc002","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc002","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc002","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc002-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];

    private string LedgerDatabasePath()
    {
        var current = File.ReadAllText(Path.Combine(root, "CURRENT")).Trim();
        return Path.Combine(root, "generations", current, "ledger.db");
    }

    private string ClassifyDatabasePath() => Path.Combine(root, "classify", "classify.db");

    private async Task<string> LedgerFingerprintAsync()
    {
        var path = LedgerDatabasePath();
        if (!File.Exists(path))
        {
            return "absent";
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        var builder = new StringBuilder();
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM spend_category;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM catalogue_lifecycle_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM category_parent_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM account;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM transaction_fact;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM transaction_lifecycle_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM category_allocation_event;");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT category_id || '|' || COALESCE(
                    (SELECT action FROM catalogue_lifecycle_event e
                     WHERE e.catalogue_kind = 'category' AND e.entity_id = spend_category.category_id
                     ORDER BY occurred_at DESC, lifecycle_event_id DESC LIMIT 1), '')
                FROM spend_category
                ORDER BY category_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.Append(reader.GetString(0)).Append(';');
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT transaction_id || '|' || COALESCE(original_description,'') || '|' || COALESCE(
                    (SELECT action FROM transaction_lifecycle_event e
                     WHERE e.transaction_id = transaction_fact.transaction_id
                     ORDER BY occurred_at DESC, lifecycle_event_id DESC LIMIT 1), '')
                FROM transaction_fact
                ORDER BY transaction_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.Append(reader.GetString(0)).Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private async Task<string> ClassifyFingerprintAsync()
    {
        var path = ClassifyDatabasePath();
        if (!File.Exists(path))
        {
            return "absent";
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        var builder = new StringBuilder();
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM evaluation_run;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM classification_outcome;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM match_evidence;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM operation_idempotency;");
        await AppendScalarAsync(
            connection,
            builder,
            """
            SELECT COALESCE(GROUP_CONCAT(evaluation_id || '|' || COALESCE(lifecycle_state,'') || '|' ||
              CAST(input_count AS TEXT) || '|' || CAST(suggestion_count AS TEXT), ';'), '')
            FROM (SELECT evaluation_id, lifecycle_state, input_count, suggestion_count
                  FROM evaluation_run ORDER BY evaluation_id);
            """);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task AppendScalarAsync(SqliteConnection connection, StringBuilder builder, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        builder.Append(value?.ToString() ?? "null").Append('#');
    }
}
