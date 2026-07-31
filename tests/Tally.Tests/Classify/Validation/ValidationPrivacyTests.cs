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
using Tally.Domain.Classify.Rules;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// bd-2kpw / NFR-CLASSIFY-LOCAL-DATA-PROTECTION
/// Durable validation evidence and diagnostics never retain private payloads.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ValidationPrivacyTests : IAsyncLifetime
{
    private const string CanaryDescription = "CANARY_VALIDATE_DESC_9a2f";
    private const string CanaryToken = "canaryvalidatetoken";
    private const string CanaryAmount = "887766554433";
    private const string CanaryPath = "private-validate-path-seg";

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-val-privacy-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "val-privacy", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
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
        validationStore = new ClassificationValidationStore();
        save = new SaveClassificationRuleCommand(store, new ClassificationRuleStore(), ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store,
            new ClassificationRuleStore(),
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

    [Fact]
    public async Task Durable_validation_rows_exclude_description_token_amount_and_path()
    {
        var category = await CreateCategoryAsync("PrivCat");
        var versionId = await SaveDraftAsync(category.CategoryId, CanaryDescription);
        var dir = Path.Combine(root, CanaryPath);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var corpusPath = Path.Combine(dir, "corpus.jsonl");
        var life = Convert.ToHexStringLower(SHA256.HashData("life"u8.ToArray()));
        var line = string.Concat(
            "{\"ordinal\":0,\"transactionId\":\"tx\",\"accountId\":\"acct\",\"sourceDescription\":",
            JsonSerializer.Serialize(CanaryDescription + " " + CanaryToken),
            ",\"amountDirection\":\"outflow\",\"amountAbsoluteMinor\":", CanaryAmount,
            ",\"itemLifecycleFingerprint\":", JsonSerializer.Serialize(life),
            ",\"expectedOutcomeKind\":\"suggestion\",\"expectedCategoryId\":",
            JsonSerializer.Serialize(category.CategoryId), "}");
        File.WriteAllText(corpusPath, line + "\n");
        File.SetUnixFileMode(corpusPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpusPath),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await validationStore.GetRunAsync(connection, null, result.Value!.ValidationId, CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.NotNull(report);

        var dbBytes = await File.ReadAllBytesAsync(store.Paths.DatabasePath);
        var dbText = Encoding.UTF8.GetString(dbBytes);
        AssertNoCanary(dbText);
        Assert.DoesNotContain(corpusPath, dbText, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryPath, dbText, StringComparison.Ordinal);

        // Aggregate result surface
        var resultJson = JsonSerializer.Serialize(result.Value);
        AssertNoCanary(resultJson);
        Assert.DoesNotContain(corpusPath, resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedOutcome", resultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failure_error_codes_never_embed_path_or_canary()
    {
        var category = await CreateCategoryAsync("FailPriv");
        var versionId = await SaveDraftAsync(category.CategoryId, "neutral");
        var dir = Path.Combine(root, CanaryPath);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var corpusPath = Path.Combine(dir, CanaryDescription + ".jsonl");
        File.WriteAllText(corpusPath, "{bad " + CanaryToken + "\n");
        File.SetUnixFileMode(corpusPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpusPath),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorCode);
        AssertNoCanary(result.ErrorCode!);
        Assert.DoesNotContain(corpusPath, result.ErrorCode!, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryPath, result.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_fingerprint_is_hex_only_without_payload()
    {
        var category = await CreateCategoryAsync("FpPriv");
        var versionId = await SaveDraftAsync(category.CategoryId, "plain merchant");
        var corpusPath = Path.Combine(root, "fp.jsonl");
        var life = Convert.ToHexStringLower(SHA256.HashData("life-fp"u8.ToArray()));
        File.WriteAllText(
            corpusPath,
            "{\"ordinal\":0,\"transactionId\":\"tx\",\"accountId\":\"a\",\"sourceDescription\":\"plain merchant\",\"amountDirection\":\"outflow\",\"amountAbsoluteMinor\":1,\"itemLifecycleFingerprint\":\"" + life + "\",\"expectedOutcomeKind\":\"suggestion\",\"expectedCategoryId\":\"" + category.CategoryId + "\"}\n");
        File.SetUnixFileMode(corpusPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpusPath),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value!.ValidationId, CancellationToken.None);
        Assert.Equal(64, report!.ReportFingerprint.Length);
        Assert.All(report.ReportFingerprint, ch => Assert.True(Uri.IsHexDigit(ch)));
    }

    [Fact]
    public void Report_builder_fingerprint_helpers_exclude_raw_descriptions()
    {
        var rows = new[]
        {
            new PrivateCorpusRow(0, "tx", "a", CanaryDescription, "outflow", 1, new string('a', 64), "suggestion", "cat")
        };
        var fp = ValidationReportBuilder.ComputeExpectedOutcomeFingerprint(rows);
        Assert.Equal(64, fp.Length);
        Assert.DoesNotContain(CanaryDescription, fp, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertNoCanary(string text)
    {
        Assert.DoesNotContain(CanaryDescription, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryToken, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryAmount, text, StringComparison.Ordinal);
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
                "privacy draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
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

    private string NextKey() => $"priv-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
