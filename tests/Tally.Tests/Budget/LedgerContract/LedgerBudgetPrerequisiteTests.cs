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
/// TASK-BUDGET-GATE-INT-LEDGER-CONTRACT / bd-1d4j
/// Proves the complete released LEDGER seam BUDGET may consume:
/// descriptors, category lifecycle, snapshot actuals, half-open period mapping,
/// paging/totals integrity, compatibility failures, and private-boundary isolation.
/// Public surface only — OperationRegistry + released operations; no private Ledger store.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerBudgetPrerequisiteTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-prereq-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private string accountId = null!;
    private string groceriesId = null!;
    private string travelId = null!;
    private int keySeq;

    // Budget Period [2026-01-01, 2026-02-01) maps to inclusive LEDGER filters:
    private const string PeriodStartInclusive = "2026-01-01";
    private const string PeriodEndExclusive = "2026-02-01";
    private const string ActualsEffectiveFrom = "2026-01-01";
    private const string ActualsEffectiveTo = "2026-01-31";

    public async Task InitializeAsync()
    {
        var db = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(db));
        accountId = (await CreateAccount()).AccountId;
        groceriesId = (await CreateCategory("Groceries")).CategoryId;
        travelId = (await CreateCategory("Travel")).CategoryId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    // ── Descriptors ──────────────────────────────────────────────────────────

    [Fact]
    public void Registry_exposes_category_list_with_compatibility_and_result_type()
    {
        var op = OperationRegistry.Create().Find("ledger.category.list");
        Assert.NotNull(op);
        Assert.Equal("1.0", op.MinimumContractVersion);
        Assert.Equal("1.0", op.MaximumContractVersion);
        Assert.Equal(typeof(CategoryListResult), op.ResultTypeInfo.Type);
        Assert.Equal(typeof(ListCategoriesInput), op.RequestTypeInfo.Type);
        Assert.Equal("query", op.Kind);
        Assert.False(op.RequiresIdempotencyKey);
        Assert.Contains("ledgerContractVersion", PropertyNames(op.ResultTypeInfo), StringComparer.Ordinal);
    }

    [Fact]
    public void Registry_exposes_category_get_with_compatibility_and_result_type()
    {
        var op = OperationRegistry.Create().Find("ledger.category.get");
        Assert.NotNull(op);
        Assert.Equal("1.0", op.MinimumContractVersion);
        Assert.Equal("1.0", op.MaximumContractVersion);
        Assert.Equal(typeof(CategoryDetail), op.ResultTypeInfo.Type);
        Assert.Equal(typeof(GetCategoryInput), op.RequestTypeInfo.Type);
        Assert.Equal("query", op.Kind);
        Assert.False(op.RequiresIdempotencyKey);
        var names = PropertyNames(op.ResultTypeInfo);
        Assert.Contains("categoryId", names, StringComparer.Ordinal);
        Assert.Contains("name", names, StringComparer.Ordinal);
        Assert.Contains("status", names, StringComparer.Ordinal);
        Assert.Contains("ledgerContractVersion", names, StringComparer.Ordinal);
    }

    [Fact]
    public void Registry_exposes_actuals_query_with_required_composition_fields()
    {
        var op = OperationRegistry.Create().Find("ledger.actuals.query");
        Assert.NotNull(op);
        Assert.Equal("1.0", op.MinimumContractVersion);
        Assert.Equal("1.0", op.MaximumContractVersion);
        Assert.Equal(typeof(ActualsQueryResult), op.ResultTypeInfo.Type);
        Assert.Equal(typeof(QueryActualsInput), op.RequestTypeInfo.Type);
        Assert.Equal("query", op.Kind);
        Assert.False(op.RequiresIdempotencyKey);
        var names = PropertyNames(op.ResultTypeInfo);
        foreach (var required in new[]
                 {
                     "snapshotId", "expiresAt", "totalCount", "items", "totals", "groups", "cursor",
                     "ledgerContractVersion", "storeGenerationFingerprint"
                 })
        {
            Assert.Contains(required, names, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Actuals_page_item_exposes_identity_ordinal_category_and_budget_contribution()
    {
        var itemType = typeof(ActualsPageItem);
        foreach (var required in new[]
                 {
                     "Ordinal", "TransactionId", "EffectiveDate", "CategoryState", "CategoryId", "Contribution"
                 })
        {
            Assert.NotNull(itemType.GetProperty(required));
        }

        var contribution = typeof(ActualsTotalsResult);
        Assert.NotNull(contribution.GetProperty("BudgetActual"));
        Assert.NotNull(contribution.GetProperty("NetAccountMovement"));
        Assert.NotNull(contribution.GetProperty("ExternalSpend"));
    }

    // ── Category lifecycle evidence ──────────────────────────────────────────

    [Fact]
    public async Task Active_category_exposes_stable_id_display_name_and_contract_version()
    {
        var listed = await ListCategories(new ListCategoriesInput(Status: CategoryStatus.Active));
        var item = Assert.Single(listed.Items, x => x.CategoryId == groceriesId);
        Assert.Equal("Groceries", item.Name);
        Assert.Equal(CategoryStatus.Active, item.Status);
        Assert.Equal(CategoryContractVersions.Current, item.LedgerContractVersion);
        Assert.Equal(CategoryContractVersions.Current, listed.LedgerContractVersion);

        var got = await GetCategory(groceriesId);
        Assert.Equal(groceriesId, got.CategoryId);
        Assert.Equal("Groceries", got.Name);
        Assert.Equal(CategoryStatus.Active, got.Status);
        Assert.Equal(CategoryContractVersions.Current, got.LedgerContractVersion);
    }

    [Fact]
    public async Task Archived_category_keeps_stable_id_and_remains_readable()
    {
        var created = await CreateCategory("ArchiveMe");
        await ArchiveCategory(created.CategoryId);

        var got = await GetCategory(created.CategoryId);
        Assert.Equal(created.CategoryId, got.CategoryId);
        Assert.Equal(CategoryStatus.Archived, got.Status);
        Assert.Equal("ArchiveMe", got.Name);
        Assert.Equal(CategoryContractVersions.Current, got.LedgerContractVersion);

        var archived = await ListCategories(new ListCategoriesInput(Status: CategoryStatus.Archived));
        Assert.Contains(archived.Items, x => x.CategoryId == created.CategoryId && x.Status == CategoryStatus.Archived);
    }

    [Fact]
    public async Task Rename_preserves_stable_identity_for_budget_references()
    {
        var created = await CreateCategory("BeforeRename");
        var renamed = Success(
            await Run("ledger.category.rename", new RenameCategoryInput(created.CategoryId, "AfterRename", "budget-prereq"), NextKey()),
            LedgerJsonContext.Default.CategoryLifecycleResult);
        Assert.Equal(created.CategoryId, renamed.Category.CategoryId);
        Assert.Equal("AfterRename", renamed.Category.Name);

        var got = await GetCategory(created.CategoryId);
        Assert.Equal(created.CategoryId, got.CategoryId);
        Assert.Equal("AfterRename", got.Name);
    }

    [Fact]
    public async Task Unknown_category_id_fails_atomically_with_no_partial_evidence()
    {
        var unknown = LedgerId.New().ToString();
        var result = await Run("ledger.category.get", new GetCategoryInput(unknown), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("LEDGER-CATEGORY-NOT-FOUND", ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Active_filter_excludes_archived_while_archived_remain_gettable()
    {
        var keep = await CreateCategory("KeepActive");
        var drop = await CreateCategory("DropArchived");
        await ArchiveCategory(drop.CategoryId);

        var active = await ListCategories(new ListCategoriesInput(Status: CategoryStatus.Active));
        Assert.Contains(active.Items, x => x.CategoryId == keep.CategoryId);
        Assert.DoesNotContain(active.Items, x => x.CategoryId == drop.CategoryId);

        // Draft activation requires active lifecycle; historical assigned IDs still resolve via get.
        var archivedDetail = await GetCategory(drop.CategoryId);
        Assert.Equal(CategoryStatus.Archived, archivedDetail.Status);
        Assert.Equal(drop.CategoryId, archivedDetail.CategoryId);
    }

    // ── January half-open period mapping ─────────────────────────────────────

    [Fact]
    public void Half_open_budget_period_maps_to_inclusive_january_actuals_filter()
    {
        // Budget Period [startInclusive, endExclusive) → LEDGER inclusive EffectiveFrom/EffectiveTo.
        var start = DateOnly.Parse(PeriodStartInclusive);
        var endExclusive = DateOnly.Parse(PeriodEndExclusive);
        var effectiveFrom = start.ToString("yyyy-MM-dd");
        var effectiveTo = endExclusive.AddDays(-1).ToString("yyyy-MM-dd");

        Assert.Equal(ActualsEffectiveFrom, effectiveFrom);
        Assert.Equal(ActualsEffectiveTo, effectiveTo);
        Assert.NotEqual(PeriodEndExclusive, effectiveTo); // never pass endExclusive as inclusive To
    }

    [Fact]
    public async Task January_inclusive_filter_returns_boundary_days_and_excludes_next_month()
    {
        await Record("-10.00", "2026-01-01", "jan-start");
        await Record("-20.00", "2026-01-15", "jan-mid");
        await Record("-30.00", "2026-01-31", "jan-end");
        await Record("-40.00", "2026-02-01", "feb-excluded");
        await Record("-50.00", "2025-12-31", "dec-excluded");

        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, item => Assert.True(
            item.EffectiveDate is "2026-01-01" or "2026-01-15" or "2026-01-31",
            $"Unexpected effective date {item.EffectiveDate}"));
        Assert.DoesNotContain(page.Items, i => i.EffectiveDate is "2026-02-01" or "2025-12-31");
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(page.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(page.ExpiresAt));
    }

    [Fact]
    public async Task January_period_full_set_budget_actual_total_is_exact()
    {
        await Record("-12.50", "2026-01-05", "jan-a");
        await Record("-7.50", "2026-01-06", "jan-b");
        await Record("-5.00", "2026-01-31", "jan-c");
        // Outside period — must not affect totals
        await Record("-100.00", "2026-02-01", "feb-out");

        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal("25", page.Totals.BudgetActual);
        Assert.Equal(page.Totals.BudgetActual, SumBudget(page.Items));
    }

    [Fact]
    public async Task January_multi_page_shares_snapshot_consumes_every_ordinal_once_and_reconciles_totals()
    {
        for (var i = 0; i < 5; i++)
        {
            var day = (i + 1).ToString("00");
            await Record($"-{(i + 1)}.00", $"2026-01-{day}", "jan-page-" + i);
        }

        var pages = await Drain(new ActualsFilterInput(EffectiveFrom: ActualsEffectiveFrom, EffectiveTo: ActualsEffectiveTo), pageSize: 2);
        Assert.True(pages.Count >= 3, $"Expected multiple pages, got {pages.Count}");

        var first = pages[0];
        Assert.All(pages, p => Assert.Equal(first.SnapshotId, p.SnapshotId));
        Assert.All(pages, p => Assert.Equal(first.StoreGenerationFingerprint, p.StoreGenerationFingerprint));
        Assert.All(pages, p => Assert.Equal(first.Totals.BudgetActual, p.Totals.BudgetActual));
        Assert.All(pages, p => Assert.Equal(first.LedgerContractVersion, p.LedgerContractVersion));
        Assert.All(pages, p => Assert.Equal(first.TotalCount, p.TotalCount));

        var allItems = pages.SelectMany(p => p.Items).ToArray();
        var allIds = allItems.Select(i => i.TransactionId).ToArray();
        Assert.Equal(allIds.Length, allIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, allIds.Length);
        Assert.Equal(5, first.TotalCount);

        // Every ordinal exactly once — no duplicates, no gaps.
        var ordinals = allItems.Select(i => i.Ordinal).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 5), ordinals);

        Assert.Equal("15", first.Totals.BudgetActual);
        Assert.Equal(first.Totals.BudgetActual, SumBudget(allItems));
    }

    // ── Actuals composition evidence ─────────────────────────────────────────

    [Fact]
    public async Task Snapshot_items_expose_nullable_category_and_ordinals_without_gaps()
    {
        var uncategorized = await Record("-5.00", "2026-01-10", "uncat");
        var categorized = await Record("-7.00", "2026-01-11", "cat");
        await AssignCategory(categorized.TransactionId, groceriesId);

        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(Enumerable.Range(0, 2), page.Items.Select(i => i.Ordinal));

        var withCat = page.Items.Single(i => i.TransactionId == categorized.TransactionId);
        var without = page.Items.Single(i => i.TransactionId == uncategorized.TransactionId);
        Assert.Equal(groceriesId, withCat.CategoryId);
        Assert.Equal(TransactionCategoryState.Categorized, withCat.CategoryState);
        Assert.Null(without.CategoryId);
        Assert.Equal(TransactionCategoryState.Uncategorized, without.CategoryState);
    }

    [Fact]
    public async Task Archived_category_assignment_retains_stable_id_on_actuals()
    {
        var tx = await Record("-15.00", "2026-01-14", "archive-cat");
        await AssignCategory(tx.TransactionId, travelId);
        await ArchiveCategory(travelId);

        var page = await QueryJanuary(pageSize: 50);
        var item = page.Items.Single(i => i.TransactionId == tx.TransactionId);
        Assert.Equal(travelId, item.CategoryId);
        Assert.Equal(TransactionCategoryState.Categorized, item.CategoryState);

        // Lifecycle evidence for BUDGET comes from released category.get (composition, not private store).
        var category = await GetCategory(travelId);
        Assert.Equal(CategoryStatus.Archived, category.Status);
        Assert.Equal(travelId, category.CategoryId);
    }

    [Fact]
    public async Task Active_lifecycle_filter_excludes_voided_transactions()
    {
        var keep = await Record("-9.00", "2026-01-12", "keep-active");
        var doomed = await Record("-11.00", "2026-01-13", "void-me");
        await Void(doomed.TransactionId);

        var page = await Query(
            new ActualsFilterInput(
                EffectiveFrom: ActualsEffectiveFrom,
                EffectiveTo: ActualsEffectiveTo,
                LifecycleStates: [TransactionLifecycleStatus.Active]),
            50);
        Assert.Contains(page.Items, i => i.TransactionId == keep.TransactionId);
        Assert.DoesNotContain(page.Items, i => i.TransactionId == doomed.TransactionId);
    }

    [Fact]
    public async Task Owned_transfer_principal_is_excluded_from_budget_actual()
    {
        var other = await CreateAccount("Other");
        var outflow = await Record("-50.00", "2026-01-20", "transfer-out");
        var inflow = await Record("50.00", "2026-01-20", "transfer-in", other.AccountId);
        await ConfirmTransfer(outflow.TransactionId, inflow.TransactionId);

        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal("0", page.Totals.BudgetActual);
    }

    [Fact]
    public async Task Full_refund_nets_budget_actual_to_zero()
    {
        var spend = await Record("-100.00", "2026-01-21", "purchase");
        var credit = await Record("100.00", "2026-01-22", "refund");
        await ConfirmRefund(spend.TransactionId, credit.TransactionId);

        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal("0", page.Totals.BudgetActual);
    }

    [Fact]
    public async Task Empty_january_period_returns_zero_total_and_empty_items_with_contract()
    {
        // Use a clean year with no seed transactions in InitializeAsync.
        var page = await Query(new ActualsFilterInput(EffectiveFrom: "2024-01-01", EffectiveTo: "2024-01-31"), 50);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Equal("0", page.Totals.BudgetActual);
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.Null(page.Cursor);
    }

    [Fact]
    public async Task First_page_exposes_generation_and_contract_without_cursor_decode()
    {
        await Record("-1.00", "2026-01-10", "meta");
        var page = await QueryJanuary(pageSize: 50);
        Assert.Equal(ActualsContractVersions.Current, page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.Equal(64, page.StoreGenerationFingerprint!.Length);
        Assert.False(string.IsNullOrWhiteSpace(page.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(page.ExpiresAt));
    }

    // ── Compatibility / integrity failures → no partial evidence ─────────────

    [Fact]
    public async Task Expired_cursor_fails_atomically_without_partial_page()
    {
        await Record("-1.00", "2026-01-01", "seed-a");
        await Record("-2.00", "2026-01-02", "seed-b");
        var first = await QueryJanuary(pageSize: 1);
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorExpiry(first.Cursor!, DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = await Run("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.SnapshotExpired, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Contract_version_mismatch_on_cursor_fails_without_partial_evidence()
    {
        await Record("-1.00", "2026-01-03", "seed");
        await Record("-2.00", "2026-01-04", "seed2");
        var first = await QueryJanuary(pageSize: 1);
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorContract(first.Cursor!, "9.9");
        var result = await Run("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.ContractMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Generation_mismatch_fails_without_partial_evidence()
    {
        await Record("-1.00", "2026-01-05", "g1");
        await Record("-2.00", "2026-01-06", "g2");
        var first = await QueryJanuary(pageSize: 1);
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorGeneration(first.Cursor!, "deadbeef");
        var result = await Run("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.GenerationMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Hierarchy_mismatch_fails_without_partial_evidence()
    {
        await Record("-1.00", "2026-01-07", "h1");
        await Record("-2.00", "2026-01-08", "h2");
        var first = await QueryJanuary(pageSize: 1);
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorHierarchy(first.Cursor!, "cafebabe");
        var result = await Run("ledger.actuals.query", new QueryActualsInput(Cursor: tampered), null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.HierarchyMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Filter_on_later_page_is_rejected_without_partial_evidence()
    {
        await Record("-1.00", "2026-01-09", "f1");
        await Record("-2.00", "2026-01-10", "f2");
        var first = await QueryJanuary(pageSize: 1);
        Assert.NotNull(first.Cursor);

        var result = await Run(
            "ledger.actuals.query",
            new QueryActualsInput(new ActualsFilterInput(EffectiveFrom: ActualsEffectiveFrom), Cursor: first.Cursor),
            null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.CursorFilterMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Invalid_date_filter_is_stable_validation_error_without_partial_evidence()
    {
        var result = await Run(
            "ledger.actuals.query",
            new QueryActualsInput(new ActualsFilterInput(EffectiveFrom: "not-a-date")),
            null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.InvalidFilter, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Invalid_cursor_payload_fails_without_partial_evidence()
    {
        var result = await Run(
            "ledger.actuals.query",
            new QueryActualsInput(Cursor: "not-a-valid-cursor"),
            null);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.CursorInvalid, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    // ── Private-boundary architecture scan ───────────────────────────────────

    [Fact]
    public void Budget_production_code_does_not_reference_private_ledger_composition_surface()
    {
        // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION: BUDGET must not reach into Ledger private
        // handlers/domain/SQL/storage for category or actuals evidence. Budget-owned SQLite
        // under Infrastructure/Budget is out of scope for this gate.
        var repoRoot = FindRepositoryRoot();
        var budgetRoots = new[]
        {
            Path.Combine(repoRoot, "src", "Tally", "Features", "Budget"),
            Path.Combine(repoRoot, "src", "Tally", "Domain", "Budget"),
            Path.Combine(repoRoot, "src", "Tally", "Infrastructure", "Budget")
        };

        // Ledger-private composition leakage (not Budget-owned durability).
        string[] forbiddenLedgerPrivate =
        [
            "LedgerDb",
            "LedgerConnectionFactory",
            "LedgerSchema",
            "QuerySnapshotStore",
            "ActualsQueryHandler",
            "CategoryStore",
            "CategoryHandlers",
            "Tally.Domain.Ledger",
            "Tally.Features.Ledger",
            "Tally.Infrastructure.Storage.Actuals",
            "Tally.Infrastructure.Storage.Categories",
            "Tally.Infrastructure.Storage.Accounts",
            "Tally.Infrastructure.Storage.Transactions",
            "Tally.Infrastructure.Storage.Relationships",
            "Tally.Infrastructure.Storage.Reconciliation",
            "Tally.Infrastructure.Storage.Evidence",
            "ledger.db"
        ];

        var scanned = 0;
        foreach (var budgetRoot in budgetRoots)
        {
            if (!Directory.Exists(budgetRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(budgetRoot, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                var source = File.ReadAllText(file);
                foreach (var token in forbiddenLedgerPrivate)
                {
                    Assert.False(
                        source.Contains(token, StringComparison.Ordinal),
                        $"BUDGET production file {file} must not reference Ledger private surface token '{token}'.");
                }
            }
        }

        Assert.True(scanned > 0, "Expected at least one BUDGET production source file under Features/Budget.");
    }

    [Fact]
    public void Budget_features_and_domain_do_not_open_ledger_or_direct_sql_storage()
    {
        // Application/domain layers must not open SQLite or Ledger host bootstrap for composition.
        // Infrastructure/Budget owns Budget durability separately (DD-BUDGET-STATE-STORE).
        var repoRoot = FindRepositoryRoot();
        var layers = new[]
        {
            Path.Combine(repoRoot, "src", "Tally", "Features", "Budget"),
            Path.Combine(repoRoot, "src", "Tally", "Domain", "Budget")
        };

        string[] forbidden =
        [
            "Microsoft.Data.Sqlite",
            "SqliteConnection",
            "SqliteCommand",
            "LedgerDb",
            "LedgerRuntimeBootstrap",
            "LedgerServices",
            "Data Source=",
            "Filename=",
            "CREATE TABLE",
            "SELECT ",
            "INSERT INTO",
            "UPDATE ",
            "DELETE FROM"
        ];

        var scanned = 0;
        foreach (var layer in layers)
        {
            if (!Directory.Exists(layer)) continue;
            foreach (var file in Directory.EnumerateFiles(layer, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                var source = File.ReadAllText(file);
                foreach (var token in forbidden)
                {
                    Assert.False(
                        source.Contains(token, StringComparison.Ordinal),
                        $"BUDGET Features/Domain file {file} must not reference storage/SQL token '{token}'.");
                }
            }
        }

        Assert.True(scanned > 0, "Expected BUDGET Features and/or Domain production sources.");
    }

    [Fact]
    public void Budget_infrastructure_does_not_reference_ledger_storage_configuration_paths()
    {
        var repoRoot = FindRepositoryRoot();
        var infrastructureBudget = Path.Combine(repoRoot, "src", "Tally", "Infrastructure", "Budget");
        if (!Directory.Exists(infrastructureBudget))
        {
            // Later beads introduce Infrastructure/Budget; Features-only tree still gates composition.
            return;
        }

        string[] pathTokens =
        [
            "ledger.db",
            "LedgerDb",
            "LedgerConnectionFactory",
            "LedgerSchema",
            "LedgerRuntimeBootstrap",
            "tally-ledger",
            "/ledger/"
        ];

        foreach (var file in Directory.EnumerateFiles(infrastructureBudget, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var token in pathTokens)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"BUDGET infrastructure file {file} must not reference Ledger storage path/config token '{token}'.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AccountDetail> CreateAccount(string? name = null)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var masked = $"****{Random.Shared.Next(1000, 9999)}";
        return Success(await Run(
            "ledger.account.create",
            new CreateAccountInput(
                name is null ? $"Budget Bank {unique}" : $"{name} {unique}",
                $"Primary-{unique}",
                AccountType.Cheque,
                masked,
                "ZAR"),
            NextKey()), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name) =>
        Success(await Run("ledger.category.create", new CreateCategoryInput(name), NextKey()), LedgerJsonContext.Default.CategoryDetail);

    private async Task<CategoryDetail> GetCategory(string id) =>
        Success(await Run("ledger.category.get", new GetCategoryInput(id), null), LedgerJsonContext.Default.CategoryDetail);

    private async Task<CategoryListResult> ListCategories(ListCategoriesInput input) =>
        Success(await Run("ledger.category.list", input, null), LedgerJsonContext.Default.CategoryListResult);

    private async Task ArchiveCategory(string id) =>
        _ = Success(await Run("ledger.category.archive", new ArchiveCategoryInput(id, "budget-prereq"), NextKey()), LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> Record(string amount, string date, string description, string? account = null)
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(description + date + amount + Guid.NewGuid().ToString("N"))));
        return Success(await Run(
            "ledger.transaction.record",
            new RecordTransactionInput(
                account ?? accountId,
                amount,
                "ZAR",
                date,
                null,
                description,
                null,
                null,
                new(EvidenceKind.AgentCapture, digest, null, null, null)),
            "record-" + digest[..16]), LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task AssignCategory(string transactionId, string categoryId) =>
        _ = Success(await Run(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, "budget-prereq"),
            "cat-" + transactionId), LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task Void(string transactionId) =>
        _ = Success(await Run(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-void"),
            "void-" + transactionId), TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task ConfirmTransfer(string outflowId, string inflowId) =>
        _ = Success(await Run(
            "ledger.transfer.confirm",
            new ConfirmTransferInput(outflowId, inflowId, "budget-transfer"),
            "xfer-" + outflowId), LedgerJsonContext.Default.FinancialRelationshipDetail);

    private async Task ConfirmRefund(string originalId, string creditId) =>
        _ = Success(await Run(
            "ledger.refund.confirm",
            new ConfirmRefundInput(originalId, creditId, "budget-refund"),
            "refund-" + originalId), LedgerJsonContext.Default.FinancialRelationshipDetail);

    private Task<ActualsQueryResult> QueryJanuary(int pageSize) =>
        Query(new ActualsFilterInput(EffectiveFrom: ActualsEffectiveFrom, EffectiveTo: ActualsEffectiveTo), pageSize);

    private async Task<ActualsQueryResult> Query(ActualsFilterInput filter, int pageSize) =>
        Success(await Run("ledger.actuals.query", new QueryActualsInput(filter, pageSize), null), ActualsJsonContext.Default.ActualsQueryResult);

    private async Task<ActualsQueryResult> Continue(string cursor) =>
        Success(await Run("ledger.actuals.query", new QueryActualsInput(Cursor: cursor), null), ActualsJsonContext.Default.ActualsQueryResult);

    private async Task<List<ActualsQueryResult>> Drain(ActualsFilterInput filter, int pageSize)
    {
        var pages = new List<ActualsQueryResult>();
        var first = await Query(filter, pageSize);
        pages.Add(first);
        var cursor = first.Cursor;
        while (cursor is not null)
        {
            var next = await Continue(cursor);
            pages.Add(next);
            cursor = next.Cursor;
        }

        return pages;
    }

    private async Task<ProcessResult> Run<T>(string operationId, T input, string? key)
    {
        var descriptor = OperationRegistry.Create().Find(operationId)!;
        var element = JsonSerializer.SerializeToElement(input!, descriptor.RequestTypeInfo);
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", new("human", "budget-prereq"), element, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private string NextKey() => $"budget-prereq-{Interlocked.Increment(ref keySeq)}";

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.NotNull(envelope.Result);
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static string ErrorCode(ProcessResult result) =>
        JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code;

    private static void AssertNoPartialResult(ProcessResult result)
    {
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Null(envelope.Result);
        Assert.NotNull(envelope.Error);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.Code));
    }

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

    private static IEnumerable<string> PropertyNames(System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo) =>
        typeInfo.Properties.Select(p => p.Name);

    private static string TamperCursorExpiry(string cursor, DateTimeOffset expiry)
    {
        var payload = Decode(cursor);
        var next = payload with
        {
            ExpiresAt = expiry.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture)
        };
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

    private static string TamperCursorHierarchy(string cursor, string hierarchy)
    {
        var payload = Decode(cursor);
        return Encode(payload with { CategoryHierarchyFingerprint = hierarchy });
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tally.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
