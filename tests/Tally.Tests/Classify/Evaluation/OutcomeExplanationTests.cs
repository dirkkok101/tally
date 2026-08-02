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
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-OUTCOME-EXPLANATION / bd-zae9
/// Explanation partitions: suggestion, no-suggestion, conflict, not-found, evidence-unavailable, disclosure.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class OutcomeExplanationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-outcome-explain-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "outcome-explain", "run-01");
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

    [Fact]
    public async Task Outcome_get_requires_actor()
    {
        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, "e", "t"),
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Outcome_get_rejects_unsupported_version()
    {
        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest("9.9", "e", "t"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_evaluation_returns_evaluation_not_found()
    {
        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, "missing-eval", "tx"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.EvaluationNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_transaction_returns_outcome_not_found()
    {
        var eval = await SeedSuggestionEvaluationAsync("shop");
        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, "no-such-tx"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.OutcomeNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Suggestion_explanation_returns_retained_identity_and_contributing_rules()
    {
        var category = await CreateCategoryAsync("Groceries");
        var eval = await SeedSuggestionEvaluationAsync("whole foods", category);
        var txId = eval.TransactionIds[0];

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, txId),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOutcomeKind.Suggestion, result.Value!.Kind);
        Assert.Equal(eval.EvaluationId, result.Value.EvaluationId);
        Assert.Equal(txId, result.Value.TransactionId);
        Assert.Equal(category.CategoryId, result.Value.SuggestedCategoryId);
        Assert.Equal(category.Name, result.Value.SuggestedCategoryDisplayName);
        Assert.False(result.Value.IsStale);
        Assert.Null(result.Value.PermittedNextOperationId);
        Assert.Equal(NormalizationDescriptor.V1.Version, result.Value.NormalizationVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.RuleSetVersionId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.SafeReason));
        Assert.NotNull(result.Value.ContributingRuleVersionIds);
        Assert.NotEmpty(result.Value.ContributingRuleVersionIds!);
        Assert.Equal(
            result.Value.ContributingRuleVersionIds!.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            result.Value.ContributingRuleVersionIds!.ToArray());
        Assert.Contains(eval.RuleVersionId, result.Value.ContributingRuleVersionIds!);
        Assert.NotNull(result.Value.MatchedFieldKeys);
        Assert.NotEmpty(result.Value.MatchedFieldKeys!);
        Assert.Equal(
            result.Value.MatchedFieldKeys!.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            result.Value.MatchedFieldKeys!.ToArray());
        Assert.Null(result.Value.ConflictProposals);
        Assert.True(result.Value.Ordinal >= 0);
    }

    [Fact]
    public async Task No_suggestion_explanation_has_no_category_and_stable_reason_partition()
    {
        var category = await CreateCategoryAsync("Nomatch");
        var eval = await SeedNoSuggestionEvaluationAsync("expected phrase", "different phrase", category);
        var txId = eval.TransactionIds.Single(id => id != eval.MatchedTransactionId);

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, txId),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOutcomeKind.NoSuggestion, result.Value!.Kind);
        Assert.Null(result.Value.SuggestedCategoryId);
        Assert.Null(result.Value.SuggestedCategoryDisplayName);
        Assert.Null(result.Value.ContributingRuleVersionIds);
        Assert.Null(result.Value.MatchedFieldKeys);
        Assert.Null(result.Value.ConflictProposals);
        Assert.False(result.Value.IsStale);
        Assert.Equal(ClassifyOperationIds.Evaluate, result.Value.PermittedNextOperationId);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.SafeReason));
        Assert.Equal(NormalizationDescriptor.V1.Version, result.Value.NormalizationVersion);
    }

    [Fact]
    public async Task Conflict_explanation_lists_contributing_rules_without_winner()
    {
        var catA = await CreateCategoryAsync("CA");
        var catB = await CreateCategoryAsync("CB");
        var eval = await SeedConflictEvaluationAsync("clash", catA, catB);
        var txId = eval.TransactionIds[0];

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, txId),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOutcomeKind.Conflict, result.Value!.Kind);
        Assert.Null(result.Value.SuggestedCategoryId);
        Assert.NotNull(result.Value.ContributingRuleVersionIds);
        Assert.True(result.Value.ContributingRuleVersionIds!.Count >= 2);
        Assert.Equal(
            result.Value.ContributingRuleVersionIds!.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            result.Value.ContributingRuleVersionIds!.ToArray());
        Assert.NotNull(result.Value.ConflictProposals);
        Assert.Equal(result.Value.ContributingRuleVersionIds!.Count, result.Value.ConflictProposals!.Count);
        Assert.Equal(
            result.Value.ConflictProposals!.Select(p => p.RuleVersionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            result.Value.ConflictProposals!.Select(p => p.RuleVersionId).ToArray());
        Assert.All(result.Value.ConflictProposals!, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.RuleVersionId));
            Assert.False(string.IsNullOrWhiteSpace(p.ProposedCategoryId));
            Assert.Contains(p.ProposedCategoryId, new[] { catA.CategoryId, catB.CategoryId });
        });
        Assert.False(result.Value.IsStale);
        Assert.Equal(ClassifyOperationIds.Evaluate, result.Value.PermittedNextOperationId);
    }

    [Fact]
    public async Task Evidence_unavailable_is_distinct_from_not_found()
    {
        var category = await CreateCategoryAsync("EvUnav");
        var eval = await SeedSuggestionEvaluationAsync("ev merchant", category);
        var txId = eval.TransactionIds[0];

        // Simulate durable evidence loss without reconstructing it later.
        // Product immutability triggers reject ordinary DELETE; drop the delete
        // guard only for this storage-damage simulation, then remove rows.
        await using (var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None))
        {
            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER IF EXISTS match_evidence_no_delete;";
                await drop.ExecuteNonQueryAsync();
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM match_evidence;";
            await cmd.ExecuteNonQueryAsync();
        }

        var missing = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, txId),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyContractMapper.EvidenceUnavailable, missing.ErrorCode);
        Assert.NotEqual(ClassifyErrors.OutcomeNotFound, missing.ErrorCode);
        Assert.NotEqual(ClassifyErrors.EvaluationNotFound, missing.ErrorCode);

        var unknown = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, "ghost"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.OutcomeNotFound, unknown.ErrorCode);
        Assert.NotEqual(ClassifyContractMapper.EvidenceUnavailable, unknown.ErrorCode);
    }

    [Fact]
    public async Task Explanation_does_not_disclose_raw_description_or_normalized_hashes()
    {
        var category = await CreateCategoryAsync("Disc");
        const string canary = "CANARY_PRIVATE_DESC_xyz";
        var eval = await SeedSuggestionEvaluationAsync(canary, category);
        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, eval.TransactionIds[0]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Matched_field_keys_come_only_from_retained_evidence()
    {
        var category = await CreateCategoryAsync("Fields");
        var eval = await SeedSuggestionEvaluationAsync("field merchant", category);
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var outcomes = await services.EvaluationStore.ListOutcomesAsync(
            connection, null, eval.EvaluationId, CancellationToken.None);
        var outcome = outcomes.Single(o => o.TransactionId == eval.TransactionIds[0]);
        var evidence = await services.EvaluationStore.ListEvidenceForOutcomeAsync(
            connection, null, outcome.OutcomeId, CancellationToken.None);
        var fields = ClassifyContractMapper.ToMatchedFieldKeys(evidence);
        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.False(string.IsNullOrWhiteSpace(f)));
        Assert.DoesNotContain(fields, f => f.Contains("field merchant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rename_of_active_category_preserves_validity_and_returns_current_display_name()
    {
        var category = await CreateCategoryAsync("RenameMe");
        var eval = await SeedSuggestionEvaluationAsync("rename shop", category);
        await RenameCategoryAsync(category.CategoryId, "Renamed Display");

        var result = await services.OutcomeGet.HandleAsync(
            new ClassifyOutcomeGetRequest(ClassifyOperationIds.ContractVersion, eval.EvaluationId, eval.TransactionIds[0]),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.IsStale);
        Assert.Null(result.Value.StaleDimensions);
        Assert.Null(result.Value.PermittedNextOperationId);
        Assert.Equal(category.CategoryId, result.Value.SuggestedCategoryId);
        Assert.Equal("Renamed Display", result.Value.SuggestedCategoryDisplayName);
    }

    [Fact]
    public void Policy_unappliable_kinds_require_re_evaluate()
    {
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
    public void Outcome_result_serialization_excludes_private_payload_keys()
    {
        var sample = new ClassifyOutcomeGetResult(
            ClassifyOperationIds.ContractVersion,
            "eval",
            "out",
            "tx",
            0,
            ClassifyOutcomeKind.Suggestion,
            NormalizationDescriptor.V1.Version,
            "rsv",
            "suggestion",
            "cat",
            "Groceries",
            ["rv-1"],
            ["description.normalized"],
            null,
            false,
            null,
            null);
        var json = JsonSerializer.Serialize(sample, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        Assert.Contains("normalizationVersion", json, StringComparison.Ordinal);
        Assert.Contains("ruleSetVersionId", json, StringComparison.Ordinal);
        Assert.Contains("safeReason", json, StringComparison.Ordinal);
        Assert.Contains("matchedFieldKeys", json, StringComparison.Ordinal);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("predicate", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    private sealed record SeededEval(
        string EvaluationId,
        string RuleVersionId,
        IReadOnlyList<string> TransactionIds,
        string? MatchedTransactionId = null);

    private async Task<SeededEval> SeedSuggestionEvaluationAsync(string description, CategoryDetail? category = null)
    {
        category ??= await CreateCategoryAsync("Sug");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, versionId, [tx.TransactionId], tx.TransactionId);
    }

    private async Task<SeededEval> SeedNoSuggestionEvaluationAsync(
        string ruleDescription,
        string unmatchedDescription,
        CategoryDetail category)
    {
        var versionId = await SaveDraftAsync(category.CategoryId, ruleDescription);
        await ActivateWithGateAsync(versionId, category.CategoryId, ruleDescription);
        var matched = await RecordAsync(ruleDescription);
        var unmatched = await RecordAsync(unmatchedDescription);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.NoSuggestionCount >= 1);
        return new SeededEval(
            evaluated.Value.EvaluationId,
            versionId,
            [matched.TransactionId, unmatched.TransactionId],
            matched.TransactionId);
    }

    private async Task<SeededEval> SeedConflictEvaluationAsync(
        string description,
        CategoryDetail catA,
        CategoryDetail catB)
    {
        var vA = await SaveDraftAsync(catA.CategoryId, description, "rule-ca");
        var vB = await SaveDraftAsync(catB.CategoryId, description, "rule-cb");
        await ActivateMultiWithGateAsync([vA, vB], [(description, "conflict", null)]);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.ConflictCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, vA, [tx.TransactionId]);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        await ActivateMultiWithGateAsync([versionId], [(description, "suggestion", categoryId)]);
    }

    private async Task ActivateMultiWithGateAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, versionIds, path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new Contracts.Classify.Operations.ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "outcome explain activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description, string? ruleId = null)
    {
        var result = await services.Save.HandleAsync(
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
                "outcome draft"),
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

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "outcome-rename"),
            NextKey(),
            LedgerJsonContext.Default.RenameCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Explain Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(), "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "explain:" + Guid.NewGuid().ToString("N")[..8], null, null)),
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

    private string NextKey() => $"outcome-explain-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
