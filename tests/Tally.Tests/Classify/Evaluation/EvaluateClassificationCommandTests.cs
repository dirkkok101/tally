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
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-EVALUATION-WORKFLOW / bd-8uew
/// Command, accounting, idempotency, and no-Ledger-mutation cases for classify.evaluate.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class EvaluateClassificationCommandTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-eval-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "eval-cmd", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
    private ActivateClassificationRuleCommand activate = null!;
    private EvaluateClassificationCommand evaluate = null!;
    private ClassificationEvaluationStore evaluationStore = null!;
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
        save = services.Save;
        activate = services.Activate;
        validate = new ValidateClassificationRuleCommand(
            store,
            services.RuleStore,
            services.ValidationStore,
            ClassifyCorpusExtensions.CreateReader(),
            ledger,
            services.State.Idempotency,
            receiptStore: services.ReceiptStore);
        evaluationStore = new ClassificationEvaluationStore();
        evaluate = new EvaluateClassificationCommand(
            store,
            evaluationStore,
            new ClassificationEvaluationInputLoader(ledger),
            services.RuleSetStore,
            services.RuleStore,
            services.State.Idempotency);
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

    [Fact]
    public async Task Evaluate_requires_actor()
    {
        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            null,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Evaluate_requires_idempotency_key()
    {
        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Evaluate_rejects_unsupported_contract_version()
    {
        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest("9.9"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Evaluate_fails_closed_without_active_rule_set()
    {
        await RecordAsync("no-active");
        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0L, await evaluationStore.CountEvaluationsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Evaluate_persists_complete_outcome_accounting_for_active_rules()
    {
        var category = await CreateCategoryAsync("Groceries");
        var versionId = await SaveDraftAsync(category.CategoryId, "whole foods");
        await ActivateWithGateAsync(versionId, category.CategoryId, "whole foods");
        await RecordAsync("whole foods");
        await RecordAsync("unmatched merchant");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(
            result.Value.TotalCount,
            result.Value.SuggestionCount
            + result.Value.NoSuggestionCount
            + result.Value.ConflictCount
            + result.Value.StaleCount);
        Assert.True(result.Value.SuggestionCount >= 1);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.EvaluationId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ProjectionFingerprint));
        Assert.Equal(NormalizationDescriptor.V1.Version, result.Value.NormalizationVersion);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await evaluationStore.GetRunAsync(
            connection, null, result.Value.EvaluationId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(result.Value.TotalCount, run!.InputCount);
        var outcomes = await evaluationStore.ListOutcomesAsync(
            connection, null, result.Value.EvaluationId, CancellationToken.None);
        Assert.Equal(result.Value.TotalCount, outcomes.Count);
        Assert.Equal(new[] { 0, 1 }, outcomes.Select(o => o.Ordinal).ToArray());
        Assert.DoesNotContain(outcomes, o => o.SafeReason.Contains("whole foods", StringComparison.OrdinalIgnoreCase));
        Assert.All(outcomes, o => Assert.DoesNotContain("sourceDescription", o.SafeReason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_is_deterministic_for_identical_fingerprint()
    {
        var category = await CreateCategoryAsync("Stable");
        var versionId = await SaveDraftAsync(category.CategoryId, "stable merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "stable merchant");
        await RecordAsync("stable merchant");

        var first = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        // Fresh evaluation with a new key after identical projection membership.
        var second = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.TotalCount, second.Value!.TotalCount);
        Assert.Equal(first.Value.SuggestionCount, second.Value.SuggestionCount);
        Assert.Equal(first.Value.NoSuggestionCount, second.Value.NoSuggestionCount);
        Assert.Equal(first.Value.ConflictCount, second.Value.ConflictCount);
        Assert.Equal(first.Value.StaleCount, second.Value.StaleCount);
        // ProjectionFingerprint embeds a server-minted SnapshotId per freeze; partition accounting
        // is the stable identity of identical membership under an unchanged store generation.
        Assert.Equal(first.Value.NormalizationVersion, second.Value.NormalizationVersion);
    }

    [Fact]
    public async Task Evaluate_idempotent_replay_returns_stored_result()
    {
        var category = await CreateCategoryAsync("Idem");
        var versionId = await SaveDraftAsync(category.CategoryId, "idem merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "idem merchant");
        await RecordAsync("idem merchant");
        var key = NextKey();

        var first = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            key,
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var replay = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            key,
            CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        Assert.Equal(first.Value!.EvaluationId, replay.Value!.EvaluationId);
        Assert.Equal(first.Value.TotalCount, replay.Value.TotalCount);
        Assert.Equal(first.Value.SuggestionCount, replay.Value.SuggestionCount);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(1L, await evaluationStore.CountEvaluationsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Evaluate_idempotency_conflict_on_key_reuse_with_different_actor()
    {
        var category = await CreateCategoryAsync("Conflict");
        var versionId = await SaveDraftAsync(category.CategoryId, "conflict merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "conflict merchant");
        await RecordAsync("conflict merchant");
        var key = NextKey();

        var first = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            key,
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var other = new SafeActor("automation", "other-actor", "run-02");
        var conflict = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            other,
            key,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, conflict.ErrorCode);
    }

    [Fact]
    public async Task Evaluate_does_not_mutate_ledger_category_or_description()
    {
        var category = await CreateCategoryAsync("LedgerSafe");
        var versionId = await SaveDraftAsync(category.CategoryId, "ledger safe");
        await ActivateWithGateAsync(versionId, category.CategoryId, "ledger safe");
        var tx = await RecordAsync("ledger safe");
        var before = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(before.IsSuccess);

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var after = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(after.IsSuccess);
        Assert.Equal(before.Value!.Category.CategoryId, after.Value!.Category.CategoryId);
        Assert.Equal(before.Value.OriginalDescription, after.Value.OriginalDescription);
        Assert.Equal(before.Value.LifecycleStatus, after.Value.LifecycleStatus);
        Assert.Equal(before.Value.SignedAmount, after.Value.SignedAmount);
    }

    [Fact]
    public async Task Evaluate_conflict_when_incompatible_active_rules_match()
    {
        var catA = await CreateCategoryAsync("A");
        var catB = await CreateCategoryAsync("B");
        var vA = await SaveDraftAsync(catA.CategoryId, "clash", ruleId: "rule-a");
        var vB = await SaveDraftAsync(catB.CategoryId, "clash", ruleId: "rule-b");
        // Owner gate expects an explained conflict on the private corpus, so activation is eligible.
        await ActivateMultiWithGateAsync(
            [vA, vB],
            [("clash", "conflict", null)]);
        await RecordAsync("clash");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.ConflictCount >= 1);
        Assert.Equal(0, result.Value.SuggestionCount);
    }

    [Fact]
    public async Task Evaluate_no_suggestion_when_no_rule_matches()
    {
        var category = await CreateCategoryAsync("Nomatch");
        var versionId = await SaveDraftAsync(category.CategoryId, "expected phrase");
        await ActivateWithGateAsync(versionId, category.CategoryId, "expected phrase");
        await RecordAsync("totally different");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(result.Value!.TotalCount, result.Value.NoSuggestionCount);
        Assert.Equal(0, result.Value.SuggestionCount);
    }

    [Fact]
    public async Task Evaluate_does_not_suggest_an_archived_target_when_no_active_categories_remain()
    {
        var category = await CreateCategoryAsync("ArchivedTarget");
        var versionId = await SaveDraftAsync(category.CategoryId, "archived target");
        await ActivateWithGateAsync(versionId, category.CategoryId, "archived target");
        await ArchiveCategoryAsync(category.CategoryId);
        await RecordAsync("archived target");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(result.Value!.TotalCount, result.Value.NoSuggestionCount);
        Assert.Equal(0, result.Value.SuggestionCount);
    }

    [Fact]
    public async Task Evaluate_suggestion_names_compatible_same_category_aggregation()
    {
        var category = await CreateCategoryAsync("SameCat");
        var v1 = await SaveDraftAsync(category.CategoryId, "shared", ruleId: "rule-s1");
        // Second rule also proposes the same category for a broader starts_with style via equals on same token.
        var v2 = await SaveDraftAsync(category.CategoryId, "shared", ruleId: "rule-s2");
        await ActivateMultiWithGateAsync(
            [v1, v2],
            [("shared", "suggestion", category.CategoryId)]);
        await RecordAsync("shared");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.SuggestionCount >= 1);
        Assert.Equal(0, result.Value.ConflictCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var granted = await ValidateAndGrantAsync(
            [versionId],
            [(description, "suggestion", categoryId)]);
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                granted.ValidationId,
                granted.ReceiptId,
                BroadApplyAllowed: false,
                Reason: "eval activate"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
        // Gate corpus transactions must leave the evaluation universe (uncategorized only)
        // so later RecordAsync membership is isolated from activation evidence.
        await CategorizeGateCorpusAsync(granted.GateTransactionIds, categoryId);
    }

    private async Task ActivateMultiWithGateAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var granted = await ValidateAndGrantAsync(versionIds, rows);
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                granted.ValidationId,
                granted.ReceiptId,
                BroadApplyAllowed: false,
                Reason: "eval multi activate"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
        var firstCategory = rows[0].ExpectedCategory;
        if (!string.IsNullOrWhiteSpace(firstCategory))
        {
            await CategorizeGateCorpusAsync(granted.GateTransactionIds, firstCategory!);
        }
    }

    private async Task<(string ValidationId, string ReceiptId, IReadOnlyList<string> GateTransactionIds)> ValidateAndGrantAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var (path, txIds) = await WriteBoundCorpusAsync(rows);
        var rep = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion,
                versionIds,
                path,
                rep.Value!.ValidationId,
                replay.Value!.ValidationId,
                OwnerDecisionCountBefore: 10,
                OwnerDecisionCountAfter: 2,
                ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(hold.Value!.OwnerRulebookGateReceiptId));
        return (rep.Value.ValidationId, hold.Value.OwnerRulebookGateReceiptId!, txIds);
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
                "eval draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task CategorizeGateCorpusAsync(IReadOnlyList<string> transactionIds, string categoryId)
    {
        foreach (var txId in transactionIds)
        {
            _ = await ExecuteSuccessAsync(
                "ledger.transaction.category.assign",
                new AssignCategoryInput(txId, categoryId, "remove gate evidence from evaluation universe"),
                NextKey(),
                LedgerJsonContext.Default.AssignCategoryInput,
                LedgerJsonContext.Default.CategoryAllocationResult);
        }
    }

    private async Task<(string Path, IReadOnlyList<string> TransactionIds)> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
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
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                rows[i].ExpectedKind, rows[i].ExpectedCategory));
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (path, created.Select(c => c.TxId).ToArray());
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

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "EvalCmd Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(), "ZAR"),
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
            new ArchiveCategoryInput(categoryId, "evaluation target archived"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> RecordAsync(string description, string amount = "-12.34")
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
                    "eval-cmd:" + Guid.NewGuid().ToString("N")[..8],
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

    private string NextKey() => $"eval-cmd-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
