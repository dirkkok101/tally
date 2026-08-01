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
using Tally.Domain.Classify.Evaluation;
// TransactionCorrectionJsonContext lives next to VoidTransactionInput.
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-OUTCOME-EXPLANATION / bd-zae9
/// Complete staleness matrix: every FR-CLASSIFY-OUTCOME-INVALIDATION dimension + apply-block + rename control.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class OutcomeInvalidationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-outcome-inval-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "outcome-inval", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyEvaluationServices services = null!;
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
        services = await ClassifyEvaluationExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
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

    // ── Pure policy matrix (one case per dimension) ──────────────────────────

    [Fact]
    public void Policy_store_generation_change_is_stale()
    {
        var result = EvaluatePolicy(currentStoreGen: new string('9', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, result.PermittedNextOperationId);
    }

    [Fact]
    public void Policy_ledger_contract_version_change_is_stale()
    {
        var result = EvaluatePolicy(currentLedgerContract: "2.0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionLedgerContractVersion, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_projection_version_change_is_stale()
    {
        var result = EvaluatePolicy(currentProjection: "classification_v0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionProjectionVersion, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_category_catalogue_fingerprint_change_is_stale()
    {
        var result = EvaluatePolicy(currentCategoryLifecycle: new string('c', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionCategoryLifecycle, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_normalization_version_change_is_stale()
    {
        var result = EvaluatePolicy(currentNormalization: "normalization_v0");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionNormalizationVersion, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_rule_set_version_change_is_stale()
    {
        var result = EvaluatePolicy(currentRuleSet: "rsv-other");
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionRuleSetVersion, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_ordered_items_fingerprint_change_is_stale()
    {
        var result = EvaluatePolicy(currentOrderedItems: new string('o', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionOrderedItems, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_snapshot_expiry_is_stale()
    {
        var retained = BaseFingerprint(expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        var result = ClassificationStalenessPolicy.Evaluate(BaseInput(
            retained,
            now: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionSnapshotExpiresAt, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_item_lifecycle_change_is_stale()
    {
        var result = EvaluatePolicy(currentItemLifecycle: new string('i', 64));
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_missing_transaction_is_item_lifecycle_stale()
    {
        var result = EvaluatePolicy(transactionFound: false, currentItemLifecycle: null);
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_archived_suggested_category_is_stale()
    {
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: "archived");
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_missing_suggested_category_is_stale()
    {
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-missing",
            suggestedCategoryLifecycle: null);
        Assert.True(result.IsStale);
        Assert.Contains(ClassificationStalenessPolicy.DimensionSuggestedCategoryLifecycle, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_active_category_rename_is_not_identity_drift()
    {
        // Same active id with only display rename: lifecycle remains "active"; fingerprints unchanged.
        var result = EvaluatePolicy(
            suggestedCategoryId: "cat-1",
            suggestedCategoryLifecycle: "active");
        Assert.False(result.IsStale);
        Assert.Empty(result.ChangedDimensions);
    }

    [Fact]
    public void Policy_unavailable_current_store_generation_fails_closed()
    {
        var result = EvaluatePolicy(currentStoreGen: null);
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
    }

    [Fact]
    public void Policy_always_permits_only_re_evaluate()
    {
        var fresh = EvaluatePolicy();
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, fresh.PermittedNextOperationId);
        var stale = EvaluatePolicy(currentRuleSet: "other");
        Assert.Equal(ClassificationStalenessPolicy.NextOperationReEvaluate, stale.PermittedNextOperationId);
    }

    // ── Integration: archive blocks / re-evaluate ────────────────────────────

    [Fact]
    public async Task Archive_of_suggested_category_marks_outcome_stale()
    {
        var category = await CreateCategoryAsync("ArchiveMe");
        var versionId = await SaveDraftAsync(category.CategoryId, "archive shop");
        await ActivateWithGateAsync(versionId, category.CategoryId, "archive shop");
        var tx = await RecordAsync("archive shop");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        await ArchiveCategoryAsync(category.CategoryId);

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value!.EvaluationId,
                tx.TransactionId),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.IsStale);
        Assert.NotNull(result.Value.StaleDimensions);
        Assert.NotEmpty(result.Value.StaleDimensions!);
        // Apply is never returned — re-evaluate is the only permitted next operation.
        Assert.Equal(
            ClassificationStalenessPolicy.NextOperationReEvaluate,
            ClassifyContractMapper.PermittedNextOperationId(
                ClassificationOutcomeKind.Suggestion,
                new ClassificationStalenessPolicy.Result(
                    true,
                    result.Value.StaleDimensions!,
                    ClassificationStalenessPolicy.NextOperationReEvaluate)));
    }

    [Fact]
    public async Task Void_transaction_marks_item_lifecycle_stale()
    {
        var category = await CreateCategoryAsync("VoidMe");
        var versionId = await SaveDraftAsync(category.CategoryId, "void shop");
        await ActivateWithGateAsync(versionId, category.CategoryId, "void shop");
        var tx = await RecordAsync("void shop");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        await VoidTransactionAsync(tx.TransactionId);

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value!.EvaluationId,
                tx.TransactionId),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.IsStale);
        Assert.Contains(
            ClassificationStalenessPolicy.DimensionItemLifecycle,
            result.Value.StaleDimensions!);
    }

    [Fact]
    public async Task Stale_outcome_never_returns_apply_or_correction_operation_id()
    {
        var category = await CreateCategoryAsync("NoApply");
        var versionId = await SaveDraftAsync(category.CategoryId, "no apply shop");
        await ActivateWithGateAsync(versionId, category.CategoryId, "no apply shop");
        var tx = await RecordAsync("no apply shop");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        await ArchiveCategoryAsync(category.CategoryId);

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value!.EvaluationId,
                tx.TransactionId),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.IsStale);

        var next = ClassifyContractMapper.PermittedNextOperationId(
            ClassificationOutcomeKind.Suggestion,
            new ClassificationStalenessPolicy.Result(
                true,
                result.Value.StaleDimensions ?? Array.Empty<string>(),
                ClassificationStalenessPolicy.NextOperationReEvaluate));
        Assert.Equal(ClassifyOperationIds.Evaluate, next);
        Assert.DoesNotContain("apply", next, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correct", next, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_and_no_suggestion_remain_unappliable()
    {
        Assert.True(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.Conflict));
        Assert.True(ClassificationStalenessPolicy.IsUnappliableOutcomeKind(ClassificationOutcomeKind.NoSuggestion));
        Assert.Equal(
            ClassificationStalenessPolicy.NextOperationReEvaluate,
            ClassifyContractMapper.PermittedNextOperationId(
                ClassificationOutcomeKind.NoSuggestion,
                new ClassificationStalenessPolicy.Result(false, Array.Empty<string>(), ClassificationStalenessPolicy.NextOperationReEvaluate)));
    }

    [Fact]
    public void Policy_multiple_dimension_drifts_are_all_named()
    {
        var result = EvaluatePolicy(
            currentStoreGen: new string('1', 64),
            currentRuleSet: "rsv-x",
            currentItemLifecycle: new string('2', 64));
        Assert.True(result.IsStale);
        Assert.Contains(EvaluationFingerprint.DimensionStoreGeneration, result.ChangedDimensions);
        Assert.Contains(EvaluationFingerprint.DimensionRuleSetVersion, result.ChangedDimensions);
        Assert.Contains(ClassificationStalenessPolicy.DimensionItemLifecycle, result.ChangedDimensions);
        // Stable ordinal order of dimension names.
        Assert.Equal(
            result.ChangedDimensions.OrderBy(d => d, StringComparer.Ordinal).ToArray(),
            result.ChangedDimensions.ToArray());
    }

    [Fact]
    public void Policy_does_not_compare_snapshot_id_directly()
    {
        // Fresh store generation match + not expired → snapshot id churn is ignored.
        var result = EvaluatePolicy();
        Assert.False(result.IsStale);
        Assert.DoesNotContain(EvaluationFingerprint.DimensionSnapshotId, result.ChangedDimensions);
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
        string? suggestedCategoryLifecycle = null) =>
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
        string? suggestedCategoryLifecycle = null)
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
            suggestedCategoryLifecycle: suggestedCategoryLifecycle));
    }

    // ── Integration helpers ──────────────────────────────────────────────────

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "inval activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                "rule-" + Guid.NewGuid().ToString("N")[..12],
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
                "inval draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, ActualsContractVersions.Current, actor, CancellationToken.None);
        Assert.True(page.IsSuccess);
        var byTx = page.Value!.ClassificationItems!.ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            var item = byTx[txId];
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

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "outcome-archive"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task VoidTransactionAsync(string transactionId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "outcome-void"),
            NextKey(),
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Inval Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR"),
            NextKey(), LedgerJsonContext.Default.CreateAccountInput, LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(), LedgerJsonContext.Default.CreateCategoryInput, LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId, "-12.34", "ZAR", "2026-07-15", null, description, null, null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "inval:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId, TInput input, string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var request = new RequestEnvelope("1.0", actor, JsonSerializer.SerializeToElement(input, inputType), key);
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, json, CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
    }

    private string NextKey() => $"outcome-inval-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
