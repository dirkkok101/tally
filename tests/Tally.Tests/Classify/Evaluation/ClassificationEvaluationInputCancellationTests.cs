using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-EVALUATION-INPUT / bd-25a7
/// Cancellation and partial-input rejection: no evaluation input on cancel or incomplete acquisition.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationEvaluationInputCancellationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-eval-input-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "eval-input-cancel", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassificationEvaluationInputLoader loader = null!;
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
        loader = new ClassificationEvaluationInputLoader(ledger);
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
    public async Task Load_cancelled_token_returns_stable_failure_without_input()
    {
        await RecordAsync("cancel-me");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await loader.LoadAsync(actor, cts.Token);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(ClassifyErrors.Unexpected, result.ErrorCode);
    }

    [Fact]
    public async Task Load_cancelled_token_creates_no_classify_state()
    {
        await RecordAsync("cancel-state");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await loader.LoadAsync(actor, cts.Token);
        Assert.False(Directory.Exists(Path.Combine(root, "classify")));
        Assert.False(File.Exists(Path.Combine(root, "classify", "classify.db")));
    }

    [Fact]
    public async Task Load_cancelled_token_does_not_mutate_ledger()
    {
        var tx = await RecordAsync("cancel-mut");
        var before = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await loader.LoadAsync(actor, cts.Token);

        var after = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(before.IsSuccess && after.IsSuccess);
        Assert.Equal(before.Value!.LifecycleStatus, after.Value!.LifecycleStatus);
        Assert.Equal(before.Value.Category.CategoryId, after.Value.Category.CategoryId);
        Assert.Equal(before.Value.OriginalDescription, after.Value.OriginalDescription);
    }

    [Fact]
    public void Incomplete_cursor_never_builds_evaluation_input()
    {
        var page = new ActualsQueryResult(
            SnapshotId: "snap-partial",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            TotalCount: 1,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: "page-2-cursor",
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: Convert.ToHexStringLower(SHA256.HashData("store"u8.ToArray())),
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: Convert.ToHexStringLower(SHA256.HashData("cat"u8.ToArray())),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems:
            [
                new ClassificationProjectionItem(
                    0,
                    "tx-1",
                    "acct-1",
                    "2026-07-15",
                    "-1.00",
                    "partial",
                    ClassificationAmountDirection.Expense,
                    CategoryMutationState.Assignable,
                    null,
                    null,
                    "tr",
                    "rr",
                    "ar")
            ]);

        var error = ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
            page, DateTimeOffset.UtcNow);
        Assert.Equal(ClassifyErrors.Stale, error);
    }

    [Fact]
    public void Failed_validation_does_not_return_partial_membership()
    {
        // Total claims 2 but only one item is present — fail closed, no partial input.
        var page = new ActualsQueryResult(
            SnapshotId: "snap-partial-2",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            TotalCount: 2,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: Convert.ToHexStringLower(SHA256.HashData("store2"u8.ToArray())),
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: Convert.ToHexStringLower(SHA256.HashData("cat2"u8.ToArray())),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems:
            [
                new ClassificationProjectionItem(
                    0,
                    "tx-only",
                    "acct-1",
                    "2026-07-15",
                    "-1.00",
                    "only",
                    ClassificationAmountDirection.Expense,
                    CategoryMutationState.Assignable,
                    null,
                    null,
                    "tr",
                    "rr",
                    "ar")
            ]);

        Assert.Equal(
            ClassifyErrors.Integrity,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Successful_load_after_prior_cancellation_still_works()
    {
        await RecordAsync("retry-after-cancel");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await loader.LoadAsync(actor, cts.Token);
        Assert.False(cancelled.IsSuccess);

        var ok = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(ok.IsSuccess, ok.ErrorCode);
        Assert.NotNull(ok.Value);
        Assert.True(ok.Value!.TotalCount >= 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "Cancel Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(), "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

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
                    "eval-cancel:" + Guid.NewGuid().ToString("N")[..8],
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

    private string NextKey() => $"eval-cancel-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
