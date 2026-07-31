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

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-VALIDATION / FR-CLASSIFY-RULE-VALIDATION / bd-2kpw
/// Fingerprints, accounting, canaries, corpus-unavailable, no activation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationRuleValidationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-rule-validate-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-validate", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private ClassificationValidationStore validationStore = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
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
        save = new SaveClassificationRuleCommand(store, ruleStore, ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store,
            ruleStore,
            validationStore,
            ClassifyCorpusExtensions.CreateReader(),
            ledger,
            classify.Idempotency);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success / accounting ─────────────────────────────────────────────────

    [Fact]
    public async Task Valid_candidate_over_matching_corpus_is_activation_eligible()
    {
        var category = await CreateCategoryAsync("Groceries");
        var versionId = await SaveDraftAsync(
            category.CategoryId,
            DescriptionEquals("whole foods"));
        var corpus = WriteCorpus([
            CorpusRow(0, "tx-0", "acct", "Whole Foods Market", "outflow", 100, "suggestion", category.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.ActivationEligible);
        Assert.Equal(1, result.Value.TotalRows);
        Assert.Equal(1, result.Value.SuggestionCount);
        Assert.Equal(0, result.Value.IncorrectApplicationCanaries);
        Assert.Equal(64, result.Value.CorpusFingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ValidationId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await validationStore.GetRunAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.NotNull(report);
        Assert.Equal(ClassificationValidationStore.LifecycleCompleted, run!.LifecycleState);
        Assert.Equal(1, report!.AccountedRows);
        Assert.Equal(1, report.TotalRows);
        Assert.Equal(0, report.UnexplainedConflictCount);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Every_row_accounted_exactly_once_with_mixed_partition()
    {
        var catA = await CreateCategoryAsync("A");
        var catB = await CreateCategoryAsync("B");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("alpha"), ruleId: "rule-a");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("beta"), ruleId: "rule-b");
        var corpus = WriteCorpus([
            CorpusRow(0, "tx-0", "acct", "alpha", "outflow", 1, "suggestion", catA.CategoryId),
            CorpusRow(1, "tx-1", "acct", "beta", "outflow", 2, "suggestion", catB.CategoryId),
            CorpusRow(2, "tx-2", "acct", "none", "outflow", 3, "no_suggestion", null)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(3, result.Value!.TotalRows);
        Assert.Equal(2, result.Value.SuggestionCount);
        Assert.Equal(0, result.Value.ConflictCount);
        Assert.True(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.Equal(3, report!.AccountedRows);
        Assert.Equal(1, report.NoSuggestionCount);
        Assert.Equal(3, report.SuggestionCount + report.NoSuggestionCount + report.ConflictCount + report.StaleCount);
    }

    // ── Fingerprints ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_run_binds_all_required_fingerprints()
    {
        var category = await CreateCategoryAsync("Bind");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("bindme"));
        var corpus = WriteCorpus([
            CorpusRow(0, "tx", "acct", "bindme", "outflow", 5, "suggestion", category.CategoryId)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await validationStore.GetRunAsync(connection, null, result.Value!.ValidationId, CancellationToken.None);
        Assert.Equal(64, run!.CandidateFingerprint.Length);
        Assert.Equal(64, run.CorpusFingerprint.Length);
        Assert.Equal(64, run.ExpectedOutcomeFingerprint.Length);
        Assert.Equal(64, run.CategoryLifecycleFingerprint.Length);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, run.ProjectionContractVersion);
        Assert.Equal(NormalizationDescriptor.V1.Version, run.NormalizationVersion);
        Assert.Equal(ClassificationRuleStore.OriginOwnerAuthored, run.RuleOrigin);
    }

    // ── Canaries ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Incorrect_application_canary_makes_activation_ineligible()
    {
        var category = await CreateCategoryAsync("Wrong");
        var other = await CreateCategoryAsync("Other");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("shop"));
        var corpus = WriteCorpus([
            // Engine will suggest `category`, but expected is `other` → incorrect application.
            CorpusRow(0, "tx", "acct", "shop", "outflow", 10, "suggestion", other.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
        Assert.True(result.Value.IncorrectApplicationCanaries >= 1);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.True(report!.IncorrectApplicationCanaryCount >= 1);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unexplained_conflict_makes_activation_ineligible()
    {
        var catA = await CreateCategoryAsync("CA");
        var catB = await CreateCategoryAsync("CB");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("clash"), ruleId: "rule-ca");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("clash"), ruleId: "rule-cb");
        var corpus = WriteCorpus([
            // Expected suggestion, engine will conflict → incorrect + unexplained conflict paths.
            CorpusRow(0, "tx", "acct", "clash", "outflow", 1, "suggestion", catA.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.ConflictCount);
        Assert.False(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.True(report!.UnexplainedConflictCount >= 1 || report.IncorrectApplicationCanaryCount >= 1);
    }

    [Fact]
    public async Task Expected_conflict_is_explained_and_may_remain_eligible_when_correct()
    {
        var catA = await CreateCategoryAsync("XA");
        var catB = await CreateCategoryAsync("XB");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("both"), ruleId: "rule-xa");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("both"), ruleId: "rule-xb");
        var corpus = WriteCorpus([
            CorpusRow(0, "tx", "acct", "both", "outflow", 1, "conflict", null)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.ConflictCount);
        Assert.True(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.Equal(0, report!.UnexplainedConflictCount);
        Assert.Equal(0, report.IncorrectApplicationCanaryCount);
    }

    // ── Corpus unavailable / fail closed ─────────────────────────────────────

    [Fact]
    public async Task Missing_corpus_fails_closed_without_active_set_change()
    {
        var category = await CreateCategoryAsync("Miss");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var missing = Path.Combine(root, "does-not-exist.jsonl");

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], missing),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PrivateCorpusErrors.NotFound, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM validation_run;"));
    }

    [Fact]
    public async Task Blank_corpus_source_fails_closed()
    {
        var category = await CreateCategoryAsync("BlankSrc");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], "  "),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PathRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_corpus_fails_closed_without_activation()
    {
        var category = await CreateCategoryAsync("Bad");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var path = Path.Combine(root, "bad.jsonl");
        WriteOwnerFile(path, "{not-json\n");
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.Malformed, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_candidate_returns_rule_version_not_found()
    {
        var corpus = WriteCorpus([
            CorpusRow(0, "tx", "acct", "x", "outflow", 1, null, null)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, ["missing-version"], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.RuleVersionNotFound, result.ErrorCode);
    }

    // ── No activation / no ledger mutation ───────────────────────────────────

    [Fact]
    public async Task Validation_never_creates_active_rule_set_or_rule_set_version()
    {
        var category = await CreateCategoryAsync("NoAct");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("keep"));
        var corpus = WriteCorpus([
            CorpusRow(0, "tx", "acct", "keep", "outflow", 1, "suggestion", category.CategoryId)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_member;"));
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Identical_idempotent_validation_replays_terminal_result()
    {
        var category = await CreateCategoryAsync("Idem");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("idem"));
        var corpus = WriteCorpus([
            CorpusRow(0, "tx", "acct", "idem", "outflow", 1, "suggestion", category.CategoryId)
        ]);
        const string key = "validate-idem-1";
        var first = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            key,
            CancellationToken.None);
        var second = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            key,
            CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.ValidationId, second.Value!.ValidationId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM validation_run;"));
    }

    // ── Boundary ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_actor_and_idempotency_are_rejected()
    {
        var category = await CreateCategoryAsync("Env");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("e"));
        var corpus = WriteCorpus([CorpusRow(0, "tx", "acct", "e", "outflow", 1, null, null)]);
        var noActor = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            null,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, noActor.ErrorCode);
        var noKey = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, noKey.ErrorCode);
    }

    [Fact]
    public async Task Empty_candidate_list_is_rejected()
    {
        var corpus = WriteCorpus([CorpusRow(0, "tx", "acct", "e", "outflow", 1, null, null)]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> SaveDraftAsync(
        string categoryId,
        ClassificationRuleConditionInput condition,
        string? ruleId = null)
    {
        var result = await save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [condition],
                "draft for validate"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private static ClassificationRuleConditionInput DescriptionEquals(string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.EqualsPredicate,
            value,
            null, null, null,
            out var condition,
            out _));
        // Wire input uses enums — rebuild as contract input from canonical text.
        return new ClassificationRuleConditionInput(
            0,
            ClassificationRuleFieldKey.DescriptionNormalized,
            ClassificationRulePredicateKind.Equals,
            ValueText: value);
    }

    private string WriteCorpus(IReadOnlyList<string> lines)
    {
        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        WriteOwnerFile(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private static string CorpusRow(
        int ordinal,
        string transactionId,
        string accountId,
        string description,
        string direction,
        long minor,
        string? expectedKind,
        string? expectedCategory)
    {
        var life = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("life-" + ordinal)));
        var sb = new StringBuilder();
        sb.Append("{\"ordinal\":").Append(ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(transactionId));
        sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(accountId));
        sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
        sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
        sb.Append(",\"amountAbsoluteMinor\":").Append(minor.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
        if (expectedKind is not null)
        {
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expectedKind));
        }

        if (expectedCategory is not null)
        {
            sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expectedCategory));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteOwnerFile(string path, string content)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
            ?? throw new InvalidOperationException($"Missing operation {operationId}");
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
            ?? throw new InvalidOperationException("No result envelope");
        Assert.Equal(0, processResult.ExitCode);
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"val-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
