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

namespace Tally.Tests.Budget.LedgerContract;

/// <summary>
/// TC-BUDGET-LEDGER-COMPOSITION-CONTRACT / FR-BUDGET-LEDGER-COMPOSITION
/// Proves released ledger.actuals.query evidence BUDGET may consume for positions.
/// Public surface only — no LedgerDb / private SQL.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerBudgetActualsProjectionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-act-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private string accountId = null!;
    private string foodId = null!;
    private string travelId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var db = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(db));
        accountId = (await CreateAccount()).AccountId;
        foodId = (await CreateCategory("Food")).CategoryId;
        travelId = (await CreateCategory("Travel")).CategoryId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void Registry_exposes_actuals_query_with_compatibility_range()
    {
        var op = OperationRegistry.Create().Find("ledger.actuals.query")!;
        Assert.Equal("1.0", op.MinimumContractVersion);
        Assert.Equal(typeof(ActualsQueryResult), op.ResultTypeInfo.Type);
        Assert.Equal(typeof(QueryActualsInput), op.RequestTypeInfo.Type);
    }

    [Fact]
    public async Task Inclusive_date_filter_returns_boundary_days()
    {
        await Record("-10.00", "2026-07-01", "start");
        await Record("-20.00", "2026-07-15", "mid");
        await Record("-30.00", "2026-07-31", "end");
        await Record("-40.00", "2026-08-01", "after");

        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, item => Assert.True(item.EffectiveDate is "2026-07-01" or "2026-07-15" or "2026-07-31"));
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
    }

    [Fact]
    public async Task Snapshot_items_expose_identity_ordinal_and_nullable_category()
    {
        var uncategorized = await Record("-5.00", "2026-07-10", "uncat");
        var categorized = await Record("-7.00", "2026-07-11", "cat");
        await AssignCategory(categorized.TransactionId, foodId);

        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(Enumerable.Range(0, 2), page.Items.Select(i => i.Ordinal));
        var withCat = page.Items.Single(i => i.TransactionId == categorized.TransactionId);
        var without = page.Items.Single(i => i.TransactionId == uncategorized.TransactionId);
        Assert.Equal(foodId, withCat.CategoryId);
        Assert.Equal(TransactionCategoryState.Categorized, withCat.CategoryState);
        Assert.Null(without.CategoryId);
        Assert.Equal(TransactionCategoryState.Uncategorized, without.CategoryState);
    }

    [Fact]
    public async Task Full_set_budget_actual_total_is_exact_signed_money_string()
    {
        await Record("-12.50", "2026-07-05", "a");
        await Record("-7.50", "2026-07-06", "b");
        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Equal("20", page.Totals.BudgetActual);
        Assert.Equal(page.Totals.BudgetActual, SumBudget(page.Items));
    }

    [Fact]
    public async Task Multi_page_shares_snapshot_generation_and_full_totals()
    {
        for (var i = 0; i < 5; i++)
        {
            await Record($"-{(i + 1)}.00", "2026-07-0" + (i + 1), "row-" + i);
        }

        var first = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 2);
        Assert.NotNull(first.Cursor);
        var second = await Continue(first.Cursor!);
        var third = second.Cursor is null ? second : await Continue(second.Cursor!);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.StoreGenerationFingerprint, second.StoreGenerationFingerprint);
        Assert.Equal(first.Totals, second.Totals);
        Assert.Equal(first.LedgerContractVersion, second.LedgerContractVersion);
        var ids = first.Items.Concat(second.Items).Concat(third.Cursor is null && third.SnapshotId == first.SnapshotId ? third.Items : []).Select(i => i.TransactionId).Distinct().ToArray();
        // Drain remaining pages
        var pages = new List<ActualsQueryResult> { first, second };
        var cursor = second.Cursor;
        while (cursor is not null)
        {
            var next = await Continue(cursor);
            pages.Add(next);
            cursor = next.Cursor;
        }

        Assert.All(pages, p => Assert.Equal(first.SnapshotId, p.SnapshotId));
        Assert.All(pages, p => Assert.Equal(first.Totals.BudgetActual, p.Totals.BudgetActual));
        var allIds = pages.SelectMany(p => p.Items).Select(i => i.TransactionId).ToArray();
        Assert.Equal(allIds.Length, allIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, allIds.Length);
        Assert.Equal(Enumerable.Range(0, 5).Order(), pages.SelectMany(p => p.Items).Select(i => i.Ordinal).Order());
    }

    [Fact]
    public async Task Active_lifecycle_filter_excludes_voided_transactions()
    {
        var keep = await Record("-9.00", "2026-07-12", "keep");
        var doomed = await Record("-11.00", "2026-07-13", "void-me");
        await Void(doomed.TransactionId);

        var page = await Query(new ActualsFilterInput(
            EffectiveFrom: "2026-07-01",
            EffectiveTo: "2026-07-31",
            LifecycleStates: [TransactionLifecycleStatus.Active]), 50);
        Assert.Contains(page.Items, i => i.TransactionId == keep.TransactionId);
        Assert.DoesNotContain(page.Items, i => i.TransactionId == doomed.TransactionId);
    }

    [Fact]
    public async Task Archived_category_assignment_retains_stable_category_id_on_actuals()
    {
        var tx = await Record("-15.00", "2026-07-14", "archive-cat");
        await AssignCategory(tx.TransactionId, travelId);
        await ArchiveCategory(travelId);

        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        var item = page.Items.Single(i => i.TransactionId == tx.TransactionId);
        Assert.Equal(travelId, item.CategoryId);
        Assert.Equal(TransactionCategoryState.Categorized, item.CategoryState);

        // Lifecycle evidence for BUDGET comes from released category.get (composition, not private store).
        var category = await GetCategory(travelId);
        Assert.Equal(CategoryStatus.Archived, category.Status);
        Assert.Equal(travelId, category.CategoryId);
    }

    [Fact]
    public async Task Owned_transfer_principal_is_excluded_from_budget_actual()
    {
        var other = await CreateAccount("Other");
        var outflow = await Record("-50.00", "2026-07-20", "transfer-out");
        var inflow = await Record("50.00", "2026-07-20", "transfer-in", other.AccountId);
        await ConfirmTransfer(outflow.TransactionId, inflow.TransactionId);

        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        // Transfer principal must not inflate BudgetActual (owned-transfer exclusion).
        Assert.Equal("0", page.Totals.BudgetActual);
    }

    [Fact]
    public async Task Full_refund_nets_budget_actual_to_zero()
    {
        // LEDGER refunds are full-amount; credit nets the original purchase contribution exactly.
        var spend = await Record("-100.00", "2026-07-21", "purchase");
        var credit = await Record("100.00", "2026-07-22", "refund");
        await ConfirmRefund(spend.TransactionId, credit.TransactionId);

        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Equal("0", page.Totals.BudgetActual);
    }

    [Fact]
    public async Task Expired_cursor_fails_atomically_without_partial_page()
    {
        await Record("-1.00", "2026-07-01", "seed-a");
        await Record("-2.00", "2026-07-02", "seed-b");
        var first = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 1);
        Assert.NotNull(first.Cursor);

        // Tamper expiry in cursor payload to force SnapshotExpired.
        var tampered = TamperCursorExpiry(first.Cursor!, DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = await RunRaw("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.SnapshotExpired, ErrorCode(result));
    }

    [Fact]
    public async Task Contract_version_mismatch_on_cursor_fails_compatibility()
    {
        await Record("-1.00", "2026-07-03", "seed");
        await Record("-2.00", "2026-07-04", "seed2");
        var first = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 1);
        var tampered = TamperCursorContract(first.Cursor!, "9.9");
        var result = await RunRaw("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.ContractMismatch, ErrorCode(result));
    }

    [Fact]
    public async Task Generation_mismatch_fails_when_fingerprint_tampered()
    {
        await Record("-1.00", "2026-07-05", "g1");
        await Record("-2.00", "2026-07-06", "g2");
        var first = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 1);
        var tampered = TamperCursorGeneration(first.Cursor!, "deadbeef");
        var result = await RunRaw("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.GenerationMismatch, ErrorCode(result));
    }

    [Fact]
    public async Task Filter_on_later_page_is_rejected()
    {
        await Record("-1.00", "2026-07-07", "f1");
        await Record("-2.00", "2026-07-08", "f2");
        var first = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 1);
        var result = await RunRaw(
            "ledger.actuals.query",
            new QueryActualsInput(new ActualsFilterInput(EffectiveFrom: "2026-07-01"), Cursor: first.Cursor),
            null);
        Assert.Equal(ActualsErrors.CursorFilterMismatch, ErrorCode(result));
    }

    [Fact]
    public async Task Invalid_date_filter_is_stable_validation_error()
    {
        var result = await RunRaw(
            "ledger.actuals.query",
            new QueryActualsInput(new ActualsFilterInput(EffectiveFrom: "not-a-date")),
            null);
        Assert.Equal(ActualsErrors.InvalidFilter, ErrorCode(result));
    }

    [Fact]
    public async Task Categorization_any_includes_categorized_and_uncategorized()
    {
        var a = await Record("-3.00", "2026-07-09", "u");
        var b = await Record("-4.00", "2026-07-09", "c");
        await AssignCategory(b.TransactionId, foodId);
        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Contains(page.Items, i => i.TransactionId == a.TransactionId && i.CategoryId is null);
        Assert.Contains(page.Items, i => i.TransactionId == b.TransactionId && i.CategoryId == foodId);
    }

    [Fact]
    public async Task First_page_exposes_generation_and_contract_without_cursor_decode()
    {
        await Record("-1.00", "2026-07-10", "meta");
        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2026-07-01", EffectiveTo: "2026-07-31"), 50);
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.Equal(64, page.StoreGenerationFingerprint!.Length);
    }

    [Fact]
    public async Task Empty_period_returns_zero_total_and_empty_items()
    {
        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2025-01-01", EffectiveTo: "2025-01-31"), 50);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Equal("0", page.Totals.BudgetActual);
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
    }

    private async Task<AccountDetail> CreateAccount(string? name = null)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var masked = $"****{Random.Shared.Next(1000, 9999)}";
        return Success(await RunRaw(
            "ledger.account.create",
            new CreateAccountInput(name is null ? $"Budget Bank {unique}" : $"{name} {unique}", $"Primary-{unique}", AccountType.Cheque, masked, "ZAR"),
            NextKey()), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name) =>
        Success(await RunRaw("ledger.category.create", new CreateCategoryInput(name), NextKey()), LedgerJsonContext.Default.CategoryDetail);

    private async Task<CategoryDetail> GetCategory(string id) =>
        Success(await RunRaw("ledger.category.get", new GetCategoryInput(id), null), LedgerJsonContext.Default.CategoryDetail);

    private async Task ArchiveCategory(string id) =>
        _ = Success(await RunRaw("ledger.category.archive", new ArchiveCategoryInput(id, "budget"), NextKey()), LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> Record(string amount, string date, string description, string? account = null)
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(description + date + amount + Guid.NewGuid().ToString("N"))));
        return Success(await RunRaw(
            "ledger.transaction.record",
            new RecordTransactionInput(account ?? accountId, amount, "ZAR", date, null, description, null, null, new(EvidenceKind.AgentCapture, digest, null, null, null)),
            "record-" + digest[..16]), LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task AssignCategory(string transactionId, string categoryId) =>
        _ = Success(await RunRaw(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, "budget"),
            "cat-" + transactionId), LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task Void(string transactionId) =>
        _ = Success(await RunRaw(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-void"),
            "void-" + transactionId), TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task ConfirmTransfer(string outflowId, string inflowId) =>
        _ = Success(await RunRaw(
            "ledger.transfer.confirm",
            new ConfirmTransferInput(outflowId, inflowId, "budget-transfer"),
            "xfer-" + outflowId), LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task ConfirmRefund(string originalId, string creditId) =>
        _ = Success(await RunRaw(
            "ledger.refund.confirm",
            new ConfirmRefundInput(originalId, creditId, "budget-refund"),
            "refund-" + originalId), LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task<ActualsQueryResult> Query(ActualsFilterInput filter, int pageSize) =>
        Success(await RunRaw("ledger.actuals.query", new QueryActualsInput(filter, pageSize), null), ActualsJsonContext.Default.ActualsQueryResult);

    private async Task<ActualsQueryResult> Continue(string cursor) =>
        Success(await RunRaw("ledger.actuals.query", new QueryActualsInput(Cursor: cursor), null), ActualsJsonContext.Default.ActualsQueryResult);

    private async Task<ProcessResult> RunRaw<T>(string operationId, T input, string? key)
    {
        var descriptor = OperationRegistry.Create().Find(operationId)!;
        var element = JsonSerializer.SerializeToElement(input!, descriptor.RequestTypeInfo);
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new("human", "budget-act"), element, key), LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private string NextKey() => $"budget-act-{Interlocked.Increment(ref keySeq)}";

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static string ErrorCode(ProcessResult result) =>
        JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code;

    private static string SumBudget(IEnumerable<ActualsPageItem> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            Assert.True(Money.TryParse(item.Contribution.BudgetActual, out var money, out _), item.Contribution.BudgetActual);
            total = checked(total + money.MinorUnits);
        }
        return Money.FromMinorUnits(total).ToString();
    }

    private static string TamperCursorExpiry(string cursor, DateTimeOffset expiry)
    {
        var payload = Decode(cursor);
        var next = payload with { ExpiresAt = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) };
        return Encode(next);
    }

    private static string TamperCursorContract(string cursor, string version)
    {
        var payload = Decode(cursor);
        return Encode(payload with { ContractVersion = version });
    }

    private static string TamperCursorGeneration(string cursor, string generation)
    {
        var payload = Decode(cursor);
        return Encode(payload with { GenerationFingerprint = generation });
    }

    private static ActualsCursorPayload Decode(string value)
    {
        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return JsonSerializer.Deserialize(Convert.FromBase64String(encoded), ActualsJsonContext.Default.ActualsCursorPayload)!;
    }

    private static string Encode(ActualsCursorPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ActualsJsonContext.Default.ActualsCursorPayload);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
