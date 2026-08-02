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
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
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
/// Atomicity, rollback, fingerprint, and stored-result cases.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class EvaluationPersistenceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-eval-persist-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "eval-persist", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
    private ActivateClassificationRuleCommand activate = null!;
    private EvaluateClassificationCommand evaluate = null!;
    private ClassificationEvaluationStore evaluationStore = null!;
    private RuleSetStore ruleSetStore = null!;
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
        ruleSetStore = services.RuleSetStore;
        save = services.Save;
        activate = services.Activate;
        validate = new ValidateClassificationRuleCommand(
            store, services.RuleStore, services.ValidationStore,
            ClassifyCorpusExtensions.CreateReader(), ledger, services.State.Idempotency,
            receiptStore: services.ReceiptStore);
        evaluationStore = new ClassificationEvaluationStore();
        evaluate = new EvaluateClassificationCommand(
            store, evaluationStore, new ClassificationEvaluationInputLoader(ledger),
            services.RuleSetStore, services.RuleStore, services.State.Idempotency);
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
    public async Task Persist_completed_writes_run_outcomes_and_evidence_atomically()
    {
        var category = await CreateCategoryAsync("Persist");
        var versionId = await SaveDraftAsync(category.CategoryId, "persist me");
        await ActivateWithGateAsync(versionId, category.CategoryId, "persist me");
        await RecordAsync("persist me");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await evaluationStore.GetRunAsync(connection, null, result.Value!.EvaluationId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(ClassifyContractMapper.EvaluationLifecycleCompleted, run!.LifecycleState);
        Assert.Equal(result.Value.TotalCount, run.InputCount);
        Assert.Equal(
            run.InputCount,
            run.SuggestionCount + run.NoSuggestionCount + run.ConflictCount + run.StaleCount);

        var outcomes = await evaluationStore.ListOutcomesAsync(
            connection, null, result.Value.EvaluationId, CancellationToken.None);
        Assert.Equal(run.InputCount, outcomes.Count);

        var evidenceTotal = 0L;
        foreach (var outcome in outcomes.Where(o => o.OutcomeType == "suggestion"))
        {
            var evidence = await evaluationStore.ListEvidenceForOutcomeAsync(
                connection, null, outcome.OutcomeId, CancellationToken.None);
            evidenceTotal += evidence.Count;
            Assert.All(evidence, e =>
            {
                Assert.Equal(64, e.NormalizedValueHash.Length);
                Assert.DoesNotContain("persist me", e.FieldKey, StringComparison.OrdinalIgnoreCase);
            });
        }

        Assert.True(evidenceTotal >= 1);
        Assert.Equal(evidenceTotal, await evaluationStore.CountEvidenceAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Persist_never_stores_raw_source_descriptions()
    {
        var category = await CreateCategoryAsync("NoDesc");
        var versionId = await SaveDraftAsync(category.CategoryId, "secret-description-xyz");
        await ActivateWithGateAsync(versionId, category.CategoryId, "secret-description-xyz");
        await RecordAsync("secret-description-xyz");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT evaluation_id, lifecycle_state, actor FROM evaluation_run
            UNION ALL
            SELECT outcome_id, outcome_type, safe_reason FROM classification_outcome
            UNION ALL
            SELECT condition_id, field_key, normalized_value_hash FROM match_evidence;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    continue;
                }

                var text = reader.GetValue(i)?.ToString() ?? string.Empty;
                Assert.DoesNotContain("secret-description-xyz", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Failed_evaluate_before_write_leaves_zero_evaluation_rows()
    {
        // No active rule set → lifecycle failure, no durable evaluation.
        await RecordAsync("orphan");
        var before = await CountEvaluationsAsync();
        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(before, await CountEvaluationsAsync());
        Assert.Equal(0L, await CountOutcomesAsync());
    }

    [Fact]
    public async Task Stored_result_fingerprint_matches_projection_input_fingerprint()
    {
        var category = await CreateCategoryAsync("Fp");
        var versionId = await SaveDraftAsync(category.CategoryId, "fp merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "fp merchant");
        await RecordAsync("fp merchant");

        var result = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        // Load after evaluate: store generation + ordered membership are stable; SnapshotId is
        // freeze-local and may differ from the evaluate run's retained freeze identity.
        var loader = new ClassificationEvaluationInputLoader(ledger);
        var input = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(input.IsSuccess, input.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await evaluationStore.GetRunAsync(connection, null, result.Value!.EvaluationId, CancellationToken.None);
        Assert.Equal(input.Value!.OrderedItemsFingerprint, run!.OrderedItemsFingerprint);
        Assert.Equal(input.Value.StoreGenerationFingerprint, run.StoreGenerationFingerprint);
        Assert.Equal(input.Value.TotalCount, result.Value.TotalCount);
    }

    [Fact]
    public void Mapper_to_evaluate_result_preserves_partition_totals()
    {
        var fingerprint = EvaluationFingerprint.Create(
            "1.0",
            ClassificationProjectionVersions.ClassificationV1,
            new string('a', 64),
            "snap",
            DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            new string('b', 64),
            NormalizationDescriptor.V1.Version,
            "rsv-1",
            new string('c', 64));
        var outcomes = new[]
        {
            ClassificationOutcome.Suggestion(0, "tx-0", "cat", ["rv-1"],
                [new MatchEvidence("rv-1", "c0", "description.normalized", "equals", new string('d', 64))],
                new string('e', 64)),
            ClassificationOutcome.NoSuggestion(1, "tx-1", new string('f', 64)),
            ClassificationOutcome.Conflict(2, "tx-2", ["rv-1", "rv-2"],
                [
                    new MatchEvidence("rv-1", "c0", "description.normalized", "equals", new string('d', 64)),
                    new MatchEvidence("rv-2", "c1", "description.normalized", "equals", new string('d', 64))
                ],
                new string('g', 64))
        };
        var evaluation = new ClassificationEvaluationResult(fingerprint, outcomes);
        var publicResult = ClassifyContractMapper.ToEvaluateResult(
            "eval-1", "rsv-1", NormalizationDescriptor.V1.Version, new string('h', 64), evaluation);
        Assert.Equal(3, publicResult.TotalCount);
        Assert.Equal(1, publicResult.SuggestionCount);
        Assert.Equal(1, publicResult.NoSuggestionCount);
        Assert.Equal(1, publicResult.ConflictCount);
        Assert.Equal(0, publicResult.StaleCount);
    }

    [Fact]
    public void Mapper_rejects_incomplete_projection_item_mapping()
    {
        var bad = new ClassificationProjectionItem(
            0, "tx", "acct", "2026-07-15", "not-money", "desc",
            ClassificationAmountDirection.Expense, CategoryMutationState.Assignable,
            null, null, "tr", "rr", "ar");
        Assert.False(ClassifyContractMapper.TryMapProjectionItem(bad, out _));
    }

    [Fact]
    public async Task Persist_rejects_partial_outcome_count_before_write()
    {
        var run = new ClassifyEvaluationRunRow(
            "eval-partial",
            null,
            "rsv",
            NormalizationDescriptor.V1.Version,
            "1.0",
            ClassificationProjectionVersions.ClassificationV1,
            new string('a', 64),
            "snap",
            DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            new string('b', 64),
            new string('c', 64),
            InputCount: 2,
            SuggestionCount: 1,
            NoSuggestionCount: 0,
            ConflictCount: 0,
            StaleCount: 0,
            ClassifyContractMapper.EvaluationLifecycleCompleted,
            "actor",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        // Only one outcome for input_count=2.
        var outcomes = new[]
        {
            new PersistedEvaluationOutcome(
                new ClassifyOutcomeRow("o1", "eval-partial", 0, "tx", "suggestion", "cat", new string('d', 64), "suggestion"),
                Array.Empty<MatchEvidence>())
        };

        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
            await using var tx = store.BeginImmediate(connection);
            await evaluationStore.PersistCompletedAsync(connection, tx, run, outcomes, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Second_evaluate_does_not_corrupt_first_stored_run()
    {
        var category = await CreateCategoryAsync("Keep");
        var versionId = await SaveDraftAsync(category.CategoryId, "keep merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "keep merchant");
        await RecordAsync("keep merchant");

        var first = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        await RecordAsync("another");
        var second = await evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.NotEqual(first.Value!.EvaluationId, second.Value!.EvaluationId);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var firstRun = await evaluationStore.GetRunAsync(
            connection, null, first.Value.EvaluationId, CancellationToken.None);
        Assert.Equal(first.Value.TotalCount, firstRun!.InputCount);
        Assert.Equal(first.Value.SuggestionCount, firstRun.SuggestionCount);
        Assert.Equal(2L, await evaluationStore.CountEvaluationsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Active_rule_set_pointer_unchanged_by_evaluate()
    {
        var category = await CreateCategoryAsync("Ptr");
        var versionId = await SaveDraftAsync(category.CategoryId, "ptr merchant");
        await ActivateWithGateAsync(versionId, category.CategoryId, "ptr merchant");
        await RecordAsync("ptr merchant");

        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        {
            var before = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
            Assert.NotNull(before);

            var result = await evaluate.HandleAsync(
                new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
                actor, NextKey(), CancellationToken.None);
            Assert.True(result.IsSuccess, result.ErrorCode);

            var after = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
            Assert.Equal(before!.RuleSetVersionId, after!.RuleSetVersionId);
            Assert.Equal(before.ActivationEpoch, after.ActivationEpoch);
        }
    }

    [Fact]
    public void Evidence_bound_helper_enforces_published_max()
    {
        var fingerprint = EvaluationFingerprint.Create(
            "1.0", ClassificationProjectionVersions.ClassificationV1,
            new string('a', 64), "s", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            new string('b', 64), NormalizationDescriptor.V1.Version, "rsv", new string('c', 64));
        var evidence = Enumerable.Range(0, 3)
            .Select(i => new MatchEvidence(
                "rv",
                "c" + i.ToString(CultureInfo.InvariantCulture),
                "description.normalized",
                "equals",
                new string('d', 64)))
            .ToArray();
        var evaluation = new ClassificationEvaluationResult(
            fingerprint,
            [ClassificationOutcome.Suggestion(0, "tx", "cat", ["rv"], evidence, new string('e', 64))]);
        Assert.True(ClassifyContractMapper.IsEvidenceWithinBound(evaluation, maxEvidenceRows: 3));
        Assert.False(ClassifyContractMapper.IsEvidenceWithinBound(evaluation, maxEvidenceRows: 2));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<long> CountEvaluationsAsync()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        return await evaluationStore.CountEvaluationsAsync(connection, null, CancellationToken.None);
    }

    private async Task<long> CountOutcomesAsync()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        return await evaluationStore.CountOutcomesAsync(connection, null, CancellationToken.None);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var (path, gateTxIds) = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
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
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "persist activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
        foreach (var txId in gateTxIds)
        {
            _ = await ExecuteSuccessAsync(
                "ledger.transaction.category.assign",
                new AssignCategoryInput(txId, categoryId, "remove gate evidence from evaluation universe"),
                NextKey(),
                LedgerJsonContext.Default.AssignCategoryInput,
                LedgerJsonContext.Default.CategoryAllocationResult);
        }
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await save.HandleAsync(
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
                "persist draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
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
        return (path, created.Select(c => c.TxId).ToArray());
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Persist Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "persist:" + Guid.NewGuid().ToString("N")[..8], null, null)),
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

    private string NextKey() => $"eval-persist-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
