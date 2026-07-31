using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Classify.LedgerContract;

/// <summary>
/// TC-CLASSIFY-ELIGIBLE-PROJECTION-CONTRACT / DD-CLASSIFY-LEDGER-PUBLIC-PROJECTION
/// Purpose-scoped classification_v1 projection on ledger.actuals.query.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerClassificationProjectionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-proj-" + Guid.NewGuid().ToString("N"));
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    // ── Descriptor / version validation ──────────────────────────────────────

    [Fact]
    public void TC_CLASSIFY_ELIGIBLE_PROJECTION_actuals_descriptor_is_released()
    {
        var registry = OperationRegistry.Create();
        var descriptor = registry.Find("ledger.actuals.query");
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(QueryActualsInput), descriptor!.RequestTypeInfo.Type);
        Assert.Equal(typeof(ActualsQueryResult), descriptor.ResultTypeInfo.Type);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_incompatible_item_projection_fails_before_read()
    {
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: "classification_v0"));
        AssertError(result, 7, ActualsErrors.ContractMismatch);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_missing_item_projection_fails_compatibility()
    {
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: null));
        AssertError(result, 7, ActualsErrors.ContractMismatch);
    }

    // ── Evaluation purpose ───────────────────────────────────────────────────

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_evaluation_returns_only_uncategorized_active()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Food");
        var uncat = await Record(account.AccountId, 'a');
        var categorized = await Record(account.AccountId, 'b');
        await Assign(categorized.TransactionId, cat.CategoryId, "owner", "assign-b");

        var page = Success(await Query(EvaluationRequest()));
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.ProjectionVersion);
        Assert.NotNull(page.ClassificationItems);
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == uncat.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == categorized.TransactionId);
        Assert.All(page.ClassificationItems!, item =>
        {
            Assert.Equal(CategoryMutationState.Assignable, item.CategoryMutationState);
            Assert.Null(item.CurrentCategoryId);
            Assert.Equal("none", item.AllocationRevision);
        });
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_evaluation_excludes_voided_transactions()
    {
        var account = await CreateAccount();
        var active = await Record(account.AccountId, 'c');
        var voided = await Record(account.AccountId, 'd');
        await Void(voided.TransactionId);

        var page = Success(await Query(EvaluationRequest()));
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == active.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == voided.TransactionId);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_evaluation_excludes_transfer_principal_legs()
    {
        var a = await CreateAccount("Bank A", "****1111");
        var b = await CreateAccount("Bank B", "****2222");
        var outflow = await Record(a.AccountId, 'e', "-10.00");
        var inflow = await Record(b.AccountId, 'f', "10.00");
        await ConfirmTransfer(outflow.TransactionId, inflow.TransactionId);
        var plain = await Record(a.AccountId, 'g');

        var page = Success(await Query(EvaluationRequest()));
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == outflow.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == inflow.TransactionId);
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == plain.TransactionId);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_evaluation_excludes_linked_refund_credit()
    {
        var account = await CreateAccount();
        var original = await Record(account.AccountId, 'h', "-25.00");
        var credit = await Record(account.AccountId, 'i', "25.00");
        await ConfirmRefund(original.TransactionId, credit.TransactionId);
        var plain = await Record(account.AccountId, 'j');

        var page = Success(await Query(EvaluationRequest()));
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == credit.TransactionId);
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == plain.TransactionId);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_first_page_carries_descriptor_fields()
    {
        var account = await CreateAccount();
        await CreateCategory("Travel");
        await Record(account.AccountId, 'k');

        var page = Success(await Query(EvaluationRequest()));
        Assert.False(string.IsNullOrWhiteSpace(page.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(page.ExpiresAt));
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.Equal(QuerySnapshotStoreContractVersion(), page.LedgerContractVersion);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.ProjectionVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.CategoryIdentityLifecycleFingerprint));
        Assert.NotNull(page.ActiveCategories);
        Assert.Contains(page.ActiveCategories!, c => c.LifecycleState == "active");
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_pagination_preserves_snapshot_order_and_dense_ordinals()
    {
        var account = await CreateAccount();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var tx = await Record(account.AccountId, (char)('A' + i));
            ids.Add(tx.TransactionId);
        }

        var first = Success(await Query(EvaluationRequest(pageSize: 2)));
        Assert.Equal(2, first.ClassificationItems!.Count);
        Assert.NotNull(first.Cursor);
        Assert.Equal(5, first.TotalCount);

        var second = Success(await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: first.Cursor,
            PageSize: null)));
        Assert.Equal(2, second.ClassificationItems!.Count);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.TotalCount, second.TotalCount);

        var third = Success(await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: second.Cursor)));
        Assert.Single(third.ClassificationItems!);
        Assert.Null(third.Cursor);

        var allOrdinals = first.ClassificationItems!
            .Concat(second.ClassificationItems!)
            .Concat(third.ClassificationItems!)
            .Select(item => item.Ordinal)
            .Order()
            .ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, allOrdinals);
        Assert.Equal(
            5,
            first.ClassificationItems!.Concat(second.ClassificationItems!).Concat(third.ClassificationItems!)
                .Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_evaluation_rejects_transaction_ids()
    {
        var account = await CreateAccount();
        var tx = await Record(account.AccountId, 'L');
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: [tx.TransactionId]));
        AssertError(result, 3, ActualsErrors.InvalidFilter);
    }

    // ── Apply preflight purpose ──────────────────────────────────────────────

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_apply_preflight_returns_every_selected_id()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Bills");
        var uncat = await Record(account.AccountId, 'm');
        var catTx = await Record(account.AccountId, 'n');
        await Assign(catTx.TransactionId, cat.CategoryId, "owner", "assign-n");

        var page = Success(await Query(PreflightRequest([uncat.TransactionId, catTx.TransactionId])));
        Assert.Equal(2, page.ClassificationItems!.Count);
        Assert.Null(page.MissingTransactionIds);
        var assignable = page.ClassificationItems.Single(item => item.TransactionId == uncat.TransactionId);
        var correctable = page.ClassificationItems.Single(item => item.TransactionId == catTx.TransactionId);
        Assert.Equal(CategoryMutationState.Assignable, assignable.CategoryMutationState);
        Assert.Equal(CategoryMutationState.Correctable, correctable.CategoryMutationState);
        Assert.Equal(cat.CategoryId, correctable.CurrentCategoryId);
        Assert.NotNull(correctable.CurrentAllocationId);
        Assert.NotEqual("none", correctable.AllocationRevision);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_apply_preflight_reports_missing_ids()
    {
        var account = await CreateAccount();
        var real = await Record(account.AccountId, 'o');
        var missingId = LedgerId.New().ToString();

        var page = Success(await Query(PreflightRequest([real.TransactionId, missingId])));
        Assert.Single(page.ClassificationItems!);
        Assert.Equal([missingId], page.MissingTransactionIds);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_apply_preflight_requires_bounded_ids()
    {
        var empty = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: []));
        AssertError(empty, 3, ActualsErrors.InvalidFilter);

        var tooMany = Enumerable.Range(0, ClassificationProjectionVersions.MaxApplyPreflightIds + 1)
            .Select(_ => LedgerId.New().ToString())
            .ToArray();
        var oversized = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: tooMany));
        AssertError(oversized, 3, ActualsErrors.InvalidFilter);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_apply_preflight_marks_voided_ineligible()
    {
        var account = await CreateAccount();
        var voided = await Record(account.AccountId, 'p');
        await Void(voided.TransactionId);

        var page = Success(await Query(PreflightRequest([voided.TransactionId])));
        var item = Assert.Single(page.ClassificationItems!);
        Assert.Equal(CategoryMutationState.Ineligible, item.CategoryMutationState);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_apply_preflight_includes_revisions()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Office");
        var tx = await Record(account.AccountId, 'q');
        await Assign(tx.TransactionId, cat.CategoryId, "owner", "assign-q");

        var page = Success(await Query(PreflightRequest([tx.TransactionId])));
        var item = Assert.Single(page.ClassificationItems!);
        Assert.False(string.IsNullOrWhiteSpace(item.TransactionRevision));
        Assert.False(string.IsNullOrWhiteSpace(item.RelationshipRevision));
        Assert.False(string.IsNullOrWhiteSpace(item.AllocationRevision));
        Assert.NotEqual("none", item.AllocationRevision);
    }

    [Fact]
    public async Task TC_CLASSIFY_ELIGIBLE_PROJECTION_active_catalogue_excludes_archived()
    {
        var account = await CreateAccount();
        var active = await CreateCategory("ActiveCat");
        var archived = await CreateCategory("ArchivedCat");
        await ArchiveCategory(archived.CategoryId);
        await Record(account.AccountId, 'r');

        var page = Success(await Query(EvaluationRequest()));
        Assert.Contains(page.ActiveCategories!, c => c.CategoryId == active.CategoryId);
        Assert.DoesNotContain(page.ActiveCategories!, c => c.CategoryId == archived.CategoryId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QueryActualsInput EvaluationRequest(int? pageSize = null) => new(
        Purpose: ClassificationProjectionPurpose.Evaluation,
        ItemProjection: ClassificationProjectionVersions.ClassificationV1,
        PageSize: pageSize);

    private static QueryActualsInput PreflightRequest(IReadOnlyList<string> ids) => new(
        Purpose: ClassificationProjectionPurpose.ApplyPreflight,
        ItemProjection: ClassificationProjectionVersions.ClassificationV1,
        TransactionIds: ids);

    private static string QuerySnapshotStoreContractVersion() => "1.0";

    private async Task<AccountDetail> CreateAccount(string bank = "Test Bank", string mask = "****1234")
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var input = new CreateAccountInput(bank + " " + unique, "Primary-" + unique, AccountType.Cheque, mask, "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), NextKey()), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name)
    {
        var input = new CreateCategoryInput(name);
        return Success(await Run("ledger.category.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCategoryInput), NextKey()), LedgerJsonContext.Default.CategoryDetail);
    }

    private Task<ProcessResult> ArchiveCategory(string categoryId) =>
        Run("ledger.category.archive", JsonSerializer.SerializeToElement(new ArchiveCategoryInput(categoryId, "archive"), LedgerJsonContext.Default.ArchiveCategoryInput), NextKey());

    private async Task<TransactionDetail> Record(string accountId, char digest, string amount = "-12.34")
    {
        var digestText = string.Concat(Enumerable.Repeat(((byte)digest).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", "2026-07-15", null, "Owner-safe purchase " + digest, null, null,
            new RegisterEvidenceInput(EvidenceKind.AgentCapture, digestText, "capture:" + digest, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), NextKey()), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Assign(string transactionId, string categoryId, string reason, string key) =>
        Run("ledger.transaction.category.assign",
            JsonSerializer.SerializeToElement(new AssignCategoryInput(transactionId, categoryId, reason), LedgerJsonContext.Default.AssignCategoryInput),
            key);

    private Task<ProcessResult> Void(string transactionId) =>
        Run("ledger.transaction.void",
            JsonSerializer.SerializeToElement(new VoidTransactionInput(transactionId, "void for test"), TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());

    private Task<ProcessResult> ConfirmTransfer(string outflowId, string inflowId) =>
        Run("ledger.transfer.confirm",
            JsonSerializer.SerializeToElement(new ConfirmTransferInput(outflowId, inflowId, "owner transfer"), LedgerJsonContext.Default.ConfirmTransferInput),
            NextKey());

    private Task<ProcessResult> ConfirmRefund(string originalId, string creditId) =>
        Run("ledger.refund.confirm",
            JsonSerializer.SerializeToElement(new ConfirmRefundInput(originalId, creditId, "owner refund"), LedgerJsonContext.Default.ConfirmRefundInput),
            NextKey());

    private Task<ProcessResult> Query(QueryActualsInput input) =>
        Run("ledger.actuals.query", JsonSerializer.SerializeToElement(input, ActualsJsonContext.Default.QueryActualsInput), key: null);

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var descriptor = OperationRegistry.Create().Find(operationId)!;
        var actor = new SafeActor("human", "classify-proj-test", "run-01");
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, input, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private static ActualsQueryResult Success(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("result").GetRawText(), ActualsJsonContext.Default.ActualsQueryResult)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("result").GetRawText(), typeInfo)!;
    }

    private string NextKey() => "classify-proj-" + Interlocked.Increment(ref keySeq).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
