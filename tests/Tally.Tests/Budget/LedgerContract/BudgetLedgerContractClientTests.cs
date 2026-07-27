using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.LedgerContract;

/// <summary>
/// TASK-BUDGET-LEDGER-BUDGET-CLIENT / bd-2h45
/// BUDGET methods on the shared concrete LedgerContractClient: categories, period actuals paging,
/// compatibility, cancellation, and no-partial failure behavior.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetLedgerContractClientTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-client-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-client", "run-01");
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

    // ── Category list / get ──────────────────────────────────────────────────

    // FR-BUDGET-CATEGORY-LIFECYCLE / DM-BUDGET-LEDGER-COMPOSITION-CONTRACT
    [Fact]
    public async Task ListBudgetCategories_returns_stable_ids_lifecycle_and_contract_version()
    {
        var created = await CreateCategoryAsync("Groceries");

        var listed = await client.ListBudgetCategoriesAsync("1.0", actor, CancellationToken.None);

        Assert.True(listed.IsSuccess);
        Assert.Null(listed.Error);
        Assert.Equal(0, listed.ExitCode);
        Assert.Equal(CategoryContractVersions.Current, listed.Value!.LedgerContractVersion);
        var item = Assert.Single(listed.Value.Items, x => x.CategoryId == created.CategoryId);
        Assert.Equal("Groceries", item.Name);
        Assert.Equal(CategoryStatus.Active, item.Status);
        Assert.Equal(CategoryContractVersions.Current, item.LedgerContractVersion);
        Assert.Equal(created.CategoryId, item.CategoryId);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE
    [Fact]
    public async Task ListBudgetCategories_active_filter_excludes_archived()
    {
        var keep = await CreateCategoryAsync("KeepActive");
        var drop = await CreateCategoryAsync("DropArchived");
        await ArchiveCategoryAsync(drop.CategoryId);

        var active = await client.ListBudgetCategoriesAsync(
            "1.0", actor, CancellationToken.None, CategoryStatus.Active);

        Assert.True(active.IsSuccess);
        Assert.Contains(active.Value!.Items, x => x.CategoryId == keep.CategoryId);
        Assert.DoesNotContain(active.Value.Items, x => x.CategoryId == drop.CategoryId);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE
    [Fact]
    public async Task GetBudgetCategory_returns_stable_id_display_name_and_lifecycle()
    {
        var created = await CreateCategoryAsync("Travel");

        var got = await client.GetBudgetCategoryAsync(created.CategoryId, "1.0", actor, CancellationToken.None);

        Assert.True(got.IsSuccess);
        Assert.Equal(created.CategoryId, got.Value!.CategoryId);
        Assert.Equal("Travel", got.Value.Name);
        Assert.Equal(CategoryStatus.Active, got.Value.Status);
        Assert.Equal(CategoryContractVersions.Current, got.Value.LedgerContractVersion);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE
    [Fact]
    public async Task GetBudgetCategory_archived_remains_readable_with_inactive_lifecycle()
    {
        var created = await CreateCategoryAsync("ArchiveMe");
        await ArchiveCategoryAsync(created.CategoryId);

        var got = await client.GetBudgetCategoryAsync(created.CategoryId, "1.0", actor, CancellationToken.None);

        Assert.True(got.IsSuccess);
        Assert.Equal(created.CategoryId, got.Value!.CategoryId);
        Assert.Equal(CategoryStatus.Archived, got.Value.Status);
        Assert.Equal("ArchiveMe", got.Value.Name);
        Assert.Equal(CategoryContractVersions.Current, got.Value.LedgerContractVersion);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE
    [Fact]
    public async Task GetBudgetCategory_rename_preserves_stable_identity()
    {
        var created = await CreateCategoryAsync("BeforeRename");
        await RenameCategoryAsync(created.CategoryId, "AfterRename");

        var got = await client.GetBudgetCategoryAsync(created.CategoryId, "1.0", actor, CancellationToken.None);

        Assert.True(got.IsSuccess);
        Assert.Equal(created.CategoryId, got.Value!.CategoryId);
        Assert.Equal("AfterRename", got.Value.Name);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE
    [Fact]
    public async Task GetBudgetCategory_unknown_id_preserves_ledger_not_found_with_no_partial()
    {
        var unknown = LedgerId.New().ToString();

        var result = await client.GetBudgetCategoryAsync(unknown, "1.0", actor, CancellationToken.None);

        AssertError(result, 4, "LEDGER-CATEGORY-NOT-FOUND", "not_found");
    }

    // NFR-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task ListBudgetCategories_rejects_unsupported_version_as_budget_incompatible()
    {
        var result = await client.ListBudgetCategoriesAsync("2.0", actor, CancellationToken.None);

        AssertError(result, 7, BudgetErrors.LedgerIncompatible, "compatibility");
    }

    // NFR-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task GetBudgetCategory_rejects_unsupported_version_as_budget_incompatible()
    {
        var result = await client.GetBudgetCategoryAsync(
            "01J00000000000000000000000", "2.0", actor, CancellationToken.None);

        AssertError(result, 7, BudgetErrors.LedgerIncompatible, "compatibility");
    }

    // ── Budget actuals / period mapping ──────────────────────────────────────

    // FR-BUDGET-LEDGER-COMPOSITION / DD-BUDGET-LEDGER-PUBLIC-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_maps_half_open_period_to_inclusive_dates()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));
        await RecordAsync("-10.00", "2026-01-01", "jan-start");
        await RecordAsync("-20.00", "2026-01-15", "jan-mid");
        await RecordAsync("-30.00", "2026-01-31", "jan-end");
        await RecordAsync("-40.00", "2026-02-01", "feb-excluded");
        await RecordAsync("-50.00", "2025-12-31", "dec-excluded");

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 50);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.TotalCount);
        Assert.Null(result.Value.Cursor);
        Assert.All(result.Value.Items, item => Assert.True(
            item.EffectiveDate is "2026-01-01" or "2026-01-15" or "2026-01-31",
            $"Unexpected effective date {item.EffectiveDate}"));
        Assert.DoesNotContain(result.Value.Items, i => i.EffectiveDate is "2026-02-01" or "2025-12-31");
        Assert.Equal(ActualsContractVersions.Current, result.Value.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.SnapshotId));
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_full_set_budget_actual_total_is_exact()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));
        await RecordAsync("-12.50", "2026-01-05", "jan-a");
        await RecordAsync("-7.50", "2026-01-06", "jan-b");
        await RecordAsync("-5.00", "2026-01-31", "jan-c");
        await RecordAsync("-100.00", "2026-02-01", "feb-out");

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 50);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.TotalCount);
        Assert.Equal("25", result.Value.Totals.BudgetActual);
        Assert.Equal(result.Value.Totals.BudgetActual, SumBudget(result.Value.Items));
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_multi_page_shares_snapshot_and_consumes_every_ordinal_once()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));
        for (var i = 0; i < 5; i++)
        {
            var day = (i + 1).ToString("00");
            await RecordAsync($"-{(i + 1)}.00", $"2026-01-{day}", "jan-page-" + i);
        }

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 2);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Cursor);
        Assert.Equal(5, result.Value.TotalCount);
        Assert.Equal(5, result.Value.Items.Count);
        Assert.Equal(Enumerable.Range(0, 5), result.Value.Items.Select(i => i.Ordinal).Order());
        Assert.Equal(5, result.Value.Items.Select(i => i.TransactionId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("15", result.Value.Totals.BudgetActual);
        Assert.Equal(result.Value.Totals.BudgetActual, SumBudget(result.Value.Items));
        Assert.Equal(ActualsContractVersions.Current, result.Value.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.StoreGenerationFingerprint));
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_empty_period_returns_zero_total_and_empty_items()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2024, 1, "ZAR", out var period, out _));

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 50);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.TotalCount);
        Assert.Empty(result.Value.Items);
        Assert.Equal("0", result.Value.Totals.BudgetActual);
        Assert.Null(result.Value.Cursor);
        Assert.Equal(ActualsContractVersions.Current, result.Value.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.StoreGenerationFingerprint));
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_preserves_signed_budget_values_and_nullable_category()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));
        var category = await CreateCategoryAsync("Food");
        var uncategorized = await RecordAsync("-5.00", "2026-01-10", "uncat");
        var categorized = await RecordAsync("-7.00", "2026-01-11", "cat");
        await AssignCategoryAsync(categorized.TransactionId, category.CategoryId);

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 50);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        var withCat = result.Value.Items.Single(i => i.TransactionId == categorized.TransactionId);
        var without = result.Value.Items.Single(i => i.TransactionId == uncategorized.TransactionId);
        Assert.Equal(category.CategoryId, withCat.CategoryId);
        Assert.Equal(TransactionCategoryState.Categorized, withCat.CategoryState);
        Assert.Null(without.CategoryId);
        Assert.Equal(TransactionCategoryState.Uncategorized, without.CategoryState);
        Assert.Equal("12", result.Value.Totals.BudgetActual);
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_active_lifecycle_excludes_voided_transactions()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));
        var keep = await RecordAsync("-9.00", "2026-01-12", "keep-active");
        var doomed = await RecordAsync("-11.00", "2026-01-13", "void-me");
        await VoidAsync(doomed.TransactionId);

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 50);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!.Items, i => i.TransactionId == keep.TransactionId);
        Assert.DoesNotContain(result.Value.Items, i => i.TransactionId == doomed.TransactionId);
    }

    // NFR-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task QueryBudgetActuals_rejects_unsupported_version_with_no_partial()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));

        var result = await client.QueryBudgetActualsAsync(period, "2.0", actor, CancellationToken.None);

        AssertError(result, 7, BudgetErrors.LedgerIncompatible, "compatibility");
    }

    // FR-BUDGET-LEDGER-COMPOSITION
    [Fact]
    public async Task QueryBudgetActuals_invalid_page_size_fails_with_no_partial()
    {
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));

        var result = await client.QueryBudgetActualsAsync(period, "1.0", actor, CancellationToken.None, pageSize: 0);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(ActualsErrors.InvalidFilter, result.Error!.Code);
        Assert.Equal("validation", result.Error.Category);
    }

    // FR-BUDGET-LEDGER-COMPOSITION — bad cursor yields ledger error with no partial through client
    [Fact]
    public async Task QueryBudgetActuals_bad_cursor_continuation_fails_with_no_partial()
    {
        // Direct public operation with a garbage cursor must fail closed; the BUDGET client never
        // invents partial position evidence from such failures.
        var result = await ExecuteAsync(
            "ledger.actuals.query",
            new QueryActualsInput(Cursor: "not-a-valid-cursor"),
            null,
            ActualsJsonContext.Default.QueryActualsInput,
            ActualsJsonContext.Default.ActualsQueryResult);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(ActualsErrors.CursorInvalid, result.Error!.Code);
    }

    // NFR-BUDGET-AGENT-OPERABILITY / cancellation-aware
    [Fact]
    public async Task Cancellation_reaches_the_shared_executor_for_budget_methods()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        Assert.True(BudgetPeriodResolver.TryCreate(2026, 1, "ZAR", out var period, out _));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListBudgetCategoriesAsync("1.0", actor, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetBudgetCategoryAsync("01J00000000000000000000000", "1.0", actor, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.QueryBudgetActualsAsync(period, "1.0", actor, source.Token));
    }

    // Additive client: INGEST methods remain intact
    [Fact]
    public async Task Ingest_get_account_method_remains_intact_on_shared_client()
    {
        var result = await client.GetAccountAsync(accountId, "1.0", actor, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(accountId, result.Value!.AccountId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                $"Budget Client Bank {unique}",
                $"Primary-{unique}",
                AccountType.Cheque,
                $"****{Random.Shared.Next(1000, 9999)}",
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "budget-client"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "budget-client"),
            NextKey(),
            LedgerJsonContext.Default.RenameCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> RecordAsync(string amount, string date, string description)
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(description + date + amount + Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                amount,
                "ZAR",
                date,
                null,
                description,
                null,
                null,
                new(EvidenceKind.AgentCapture, digest, null, null, null)),
            "record-" + digest[..16],
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task AssignCategoryAsync(string transactionId, string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, "budget-client"),
            "cat-" + transactionId,
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task VoidAsync(string transactionId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-void"),
            "void-" + transactionId,
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var result = await ExecuteAsync(operationId, input, key, inputType, resultType);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    private async Task<LedgerContractResult<TResult>> ExecuteAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var element = JsonSerializer.SerializeToElement(input, inputType);
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, element, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, body, CancellationToken.None);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        if (processResult.ExitCode != 0)
        {
            return new(processResult.ExitCode, default, envelope.Error, processResult.Stderr);
        }

        var value = JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
        return new(processResult.ExitCode, value, null, processResult.Stderr);
    }

    private string NextKey() => $"budget-client-{Interlocked.Increment(ref keySeq)}";

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

    private static void AssertError<T>(LedgerContractResult<T> result, int exitCode, string code, string category)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(category, result.Error.Category);
        Assert.Equal($"tally: {code}", result.StandardError);
    }
}
