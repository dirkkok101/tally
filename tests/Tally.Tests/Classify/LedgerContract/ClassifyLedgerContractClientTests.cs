using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.LedgerContract;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-LEDGER-CLASSIFICATION-CLIENT / bd-2olb
/// CLASSIFY methods on the shared concrete LedgerContractClient: projection paging,
/// category display, assignment/correction preconditions, version, cancellation, and
/// Ledger error preservation without semantic translation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyLedgerContractClientTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-client-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "classify-client", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient client = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        client = new LedgerContractClient(registry, process);
        accountId = (await CreateAccountAsync()).AccountId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    // ── Projection ───────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryClassificationProjection_evaluation_returns_only_uncategorized_active()
    {
        var cat = await CreateCategoryAsync("Food");
        var uncat = await RecordAsync('a');
        var categorized = await RecordAsync('b');
        await AssignLegacyAsync(categorized.TransactionId, cat.CategoryId, "owner");

        var page = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, "1.0", actor, CancellationToken.None);

        Assert.True(page.IsSuccess);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.Value!.ProjectionVersion);
        Assert.Contains(page.Value.ClassificationItems!, item => item.TransactionId == uncat.TransactionId);
        Assert.DoesNotContain(page.Value.ClassificationItems!, item => item.TransactionId == categorized.TransactionId);
        Assert.Null(page.Value.Cursor);
    }

    [Fact]
    public async Task QueryClassificationProjection_multi_page_preserves_frozen_ordinals_and_membership()
    {
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync((char)('A' + i));
        }

        var page = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            "1.0",
            actor,
            CancellationToken.None,
            pageSize: 2);

        Assert.True(page.IsSuccess);
        Assert.Equal(5, page.Value!.TotalCount);
        Assert.Equal(5, page.Value.ClassificationItems!.Count);
        Assert.Null(page.Value.Cursor);
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            page.Value.ClassificationItems.Select(item => item.Ordinal).ToArray());
        Assert.Equal(
            5,
            page.Value.ClassificationItems.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count());
        Assert.False(string.IsNullOrWhiteSpace(page.Value.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(page.Value.CategoryIdentityLifecycleFingerprint));
    }

    [Fact]
    public async Task QueryClassificationProjection_apply_preflight_returns_every_selected_id()
    {
        var cat = await CreateCategoryAsync("Bills");
        var uncat = await RecordAsync('m');
        var catTx = await RecordAsync('n');
        await AssignLegacyAsync(catTx.TransactionId, cat.CategoryId, "owner");
        var missing = LedgerId.New().ToString();

        var page = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [uncat.TransactionId, catTx.TransactionId, missing]);

        Assert.True(page.IsSuccess);
        Assert.Equal(2, page.Value!.ClassificationItems!.Count);
        Assert.Equal([missing], page.Value.MissingTransactionIds);
        Assert.Equal(CategoryMutationState.Assignable, page.Value.ClassificationItems.Single(i => i.TransactionId == uncat.TransactionId).CategoryMutationState);
        Assert.Equal(CategoryMutationState.Correctable, page.Value.ClassificationItems.Single(i => i.TransactionId == catTx.TransactionId).CategoryMutationState);
    }

    [Fact]
    public async Task QueryClassificationProjection_rejects_unsupported_contract_version()
    {
        var result = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, "2.0", actor, CancellationToken.None);

        AssertError(result, 7, "contract.incompatible", "compatibility");
    }

    [Fact]
    public async Task QueryClassificationProjection_rejects_unsupported_item_projection()
    {
        var result = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            "1.0",
            actor,
            CancellationToken.None,
            itemProjection: "classification_v0");

        AssertError(result, 7, "contract.incompatible", "compatibility");
    }

    // ── Category display ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListClassificationCategories_returns_active_catalogue_for_display()
    {
        var created = await CreateCategoryAsync("Travel");
        var archived = await CreateCategoryAsync("Old");
        await ArchiveCategoryAsync(archived.CategoryId);

        var listed = await client.ListClassificationCategoriesAsync("1.0", actor, CancellationToken.None);

        Assert.True(listed.IsSuccess);
        Assert.Contains(listed.Value!.Items, x => x.CategoryId == created.CategoryId && x.Status == CategoryStatus.Active);
        Assert.DoesNotContain(listed.Value.Items, x => x.CategoryId == archived.CategoryId);
    }

    [Fact]
    public async Task ListClassificationCategories_rejects_unsupported_version()
    {
        var result = await client.ListClassificationCategoriesAsync("9.9", actor, CancellationToken.None);
        AssertError(result, 7, "contract.incompatible", "compatibility");
    }

    // ── Assignment / correction ──────────────────────────────────────────────

    [Fact]
    public async Task AssignCategory_with_preflight_preconditions_succeeds()
    {
        var cat = await CreateCategoryAsync("OkAssign");
        var tx = await RecordAsync('d');
        var preflight = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [tx.TransactionId]);
        var item = Assert.Single(preflight.Value!.ClassificationItems!);

        var result = await client.AssignCategoryAsync(
            new AssignCategoryInput(
                tx.TransactionId,
                cat.CategoryId,
                "owner",
                ExpectedTransactionRevision: item.TransactionRevision,
                ExpectedRelationshipRevision: item.RelationshipRevision,
                ExpectedAllocationRevision: item.AllocationRevision,
                MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(cat.CategoryId, result.Value!.Transaction.Category.CategoryId);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.AllocationEventId));
    }

    [Fact]
    public async Task AssignCategory_preserves_stale_precondition_error_without_translation()
    {
        var first = await CreateCategoryAsync("RaceFirst");
        var second = await CreateCategoryAsync("RaceSecond");
        var tx = await RecordAsync('e');
        var preflight = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [tx.TransactionId]);
        var item = Assert.Single(preflight.Value!.ClassificationItems!);

        // Intervening legacy assign after preflight.
        Assert.True((await client.AssignCategoryAsync(
            new AssignCategoryInput(tx.TransactionId, first.CategoryId, "intervening"),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None)).IsSuccess);

        var result = await client.AssignCategoryAsync(
            new AssignCategoryInput(
                tx.TransactionId,
                second.CategoryId,
                "stale race",
                ExpectedTransactionRevision: item.TransactionRevision,
                ExpectedRelationshipRevision: item.RelationshipRevision,
                ExpectedAllocationRevision: item.AllocationRevision,
                MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

        AssertError(result, 5, CategoryMutationPreconditionCodes.StalePrecondition, "conflict");
    }

    [Fact]
    public async Task AssignCategory_replay_preserves_prior_result()
    {
        var cat = await CreateCategoryAsync("Replay");
        var tx = await RecordAsync('f');
        var preflight = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [tx.TransactionId]);
        var item = Assert.Single(preflight.Value!.ClassificationItems!);
        var input = new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1);
        var key = NextKey();

        var first = await client.AssignCategoryAsync(input, "1.0", actor, key, CancellationToken.None);
        var replay = await client.AssignCategoryAsync(input, "1.0", actor, key, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            JsonSerializer.Serialize(first.Value, LedgerJsonContext.Default.CategoryAllocationResult),
            JsonSerializer.Serialize(replay.Value, LedgerJsonContext.Default.CategoryAllocationResult));
    }

    [Fact]
    public async Task CorrectCategory_with_matching_preconditions_succeeds()
    {
        var first = await CreateCategoryAsync("From");
        var second = await CreateCategoryAsync("To");
        var tx = await RecordAsync('g');
        await AssignLegacyAsync(tx.TransactionId, first.CategoryId, "initial");
        var preflight = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [tx.TransactionId]);
        var item = Assert.Single(preflight.Value!.ClassificationItems!);

        var result = await client.CorrectCategoryAsync(
            new CorrectCategoryInput(
                tx.TransactionId,
                second.CategoryId,
                "owner corrected",
                ExpectedActiveAllocationId: item.CurrentAllocationId,
                ExpectedTransactionRevision: item.TransactionRevision,
                ExpectedRelationshipRevision: item.RelationshipRevision,
                ExpectedAllocationRevision: item.AllocationRevision,
                MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(second.CategoryId, result.Value!.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task CorrectCategory_preserves_stale_allocation_error_without_translation()
    {
        var first = await CreateCategoryAsync("Orig");
        var second = await CreateCategoryAsync("Next");
        var tx = await RecordAsync('h');
        await AssignLegacyAsync(tx.TransactionId, first.CategoryId, "initial");
        var preflight = await client.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            "1.0",
            actor,
            CancellationToken.None,
            transactionIds: [tx.TransactionId]);
        var item = Assert.Single(preflight.Value!.ClassificationItems!);

        var result = await client.CorrectCategoryAsync(
            new CorrectCategoryInput(
                tx.TransactionId,
                second.CategoryId,
                "stale",
                ExpectedActiveAllocationId: LedgerId.New().ToString(),
                ExpectedTransactionRevision: item.TransactionRevision,
                ExpectedRelationshipRevision: item.RelationshipRevision,
                ExpectedAllocationRevision: item.AllocationRevision,
                MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

        AssertError(result, 5, CategoryMutationPreconditionCodes.StalePrecondition, "conflict");
    }

    [Fact]
    public async Task AssignCategory_rejects_unsupported_version_before_mutation()
    {
        var cat = await CreateCategoryAsync("Version");
        var tx = await RecordAsync('i');
        var result = await client.AssignCategoryAsync(
            new AssignCategoryInput(tx.TransactionId, cat.CategoryId, "owner"),
            "2.0",
            actor,
            NextKey(),
            CancellationToken.None);

        AssertError(result, 7, "contract.incompatible", "compatibility");
        var after = await client.GetTransactionAsync(tx.TransactionId, "1.0", actor, CancellationToken.None);
        Assert.True(after.IsSuccess);
        Assert.Null(after.Value!.Category.CategoryId);
    }

    [Fact]
    public async Task AssignCategory_preserves_incompatible_mutation_contract_error()
    {
        var cat = await CreateCategoryAsync("MutVer");
        var tx = await RecordAsync('j');
        var result = await client.AssignCategoryAsync(
            new AssignCategoryInput(
                tx.TransactionId,
                cat.CategoryId,
                "owner",
                MutationContractVersion: "classification_v0"),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

        AssertError(result, 7, CategoryMutationPreconditionCodes.ContractMismatch, "compatibility");
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryClassificationProjection_honors_cancellation_token()
    {
        await RecordAsync('k');
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.QueryClassificationProjectionAsync(
                ClassificationProjectionPurpose.Evaluation, "1.0", actor, cts.Token));
    }

    [Fact]
    public async Task AssignCategory_honors_cancellation_token()
    {
        var cat = await CreateCategoryAsync("Cancel");
        var tx = await RecordAsync('l');
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AssignCategoryAsync(
                new AssignCategoryInput(tx.TransactionId, cat.CategoryId, "owner"),
                "1.0",
                actor,
                NextKey(),
                cts.Token));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var input = new CreateAccountInput(
            "Client Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR");
        return await RunSuccess(
            "ledger.account.create",
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput),
            NextKey(),
            LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategoryAsync(string name)
    {
        var input = new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]);
        return await RunSuccess(
            "ledger.category.create",
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCategoryInput),
            NextKey(),
            LedgerJsonContext.Default.CategoryDetail);
    }

    private async Task ArchiveCategoryAsync(string categoryId) =>
        await RunSuccess(
            "ledger.category.archive",
            JsonSerializer.SerializeToElement(new ArchiveCategoryInput(categoryId, "archive"), LedgerJsonContext.Default.ArchiveCategoryInput),
            NextKey(),
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> RecordAsync(char digest, string amount = "-12.34")
    {
        var digestText = string.Concat(Enumerable.Repeat(((byte)digest).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", "2026-07-15", null, "Client purchase " + digest + Guid.NewGuid().ToString("N")[..4], null, null,
            new RegisterEvidenceInput(EvidenceKind.AgentCapture, digestText, "client-capture:" + digest + ":" + Guid.NewGuid().ToString("N")[..8], null, null));
        return await RunSuccess(
            "ledger.transaction.record",
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput),
            NextKey(),
            LedgerJsonContext.Default.TransactionDetail);
    }

    private Task AssignLegacyAsync(string transactionId, string categoryId, string reason) =>
        client.AssignCategoryAsync(
            new AssignCategoryInput(transactionId, categoryId, reason),
            "1.0",
            actor,
            NextKey(),
            CancellationToken.None);

    private async Task<T> RunSuccess<T>(
        string operationId,
        JsonElement input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var descriptor = registry.Find(operationId)!;
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, input, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        var result = await process.RunAsync(args, body, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("result").GetRawText(), typeInfo)!;
    }

    private static void AssertError<T>(LedgerContractResult<T> result, int exitCode, string errorCode, string category)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.NotNull(result.Error);
        Assert.Equal(errorCode, result.Error!.Code);
        Assert.Equal(category, result.Error.Category);
        Assert.Equal(default, result.Value);
    }

    private string NextKey() => "classify-client-" + Interlocked.Increment(ref keySeq).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
