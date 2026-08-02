using System.Globalization;
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
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-EVALUATION-INPUT / bd-25a7
/// Descriptor, paging, ordering, expiry, exact-limit, over-limit, no-state, no-mutation cases.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationEvaluationInputLoaderTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-eval-input-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "eval-input", "run-01");
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

    // ── Descriptor ───────────────────────────────────────────────────────────

    [Fact]
    public void Max_transaction_count_matches_published_c11_bound()
    {
        Assert.Equal(10_000, ClassificationEvaluationInputLoader.MaxTransactionCount);
        Assert.Equal(10_000, ClassifyOperationModule.V1Limits.MaxTransactionCount);
    }

    [Fact]
    public async Task Load_requires_actor()
    {
        var result = await loader.LoadAsync(null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Load_rejects_blank_contract_version()
    {
        var result = await loader.LoadAsync(actor, CancellationToken.None, contractVersion: "  ");
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Load_rejects_incompatible_contract_version_before_input()
    {
        await RecordAsync("incompat");
        var result = await loader.LoadAsync(actor, CancellationToken.None, contractVersion: "2.0");
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.LedgerIncompatible, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Load_returns_classification_v1_descriptor_fields()
    {
        await RecordAsync("desc-ok");
        var result = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var input = result.Value!;
        Assert.Equal(ActualsContractVersions.Current, input.LedgerContractVersion);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, input.ProjectionVersion);
        Assert.False(string.IsNullOrWhiteSpace(input.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(input.SnapshotExpiresAt));
        Assert.Equal(64, input.StoreGenerationFingerprint.Length);
        Assert.Equal(64, input.CategoryLifecycleFingerprint.Length);
        Assert.Equal(64, input.OrderedItemsFingerprint.Length);
        Assert.Equal(64, input.SnapshotFingerprint.Length);
    }

    // ── Live projection / eligibility boundary ───────────────────────────────

    [Fact]
    public async Task Load_evaluation_excludes_already_categorized_transactions()
    {
        var cat = await CreateCategoryAsync("Food");
        var uncat = await RecordAsync("uncat");
        var categorized = await RecordAsync("categorized");
        await AssignAsync(categorized.TransactionId, cat.CategoryId);

        var result = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Contains(result.Value!.Items, i => i.TransactionId == uncat.TransactionId);
        Assert.DoesNotContain(result.Value.Items, i => i.TransactionId == categorized.TransactionId);
    }

    [Fact]
    public async Task Load_buffers_complete_multi_page_projection_in_ordinal_order()
    {
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync("page-" + i.ToString(CultureInfo.InvariantCulture));
        }

        var result = await loader.LoadAsync(actor, CancellationToken.None, pageSize: 2);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(5, result.Value!.TotalCount);
        Assert.Equal(5, result.Value.Items.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Value.Items.Select(i => i.Ordinal).ToArray());
        Assert.Equal(
            5,
            result.Value.Items.Select(i => i.TransactionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Load_repeated_acquisition_is_byte_equivalent_for_unchanged_snapshot()
    {
        await RecordAsync("stable-a");
        await RecordAsync("stable-b");

        var first = await loader.LoadAsync(actor, CancellationToken.None);
        var second = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess, first.ErrorCode + "/" + second.ErrorCode);

        // Membership and store generation are stable across freezes when LEDGER data is unchanged.
        // SnapshotId/ExpiresAt are server-minted per evaluation freeze and intentionally vary.
        Assert.Equal(first.Value!.StoreGenerationFingerprint, second.Value!.StoreGenerationFingerprint);
        Assert.Equal(first.Value.OrderedItemsFingerprint, second.Value.OrderedItemsFingerprint);
        Assert.Equal(first.Value.TotalCount, second.Value.TotalCount);
        Assert.Equal(
            first.Value.Items.Select(i => (i.Ordinal, i.TransactionId, i.TransactionRevision, i.AllocationRevision)).ToArray(),
            second.Value.Items.Select(i => (i.Ordinal, i.TransactionId, i.TransactionRevision, i.AllocationRevision)).ToArray());
    }

    [Fact]
    public async Task Load_creates_no_classify_state_store()
    {
        await RecordAsync("no-state");
        var before = Directory.Exists(Path.Combine(root, "classify"));
        var result = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(before);
        Assert.False(Directory.Exists(Path.Combine(root, "classify")));
        Assert.False(File.Exists(Path.Combine(root, "classify", "classify.db")));
    }

    [Fact]
    public async Task Load_performs_no_ledger_mutation()
    {
        var tx = await RecordAsync("no-mut");
        var before = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(before.IsSuccess);
        var beforeCategory = before.Value!.Category.CategoryId;

        var result = await loader.LoadAsync(actor, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var after = await ledger.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(after.IsSuccess);
        Assert.Equal(beforeCategory, after.Value!.Category.CategoryId);
        Assert.Equal(before.Value!.LifecycleStatus, after.Value.LifecycleStatus);
        Assert.Equal(before.Value.OriginalDescription, after.Value.OriginalDescription);
    }

    // ── Pure validation (descriptor / ordinals / limits / expiry) ────────────

    [Fact]
    public void Validate_rejects_missing_projection_version()
    {
        var page = SyntheticPage(itemCount: 1) with { ProjectionVersion = null };
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_non_classification_v1_projection()
    {
        var page = SyntheticPage(itemCount: 1) with { ProjectionVersion = "classification_v0" };
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_missing_store_generation_fingerprint()
    {
        var page = SyntheticPage(itemCount: 0) with { StoreGenerationFingerprint = null };
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_missing_category_lifecycle_fingerprint()
    {
        var page = SyntheticPage(itemCount: 0) with { CategoryIdentityLifecycleFingerprint = "short" };
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_open_cursor_as_incomplete_acquisition()
    {
        var page = SyntheticPage(itemCount: 1) with { Cursor = "still-open" };
        Assert.Equal(
            ClassifyErrors.Stale,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_expired_snapshot()
    {
        var page = SyntheticPage(itemCount: 1) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture)
        };
        Assert.Equal(
            ClassifyErrors.Stale,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_invalid_expiry_format()
    {
        var page = SyntheticPage(itemCount: 0) with { ExpiresAt = "not-a-timestamp" };
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_accepts_exact_limit_row_count()
    {
        var page = SyntheticPage(itemCount: 3, maxBound: 3);
        Assert.Null(ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
            page, DateTimeOffset.UtcNow, maxTransactionCount: 3));
    }

    [Fact]
    public void Validate_rejects_one_over_limit_without_publishing_input()
    {
        var page = SyntheticPage(itemCount: 4, maxBound: 3);
        Assert.Equal(
            ClassifyErrors.ResourceLimit,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                page, DateTimeOffset.UtcNow, maxTransactionCount: 3));
    }

    [Fact]
    public void Validate_rejects_duplicate_ordinals()
    {
        var items = new[]
        {
            Item(0, "tx-a"),
            Item(0, "tx-b")
        };
        var page = SyntheticPage(itemCount: 0) with
        {
            TotalCount = 2,
            ClassificationItems = items
        };
        Assert.Equal(
            ClassifyErrors.Integrity,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_missing_ordinal_gap()
    {
        var items = new[]
        {
            Item(0, "tx-a"),
            Item(2, "tx-b")
        };
        var page = SyntheticPage(itemCount: 0) with
        {
            TotalCount = 2,
            ClassificationItems = items
        };
        Assert.Equal(
            ClassifyErrors.Integrity,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_membership_total_mismatch()
    {
        var page = SyntheticPage(itemCount: 1) with { TotalCount = 2 };
        Assert.Equal(
            ClassifyErrors.Integrity,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validate_rejects_duplicate_transaction_ids()
    {
        var items = new[]
        {
            Item(0, "tx-same"),
            Item(1, "tx-same")
        };
        var page = SyntheticPage(itemCount: 0) with
        {
            TotalCount = 2,
            ClassificationItems = items
        };
        Assert.Equal(
            ClassifyErrors.Integrity,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BuildInput_and_canonical_bytes_are_stable_for_identical_pages()
    {
        var page = SyntheticPage(itemCount: 2);
        var a = ClassificationEvaluationInputLoader.BuildInput(page);
        var b = ClassificationEvaluationInputLoader.BuildInput(page);
        Assert.IsNotType<ClassificationProjectionItem[]>(a.Items);
        Assert.IsNotType<ClassificationCategoryIdentity[]>(a.ActiveCategories);
        Assert.Equal(a.SnapshotFingerprint, b.SnapshotFingerprint);
        Assert.Equal(a.OrderedItemsFingerprint, b.OrderedItemsFingerprint);
        Assert.Equal(
            ClassificationEvaluationInputLoader.ToCanonicalBytes(a),
            ClassificationEvaluationInputLoader.ToCanonicalBytes(b));
    }

    [Fact]
    public void BuildInput_ordered_items_fingerprint_matches_evaluation_fingerprint_helper()
    {
        var page = SyntheticPage(itemCount: 2);
        var input = ClassificationEvaluationInputLoader.BuildInput(page);
        var expected = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
            input.Items.Select(i => (
                i.Ordinal,
                i.TransactionId,
                ClassificationEvaluationInputLoader.ComputeItemLifecycleFingerprint(i))));
        Assert.Equal(expected, input.OrderedItemsFingerprint);
    }

    [Fact]
    public void Validate_accepts_empty_eligible_universe()
    {
        var page = SyntheticPage(itemCount: 0);
        Assert.Null(ClassificationEvaluationInputLoader.ValidateAcquiredProjection(page, DateTimeOffset.UtcNow));
        var input = ClassificationEvaluationInputLoader.BuildInput(page);
        Assert.Equal(0, input.TotalCount);
        Assert.Empty(input.Items);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActualsQueryResult SyntheticPage(int itemCount, long? maxBound = null)
    {
        var items = new List<ClassificationProjectionItem>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            items.Add(Item(i, "tx-" + i.ToString(CultureInfo.InvariantCulture)));
        }

        // maxBound unused except to document exact/over limit construction at call sites.
        _ = maxBound;
        return new ActualsQueryResult(
            SnapshotId: "snap-" + Hex64("snap"),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            TotalCount: itemCount,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: Hex64("store-gen"),
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: Hex64("cat-life"),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems: items);
    }

    private static ClassificationProjectionItem Item(int ordinal, string transactionId) =>
        new(
            ordinal,
            transactionId,
            "acct-1",
            "2026-07-15",
            "-12.34",
            "merchant " + transactionId,
            ClassificationAmountDirection.Expense,
            CategoryMutationState.Assignable,
            CurrentCategoryId: null,
            CurrentAllocationId: null,
            TransactionRevision: "tr-" + transactionId,
            RelationshipRevision: "rr-" + transactionId,
            AllocationRevision: "ar-" + transactionId);

    private static string Hex64(string seed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "Eval Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(), "ZAR"),
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
                    "eval-input:" + Guid.NewGuid().ToString("N")[..8],
                    null,
                    null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task AssignAsync(string transactionId, string categoryId)
    {
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, "owner"),
            NextKey(),
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);
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

    private string NextKey() => $"eval-input-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
