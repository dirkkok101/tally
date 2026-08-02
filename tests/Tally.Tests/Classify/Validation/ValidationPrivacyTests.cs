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
using Tally.Domain.Ledger;
using Tally.Contracts.Ledger.Transactions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Accounts;
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
        validationStore = new ClassificationValidationStore();
        save = new SaveClassificationRuleCommand(store, new ClassificationRuleStore(), ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store,
            new ClassificationRuleStore(),
            validationStore,
            ClassifyCorpusExtensions.CreateReader(),
            ledger,
            classify.Idempotency);
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
    public async Task Durable_validation_rows_exclude_description_token_amount_and_path()
    {
        var category = await CreateCategoryAsync("PrivCat");
        // Rule + bound description are neutral; canaries live only in the private path segment.
        // Durable validation must not retain private path or canary payloads.
        var versionId = await SaveDraftAsync(category.CategoryId, "privacy merchant");
        var dir = Path.Combine(root, CanaryPath);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var corpusPath = await WriteBoundCorpusAsync(
            dir,
            "corpus.jsonl",
            [("privacy merchant", "suggestion", category.CategoryId)]);

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
        // Aggregate public result may include expectedOutcomeFingerprint (hash only), never kind/payload fields.
        Assert.DoesNotContain("expectedOutcomeKind", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedCategoryId", resultJson, StringComparison.OrdinalIgnoreCase);
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
        var corpusPath = await WriteBoundCorpusAsync(
            root,
            "fp.jsonl",
            [("plain merchant", "suggestion", category.CategoryId)]);

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


    private async Task<string> WriteBoundCorpusAsync(
        string directory,
        string fileName,
        IReadOnlyList<(string Description, string? ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordTransactionAsync(row.Description);
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
            Assert.True(ValidateClassificationRuleCommand.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ValidateClassificationRuleCommand.ComputeItemLifecycleFingerprint(item);
            var expected = rows[i];
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            if (expected.ExpectedKind is not null)
            {
                sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expected.ExpectedKind));
            }

            if (expected.ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expected.ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var digits = (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(CultureInfo.InvariantCulture);
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Priv Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + digits, "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<TransactionDetail> RecordTransactionAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                "-12.34",
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(
                    EvidenceKind.AgentCapture,
                    digest,
                    "priv-capture:" + Guid.NewGuid().ToString("N")[..8],
                    null,
                    null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private string NextKey() => $"priv-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
