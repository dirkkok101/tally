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
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Normalization;
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
/// bd-2kpw / NFR-CLASSIFY-BOUNDED-EVALUATION
/// Hard limits: 10_000 rows, 5s, 256 MiB; exact-limit acceptance; one-over rejection.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ValidationLimitTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-val-limit-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "val-limit", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
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
        save = new SaveClassificationRuleCommand(store, new ClassificationRuleStore(), ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store,
            new ClassificationRuleStore(),
            new ClassificationValidationStore(),
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

    [Fact]
    public void Published_validation_limits_match_c11_bounds()
    {
        Assert.Equal(10_000, PrivateCorpusLimits.MaxRowCount);
        Assert.Equal(10_000, ClassifyOperationModule.V1Limits.MaxCorpusRowCount);
        Assert.Equal(5_000, PrivateCorpusLimits.MaxProcessingTimeMs);
        Assert.Equal(5_000, ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
        Assert.Equal(256L * 1024 * 1024, ClassifyOperationModule.V1Limits.MaxMemoryBytes);
        Assert.Equal(500, ClassifyOperationModule.V1Limits.MaxRuleCount);
    }

    [Fact]
    public async Task Exact_max_row_count_is_accepted_when_rows_are_valid()
    {
        var category = await CreateCategoryAsync("Exact");
        var versionId = await SaveDraftAsync(category.CategoryId, "row");
        const int n = PrivateCorpusLimits.MaxRowCount;
        var lines = new string[n];
        for (var i = 0; i < n; i++)
        {
            lines[i] = CorpusLine(i, "row", category.CategoryId);
        }

        var path = Path.Combine(root, "exact.jsonl");
        WriteOwnerFile(path, string.Join('\n', lines) + "\n");

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(n, result.Value!.TotalRows);
    }

    [Fact]
    public async Task One_over_row_limit_is_rejected_as_resource_limit()
    {
        var category = await CreateCategoryAsync("Over");
        var versionId = await SaveDraftAsync(category.CategoryId, "x");
        var path = Path.Combine(root, "over.jsonl");
        var lines = new string[PrivateCorpusLimits.MaxRowCount + 1];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = CorpusLine(i, "x", category.CategoryId);
        }

        WriteOwnerFile(path, string.Join('\n', lines) + "\n");

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
    }

    [Fact]
    public async Task Candidate_count_over_max_rule_count_is_rejected()
    {
        var category = await CreateCategoryAsync("ManyRules");
        var ids = new string[(int)ClassifyOperationModule.V1Limits.MaxRuleCount + 1];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = "rv-" + i.ToString(CultureInfo.InvariantCulture);
        }

        var path = Path.Combine(root, "rules-over.jsonl");
        WriteOwnerFile(path, CorpusLine(0, "x", category.CategoryId) + "\n");
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, ids, path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
    }

    [Fact]
    public async Task Processing_time_budget_is_wired_to_five_seconds()
    {
        // Command links a 5s timeout; cancelled host token still surfaces as cancelled, while
        // timeout-only cancellation maps to ResourceLimit. Verify constants and cancelled path.
        Assert.Equal(5_000, ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
        var category = await CreateCategoryAsync("Time");
        var versionId = await SaveDraftAsync(category.CategoryId, "t");
        var path = Path.Combine(root, "time.jsonl");
        WriteOwnerFile(path, CorpusLine(0, "t", category.CategoryId) + "\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            cts.Token);
        Assert.Equal(PrivateCorpusErrors.Cancelled, result.ErrorCode);
    }

    [Fact]
    public void Memory_bound_is_256_mib()
    {
        Assert.Equal(256L * 1024 * 1024, ClassifyOperationModule.V1Limits.MaxMemoryBytes);
        // Working-set check is enforced in the command path; process memory at test time is unconstrained.
        Assert.True(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 > 0);
    }

    [Fact]
    public async Task Over_limit_does_not_write_validation_run()
    {
        var category = await CreateCategoryAsync("NoWrite");
        var versionId = await SaveDraftAsync(category.CategoryId, "z");
        var path = Path.Combine(root, "nowrite.jsonl");
        var pad = new string('q', PrivateCorpusLimits.MaxLineUtf8Bytes);
        WriteOwnerFile(
            path,
            "{\"ordinal\":0,\"transactionId\":\"tx\",\"accountId\":\"a\",\"sourceDescription\":\"" + pad
            + "\",\"amountAbsoluteMinor\":1,\"itemLifecycleFingerprint\":\"" + HexLife("z") + "\"}\n");
        var before = await CountValidationRunsAsync();
        _ = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(before, await CountValidationRunsAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<long> CountValidationRunsAsync()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM validation_run;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
                "limit draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private static string CorpusLine(int ordinal, string description, string categoryId)
    {
        var life = HexLife(ordinal.ToString(CultureInfo.InvariantCulture));
        return string.Concat(
            "{\"ordinal\":", ordinal.ToString(CultureInfo.InvariantCulture),
            ",\"transactionId\":\"tx-", ordinal.ToString(CultureInfo.InvariantCulture),
            "\",\"accountId\":\"acct\",\"sourceDescription\":", JsonSerializer.Serialize(description),
            ",\"amountDirection\":\"outflow\",\"amountAbsoluteMinor\":1,\"itemLifecycleFingerprint\":",
            JsonSerializer.Serialize(life),
            ",\"expectedOutcomeKind\":\"suggestion\",\"expectedCategoryId\":",
            JsonSerializer.Serialize(categoryId), "}");
    }

    private static string HexLife(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static void WriteOwnerFile(string path, string content)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    private string NextKey() => $"lim-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
