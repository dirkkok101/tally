using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget.Position;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.Activate;
using Tally.Features.Budget.Plans.CreateDraft;
using Tally.Features.Budget.Position.Get;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Position;

/// <summary>
/// TC-BUDGET-POSITION-QUERY-CONTRACT / FR-BUDGET-POSITION-QUERY / FR-BUDGET-LEDGER-COMPOSITION
/// Composition: revision selection → public Ledger snapshot → pure calculator → no retention.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetBudgetPositionQueryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-position-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-position", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private CreateBudgetDraftCommand draftCommand = null!;
    private ActivateBudgetPlanRevisionCommand activateCommand = null!;
    private GetBudgetPositionQuery query = null!;
    private ManualTimeProvider clock = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);

        var budgetServices = await BudgetStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = budgetServices.Store;
        executor = new BudgetMutationExecutor(store, budgetServices.Idempotency);

        // Mid-July 2026: July Current; August Future; June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        draftCommand = new CreateBudgetDraftCommand(executor, ledger, clock);
        activateCommand = new ActivateBudgetPlanRevisionCommand(executor, ledger, clock);
        query = new GetBudgetPositionQuery(store, ledger, clock);

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

    // ── Active selection (default) ───────────────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / active selection
    [Fact]
    public async Task Active_revision_selected_without_selector_reports_active_status()
    {
        var cat = await CreateCategoryAsync("Groceries");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10_000)], "july");
        var activated = await ActivateAsync(draft.Value!.Revision.RevisionId, "activate");
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        var result = await GetPositionAsync(Period(2026, 7));

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.HasActiveBudgetPlanRevision);
        var position = result.Value.Position!;
        Assert.Equal(activated.Value!.Activated.RevisionId, position.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, position.RevisionStatus);
        Assert.Equal(activated.Value.Activated.PlanId, position.PlanId);
        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, position.CalculationSchemaVersion);
        Assert.Equal("ZAR", position.CurrencyCode);
        Assert.Equal(CategoryContractVersions.Current, position.CategoryContractVersion);
        Assert.Equal(BudgetPeriodState.Current, position.Period.State);
        Assert.Equal("2026-07-01", position.Period.StartInclusive);
        Assert.Equal("2026-08-01", position.Period.EndExclusive);
    }

    // FR-BUDGET-POSITION-QUERY / provenance
    [Fact]
    public async Task Successful_position_includes_plan_ledger_and_calculation_provenance()
    {
        var cat = await CreateCategoryAsync("ProvCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 500)], "prov");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "go");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var position = result.Value!.Position!;
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.ExpiresAt));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.StoreGenerationFingerprint));
        Assert.Equal(ActualsContractVersions.Current, position.Ledger.ContractVersion);
        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, position.CalculationSchemaVersion);
        Assert.Equal(draft.Value.Revision.PlanId, position.PlanId);
        Assert.Equal(draft.Value.Revision.RevisionId, position.RevisionId);
    }

    // ── Explicit revision selection ──────────────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / explicit Draft
    [Fact]
    public async Task Explicit_draft_revision_is_evaluated_without_activation()
    {
        var cat = await CreateCategoryAsync("DraftOnly");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2_500)], "draft only");

        var result = await GetPositionAsync(Period(2026, 7), draft.Value!.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.HasActiveBudgetPlanRevision);
        Assert.Equal(BudgetRevisionStatus.Draft, result.Value.Position!.RevisionStatus);
        Assert.Equal(draft.Value.Revision.RevisionId, result.Value.Position.RevisionId);
        Assert.Equal(2_500, result.Value.Position.Totals.PlannedMinorUnits);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
        Assert.Null(await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
    }

    // FR-BUDGET-POSITION-QUERY / explicit Superseded
    [Fact]
    public async Task Explicit_superseded_revision_is_evaluated_without_lifecycle_mutation()
    {
        var cat = await CreateCategoryAsync("SupCat");
        var first = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "v1", key: "s-d1");
        await ActivateAsync(first.Value!.Revision.RevisionId, "a1", key: "s-a1");
        var second = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 999)], "v2", key: "s-d2");
        await ActivateAsync(second.Value!.Revision.RevisionId, "a2", key: "s-a2");
        Assert.Equal(BudgetRevisionStatus.Superseded, await GetRevisionStatusAsync(first.Value.Revision.RevisionId));

        var result = await GetPositionAsync(Period(2026, 7), first.Value.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.HasActiveBudgetPlanRevision);
        Assert.Equal(BudgetRevisionStatus.Superseded, result.Value.Position!.RevisionStatus);
        Assert.Equal(100, result.Value.Position.Totals.PlannedMinorUnits);
        Assert.Equal(second.Value.Revision.RevisionId, await GetActiveRevisionIdAsync(first.Value.Revision.PlanId));
    }

    // FR-BUDGET-POSITION-QUERY / explicit Active still works with selector
    [Fact]
    public async Task Explicit_active_revision_id_matches_default_active_selection()
    {
        var cat = await CreateCategoryAsync("BothPaths");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 77)], "both");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var byDefault = await GetPositionAsync(Period(2026, 7));
        var byId = await GetPositionAsync(Period(2026, 7), draft.Value.Revision.RevisionId);

        Assert.True(byDefault.IsSuccess && byId.IsSuccess);
        Assert.Equal(byDefault.Value!.Position!.RevisionId, byId.Value!.Position!.RevisionId);
        Assert.Equal(byDefault.Value.Position.Totals.PlannedMinorUnits, byId.Value.Position.Totals.PlannedMinorUnits);
        Assert.Equal(byDefault.Value.Position.Totals.ActualMinorUnits, byId.Value.Position.Totals.ActualMinorUnits);
    }

    // ── Missing plan / no-active (before Ledger) ─────────────────────────────

    // FR-BUDGET-POSITION-QUERY / NoBudgetPlan
    [Fact]
    public async Task Missing_plan_returns_NoBudgetPlan_success_with_null_position()
    {
        var result = await GetPositionAsync(Period(2026, 7));

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Null(result.Value!.Position);
        Assert.False(result.Value.HasActiveBudgetPlanRevision);
    }

    // FR-BUDGET-POSITION-QUERY / NoActiveBudgetPlanRevision
    [Fact]
    public async Task Plan_with_only_drafts_returns_NoActiveBudgetPlanRevision_before_ledger()
    {
        var cat = await CreateCategoryAsync("OnlyDraft");
        await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "no activate");

        var result = await GetPositionAsync(Period(2026, 7));

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.NoActiveBudgetPlanRevision, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-POSITION-QUERY / NoBudgetPlan vs NoActive never collapsed
    [Fact]
    public async Task NoBudgetPlan_and_NoActive_are_distinct_result_shapes()
    {
        var missing = await GetPositionAsync(Period(2026, 8));
        Assert.True(missing.IsSuccess);
        Assert.Null(missing.Value!.Position);

        var cat = await CreateCategoryAsync("Distinct");
        await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 1)], "future draft");
        var noActive = await GetPositionAsync(Period(2026, 8));
        Assert.Equal(BudgetErrors.NoActiveBudgetPlanRevision, noActive.ErrorCode);
        Assert.NotEqual(missing.ErrorCode, noActive.ErrorCode);
    }

    // ── Mismatch / validation before Ledger ──────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / RevisionPeriodMismatch
    [Fact]
    public async Task Explicit_revision_for_other_period_fails_with_RevisionPeriodMismatch()
    {
        var cat = await CreateCategoryAsync("PeriodMismatch");
        var july = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "july");
        await ActivateAsync(july.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 8), july.Value.Revision.RevisionId);

        Assert.Equal(BudgetErrors.RevisionPeriodMismatch, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-POSITION-QUERY / RevisionNotFound
    [Fact]
    public async Task Unknown_revision_id_fails_before_ledger_with_RevisionNotFound()
    {
        var result = await GetPositionAsync(Period(2026, 7), LedgerId.New().ToString());
        Assert.Equal(BudgetErrors.RevisionNotFound, result.ErrorCode);
    }

    // FR-BUDGET-POSITION-QUERY / invalid period
    [Fact]
    public async Task Invalid_period_currency_fails_before_store_and_ledger()
    {
        var result = await query.HandleAsync(
            new GetBudgetPositionInput(
                BudgetOperationIds.ContractVersion,
                new BudgetPeriodInput(2026, 7, "USD"),
                null),
            actor,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
    }

    // FR-BUDGET-POSITION-QUERY / unsupported version
    [Fact]
    public async Task Unsupported_contract_version_fails_before_ledger()
    {
        var result = await query.HandleAsync(
            new GetBudgetPositionInput("9.9", Period(2026, 7), null),
            actor,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.UnsupportedVersion, result.ErrorCode);
    }

    // FR-BUDGET-POSITION-QUERY / actor required
    [Fact]
    public async Task Missing_actor_fails_before_ledger()
    {
        var result = await query.HandleAsync(
            new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null),
            actor: null,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
    }

    // ── No actuals preserves planned ─────────────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / no matching actuals
    [Fact]
    public async Task No_matching_actuals_preserves_planned_with_exact_zero_actuals()
    {
        var cat = await CreateCategoryAsync("PlannedOnly");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(cat.CategoryId, 12_500), Entry((await CreateCategoryAsync("ZeroKeep")).CategoryId, 0)],
            "plan only");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var position = result.Value!.Position!;
        Assert.Equal(12_500, position.Totals.PlannedMinorUnits);
        Assert.Equal(0, position.Totals.ActualMinorUnits);
        Assert.Equal(12_500, position.Totals.RemainingMinorUnits);
        Assert.Equal(0, position.Totals.OverMinorUnits);
        Assert.Equal(0, position.Totals.BudgetedActualMinorUnits);
        Assert.Equal(0, position.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(0, position.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(0, position.Totals.UncategorizedActualMinorUnits);
        Assert.Equal(0, position.UncategorizedPosition.ActualMinorUnits);
        Assert.All(position.CategoryPositions, p => Assert.Equal(0, p.ActualMinorUnits));
        Assert.Equal(2, position.CategoryPositions.Count);
    }

    // ── Category bucketing with live actuals ─────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / budgeted
    [Fact]
    public async Task Categorized_actual_on_positive_plan_is_Budgeted_once()
    {
        var cat = await CreateCategoryAsync("BudgetedSpend");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "budgeted");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-15.00", "2026-07-10", "groc");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, row.Kind);
        Assert.Equal(cat.CategoryId, row.CategoryId);
        Assert.Equal(5_000, row.PlannedMinorUnits);
        Assert.Equal(1_500, row.ActualMinorUnits);
        Assert.Equal(3_500, row.RemainingMinorUnits);
        Assert.Equal(0, row.OverMinorUnits);
        Assert.Equal(1_500, result.Value.Position.Totals.BudgetedActualMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / zero budget
    [Fact]
    public async Task Categorized_actual_on_zero_plan_entry_is_ZeroBudget_once()
    {
        var cat = await CreateCategoryAsync("ZeroSpend");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 0)], "zero");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-8.00", "2026-07-11", "zero-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, row.Kind);
        Assert.Equal(0, row.PlannedMinorUnits);
        Assert.Equal(800, row.ActualMinorUnits);
        Assert.Equal(0, row.RemainingMinorUnits);
        Assert.Equal(800, row.OverMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / unbudgeted
    [Fact]
    public async Task Categorized_actual_omitted_from_plan_is_Unbudgeted_with_null_variance()
    {
        var planned = await CreateCategoryAsync("Planned");
        var omitted = await CreateCategoryAsync("Omitted");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(planned.CategoryId, 1_000)], "omit");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-3.50", "2026-07-12", "omitted-spend");
        await AssignCategoryAsync(tx.TransactionId, omitted.CategoryId);

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var unbudgeted = result.Value!.Position!.CategoryPositions
            .Single(p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(omitted.CategoryId, unbudgeted.CategoryId);
        Assert.Null(unbudgeted.PlannedMinorUnits);
        Assert.Null(unbudgeted.RemainingMinorUnits);
        Assert.Null(unbudgeted.OverMinorUnits);
        Assert.Equal(350, unbudgeted.ActualMinorUnits);
        Assert.Equal(350, result.Value.Position.Totals.UnbudgetedActualMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / uncategorized
    [Fact]
    public async Task Uncategorized_actual_is_Uncategorized_last_with_null_planned()
    {
        var cat = await CreateCategoryAsync("HasPlan");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "uncat");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        await RecordAsync("-2.25", "2026-07-13", "no-category");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var position = result.Value!.Position!;
        Assert.DoesNotContain(position.CategoryPositions, p => p.Kind == BudgetCategoryPositionKind.Uncategorized);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, position.UncategorizedPosition.Kind);
        Assert.Null(position.UncategorizedPosition.CategoryId);
        Assert.Null(position.UncategorizedPosition.PlannedMinorUnits);
        Assert.Null(position.UncategorizedPosition.RemainingMinorUnits);
        Assert.Null(position.UncategorizedPosition.OverMinorUnits);
        Assert.Equal(225, position.UncategorizedPosition.ActualMinorUnits);
        Assert.Equal(225, position.Totals.UncategorizedActualMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / over variance
    [Fact]
    public async Task When_actual_exceeds_planned_Over_is_difference_and_Remaining_is_zero()
    {
        var cat = await CreateCategoryAsync("OverCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 500)], "over");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-9.00", "2026-07-14", "over-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionAsync(Period(2026, 7));
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        Assert.Equal(0, row.RemainingMinorUnits);
        Assert.Equal(400, row.OverMinorUnits);
        Assert.Equal(0, result.Value.Position.Totals.RemainingMinorUnits);
        Assert.Equal(400, result.Value.Position.Totals.OverMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / totals reconciliation
    [Fact]
    public async Task Totals_reconcile_planned_entries_and_bucket_actual_subtotals()
    {
        var a = await CreateCategoryAsync("A");
        var b = await CreateCategoryAsync("B");
        var c = await CreateCategoryAsync("C");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, 1_000), Entry(b.CategoryId, 0)],
            "recon");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var t1 = await RecordAsync("-4.00", "2026-07-01", "a-spend");
        await AssignCategoryAsync(t1.TransactionId, a.CategoryId);
        var t2 = await RecordAsync("-1.00", "2026-07-02", "b-spend");
        await AssignCategoryAsync(t2.TransactionId, b.CategoryId);
        var t3 = await RecordAsync("-2.00", "2026-07-03", "c-spend");
        await AssignCategoryAsync(t3.TransactionId, c.CategoryId);
        await RecordAsync("-0.50", "2026-07-04", "uncat");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var totals = result.Value!.Position!.Totals;
        Assert.Equal(1_000, totals.PlannedMinorUnits);
        Assert.Equal(400, totals.BudgetedActualMinorUnits);
        Assert.Equal(100, totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(200, totals.UnbudgetedActualMinorUnits);
        Assert.Equal(50, totals.UncategorizedActualMinorUnits);
        Assert.Equal(
            totals.BudgetedActualMinorUnits
            + totals.ZeroBudgetActualMinorUnits
            + totals.UnbudgetedActualMinorUnits
            + totals.UncategorizedActualMinorUnits,
            totals.ActualMinorUnits);
        Assert.Equal(750, totals.ActualMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / ordering
    [Fact]
    public async Task Category_positions_ordered_by_category_id_ascending_uncategorized_separate()
    {
        var z = await CreateCategoryAsync("Zzz");
        var a = await CreateCategoryAsync("Aaa");
        // Stable IDs are ULIDs — order by id, not display name.
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(z.CategoryId, 10), Entry(a.CategoryId, 20)],
            "order");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        await RecordAsync("-1.00", "2026-07-05", "uncat-order");

        var result = await GetPositionAsync(Period(2026, 7));
        var ids = result.Value!.Position!.CategoryPositions.Select(p => p.CategoryId!).ToArray();
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, result.Value.Position.UncategorizedPosition.Kind);
    }

    // FR-BUDGET-POSITION-QUERY / archived category still buckets
    [Fact]
    public async Task Archived_assigned_category_still_buckets_in_position()
    {
        var cat = await CreateCategoryAsync("WillArchive");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1_000)], "arch");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-5.00", "2026-07-06", "arch-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);
        await ArchiveCategoryAsync(cat.CategoryId);

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, row.Kind);
        Assert.Equal(CategoryLifecycleStatus.Archived, row.CurrentLifecycle);
        Assert.Equal(500, row.ActualMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / negative refund-heavy actual
    [Fact]
    public async Task Refund_heavy_negative_budget_actual_is_preserved_exactly()
    {
        var cat = await CreateCategoryAsync("RefundCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10_000)], "refund");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var spend = await RecordAsync("-10.00", "2026-07-07", "purchase");
        var credit = await RecordAsync("10.00", "2026-07-08", "refund");
        await AssignCategoryAsync(spend.TransactionId, cat.CategoryId);
        await AssignCategoryAsync(credit.TransactionId, cat.CategoryId);
        await ConfirmRefundAsync(spend.TransactionId, credit.TransactionId);

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        // Full refund nets budget actual to zero for the pair.
        Assert.Equal(0, row.ActualMinorUnits);
        Assert.Equal(0, result.Value.Position.Totals.ActualMinorUnits);
    }

    // ── Correction recomputes under new snapshot ─────────────────────────────

    // FR-BUDGET-POSITION-QUERY / correction
    [Fact]
    public async Task Later_ledger_correction_yields_new_snapshot_same_revision()
    {
        var cat = await CreateCategoryAsync("CorrectMe");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "corr");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-20.00", "2026-07-09", "will-void");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var first = await GetPositionAsync(Period(2026, 7));
        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.Equal(2_000, first.Value!.Position!.Totals.ActualMinorUnits);
        var firstSnapshot = first.Value.Position.Ledger.SnapshotId;
        var revisionId = first.Value.Position.RevisionId;

        await VoidAsync(tx.TransactionId);

        var second = await GetPositionAsync(Period(2026, 7));
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(revisionId, second.Value!.Position!.RevisionId);
        Assert.Equal(0, second.Value.Position.Totals.ActualMinorUnits);
        Assert.NotEqual(firstSnapshot, second.Value.Position.Ledger.SnapshotId);
    }

    // FR-BUDGET-POSITION-QUERY / determinism
    [Fact]
    public async Task Identical_query_is_semantically_deterministic()
    {
        var cat = await CreateCategoryAsync("DetCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 300)], "det");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-1.00", "2026-07-15", "det-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var a = await GetPositionAsync(Period(2026, 7));
        var b = await GetPositionAsync(Period(2026, 7));
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.Position!.RevisionId, b.Value!.Position!.RevisionId);
        Assert.Equal(a.Value.Position.Totals.PlannedMinorUnits, b.Value.Position.Totals.PlannedMinorUnits);
        Assert.Equal(a.Value.Position.Totals.ActualMinorUnits, b.Value.Position.Totals.ActualMinorUnits);
        Assert.Equal(
            a.Value.Position.CategoryPositions.Select(p => p.CategoryId),
            b.Value.Position.CategoryPositions.Select(p => p.CategoryId));
    }

    // ── Snapshot / multi-page ────────────────────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / snapshot multi-page composition via client
    [Fact]
    public async Task Multi_transaction_period_actuals_compose_under_one_cited_snapshot()
    {
        var cat = await CreateCategoryAsync("Multi");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 50_000)], "multi");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        for (var day = 1; day <= 5; day++)
        {
            var tx = await RecordAsync($"-{day}.00", $"2026-07-{day:00}", $"m-{day}");
            await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);
        }

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        // 1+2+3+4+5 = 15.00 → 1500 minor
        Assert.Equal(1_500, result.Value!.Position!.Totals.ActualMinorUnits);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Position.Ledger.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Position.Ledger.StoreGenerationFingerprint));
    }

    // ── Integrity / overflow / cancellation ──────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / overflow
    [Fact]
    public async Task Planned_sum_overflow_returns_Integrity_with_no_partial_position()
    {
        // CreateDraft rejects checked-sum overflow; seed durable Active revision rows directly
        // so the pure calculator path is the first place the overflow is observed.
        var catA = await CreateCategoryAsync("OverflowA");
        var catB = await CreateCategoryAsync("OverflowB");
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedActiveWithEntriesAsync(
            planId,
            revisionId,
            [
                new BudgetPlanEntryRow(revisionId, catA.CategoryId, long.MaxValue),
                new BudgetPlanEntryRow(revisionId, catB.CategoryId, 1)
            ]);

        var result = await GetPositionAsync(Period(2026, 7), revisionId);
        Assert.Equal(BudgetErrors.Integrity, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-POSITION-QUERY / integrity unknown category on plan
    [Fact]
    public async Task Unknown_category_on_selected_revision_returns_Integrity()
    {
        var unknownCat = LedgerId.New().ToString();
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedActiveWithCategoryAsync(planId, revisionId, unknownCat, planned: 50);

        var result = await GetPositionAsync(Period(2026, 7), revisionId);
        Assert.Equal(BudgetErrors.Integrity, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-POSITION-QUERY / cancellation
    [Fact]
    public async Task Cancelled_token_throws_and_persists_no_derived_state()
    {
        var cat = await CreateCategoryAsync("CancelCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "cancel");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var eventsBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            query.HandleAsync(
                new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null),
                actor,
                cts.Token));

        Assert.Equal(eventsBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
    }

    // ── No retention ─────────────────────────────────────────────────────────

    // FR-BUDGET-POSITION-QUERY / no-retention
    [Fact]
    public async Task Position_query_persists_no_actual_cursor_or_report_in_budget_store()
    {
        var cat = await CreateCategoryAsync("NoRetain");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "retain");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-1.00", "2026-07-16", "retain-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var plansBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan;");
        var revsBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var entriesBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_entry;");
        var eventsBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idempBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_idempotency_record;");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotNull(result.Value!.Position);

        Assert.Equal(plansBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revsBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(entriesBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(eventsBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idempBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));

        // No extra tables beyond the five-table foundation.
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(
            [
                "budget_idempotency_record",
                "budget_lifecycle_event",
                "budget_plan",
                "budget_plan_entry",
                "budget_plan_revision"
            ],
            tables);
    }

    // FR-BUDGET-POSITION-QUERY / schema excludes analytics
    [Fact]
    public async Task Position_json_has_no_pace_forecast_trend_or_recommendation_fields()
    {
        var cat = await CreateCategoryAsync("SchemaCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "schema");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value!.Position, BudgetJsonContext.Default.BudgetPosition);
        Assert.DoesNotContain("pace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forecast", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trend", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anomaly", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recommendation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrative", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Mapper pure unit coverage (expiry / integrity mapping) ───────────────

    // FR-BUDGET-LEDGER-COMPOSITION / expiry → SourceStateChanged
    [Fact]
    public void MapLedgerCompositionError_snapshot_expired_is_SourceStateChanged()
    {
        var code = BudgetContractMapper.MapLedgerCompositionError(
            new ProcessError(ActualsErrors.SnapshotExpired, "lifecycle", "expired"));
        Assert.Equal(BudgetErrors.SourceStateChanged, code);
    }

    // FR-BUDGET-LEDGER-COMPOSITION / generation mismatch
    [Fact]
    public void MapLedgerCompositionError_generation_mismatch_is_SourceStateChanged()
    {
        var code = BudgetContractMapper.MapLedgerCompositionError(
            new ProcessError(ActualsErrors.GenerationMismatch, "conflict", "gen"));
        Assert.Equal(BudgetErrors.SourceStateChanged, code);
    }

    // FR-BUDGET-LEDGER-COMPOSITION / incompatible
    [Fact]
    public void MapLedgerCompositionError_compatibility_is_LedgerIncompatible()
    {
        var code = BudgetContractMapper.MapLedgerCompositionError(
            new ProcessError(BudgetErrors.LedgerIncompatible, "compatibility", "bad"));
        Assert.Equal(BudgetErrors.LedgerIncompatible, code);
    }

    // FR-BUDGET-LEDGER-COMPOSITION / unavailable default
    [Fact]
    public void MapLedgerCompositionError_null_or_unknown_is_LedgerUnavailable()
    {
        Assert.Equal(BudgetErrors.LedgerUnavailable, BudgetContractMapper.MapLedgerCompositionError(null));
        Assert.Equal(
            BudgetErrors.LedgerUnavailable,
            BudgetContractMapper.MapLedgerCompositionError(
                new ProcessError(ActualsErrors.InvalidFilter, "validation", "bad page")));
    }

    // FR-BUDGET-POSITION-QUERY / actual member mapping
    [Fact]
    public void TryMapActualMembers_parses_signed_minor_units_and_reconciles_total()
    {
        var actuals = new ActualsQueryResult(
            SnapshotId: "snap-1",
            ExpiresAt: "2099-01-01T00:00:00Z",
            TotalCount: 2,
            Items:
            [
                Item(0, "tx-1", "2026-07-01", "cat-1", "10.00"),
                Item(1, "tx-2", "2026-07-02", null, "-2.50")
            ],
            Totals: new ActualsTotalsResult("0", "7.50", "7.50"),
            Groups: [],
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: "gen-1");

        Assert.True(BudgetContractMapper.TryMapActualMembers(
            actuals, out var members, out var total, out var error));
        Assert.Null(error);
        Assert.Equal(750, total);
        Assert.Equal(2, members.Count);
        Assert.Equal(1_000, members[0].BudgetActualMinorUnits);
        Assert.Equal(-250, members[1].BudgetActualMinorUnits);
        Assert.Null(members[1].CategoryId);
    }

    // FR-BUDGET-POSITION-QUERY / total mismatch integrity
    [Fact]
    public void TryMapActualMembers_total_mismatch_is_integrity()
    {
        var actuals = new ActualsQueryResult(
            SnapshotId: "snap-1",
            ExpiresAt: "2099-01-01T00:00:00Z",
            TotalCount: 1,
            Items: [Item(0, "tx-1", "2026-07-01", null, "1.00")],
            Totals: new ActualsTotalsResult("0", "9.00", "9.00"),
            Groups: [],
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: "gen-1");

        Assert.False(BudgetContractMapper.TryMapActualMembers(
            actuals, out var members, out _, out var error));
        Assert.Equal(BudgetErrors.Integrity, error);
        Assert.Empty(members);
    }

    // FR-BUDGET-POSITION-QUERY / snapshot provenance required
    [Fact]
    public void TryMapLedgerSnapshot_requires_complete_provenance()
    {
        var good = new ActualsQueryResult(
            "snap", "2099-01-01T00:00:00Z", 0, [], new ActualsTotalsResult("0", "0", "0"), [],
            null, ActualsContractVersions.Current, "gen");
        Assert.NotNull(BudgetContractMapper.TryMapLedgerSnapshot(good, out var err));
        Assert.Null(err);

        var bad = good with { StoreGenerationFingerprint = null };
        Assert.Null(BudgetContractMapper.TryMapLedgerSnapshot(bad, out var err2));
        Assert.Equal(BudgetErrors.Integrity, err2);
    }

    // FR-BUDGET-POSITION-QUERY / empty active plan
    [Fact]
    public async Task Empty_active_plan_returns_zero_planned_and_empty_category_positions()
    {
        var draft = await CreateDraftAsync(Period(2026, 7), [], "empty");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Empty(result.Value!.Position!.CategoryPositions);
        Assert.Equal(0, result.Value.Position.Totals.PlannedMinorUnits);
        Assert.Equal(0, result.Value.Position.Totals.ActualMinorUnits);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, result.Value.Position.UncategorizedPosition.Kind);
    }

    // FR-BUDGET-POSITION-QUERY / future period active
    [Fact]
    public async Task Future_period_active_revision_returns_future_period_state()
    {
        var cat = await CreateCategoryAsync("FuturePos");
        var draft = await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 40)], "future");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 8));
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetPeriodState.Future, result.Value!.Position!.Period.State);
        Assert.Equal(40, result.Value.Position.Totals.PlannedMinorUnits);
    }

    // FR-BUDGET-POSITION-QUERY / display name evidence
    [Fact]
    public async Task Category_position_includes_current_display_name_evidence()
    {
        var cat = await CreateCategoryAsync("DisplayNameCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "name");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 7));
        var row = Assert.Single(result.Value!.Position!.CategoryPositions);
        Assert.Equal("DisplayNameCat", row.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, row.CurrentLifecycle);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CommandResult<GetBudgetPositionResult>> GetPositionAsync(
        BudgetPeriodInput period,
        string? revisionId = null) =>
        query.HandleAsync(
            new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, period, revisionId),
            actor,
            CancellationToken.None);

    private Task<CommandResult<CreateDraftBudgetPlanResult>> CreateDraftAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason,
        string? key = null) =>
        draftCommand.HandleAsync(
            new CreateDraftBudgetPlanInput(BudgetOperationIds.ContractVersion, period, entries, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private Task<CommandResult<ActivateBudgetPlanRevisionResult>> ActivateAsync(
        string revisionId,
        string reason,
        string? key = null) =>
        activateCommand.HandleAsync(
            new ActivateBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, revisionId, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                $"Budget Position Bank {unique}",
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
            new ArchiveCategoryInput(categoryId, "budget-position"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
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
            new AssignCategoryInput(transactionId, categoryId, "budget-position"),
            "cat-" + transactionId + "-" + Guid.NewGuid().ToString("N")[..6],
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task VoidAsync(string transactionId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-position-void"),
            "void-" + transactionId,
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task ConfirmRefundAsync(string originalId, string creditId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.refund.confirm",
            new ConfirmRefundInput(originalId, creditId, "budget-position-refund"),
            "refund-" + originalId,
            LedgerJsonContext.Default.ConfirmRefundInput,
            LedgerJsonContext.Default.FinancialRelationshipDetail);

    private Task SeedActiveWithCategoryAsync(
        string planId,
        string revisionId,
        string categoryId,
        long planned) =>
        SeedActiveWithEntriesAsync(
            planId,
            revisionId,
            [new BudgetPlanEntryRow(revisionId, categoryId, planned)]);

    private async Task SeedActiveWithEntriesAsync(
        string planId,
        string revisionId,
        IReadOnlyList<BudgetPlanEntryRow> entryRows)
    {
        var createdAt = BudgetPlanRevision.FormatUtc(clock.GetUtcNow());
        var domainEntries = entryRows
            .Select(e => new BudgetPlanEntry(e.CategoryId, e.PlannedMinorUnits))
            .ToArray();
        // Payload hash uses checked content; overflow fixtures may still hash as long as
        // individual values are representable (hash does not sum).
        var payloadHash = BudgetPlanRevision.ComputePayloadHash(CategoryContractVersions.Current, domainEntries);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(
            connection,
            transaction,
            new BudgetPlanRow(
                planId,
                "2026-07-01",
                "2026-08-01",
                "ZAR",
                ActiveRevisionId: null,
                createdAt),
            CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection,
            transaction,
            new BudgetPlanRevisionRow(
                revisionId,
                planId,
                1,
                BudgetRevisionStatus.Draft,
                actor.Kind,
                actor.Label,
                actor.RunId,
                "seeded draft",
                createdAt,
                CategoryContractVersions.Current,
                payloadHash,
                ActivatedAtUtc: null,
                SupersededAtUtc: null,
                SupersededByRevisionId: null),
            entryRows,
            new BudgetLifecycleEventRow(
                LedgerId.New().ToString(),
                planId,
                revisionId,
                BudgetPlanLifecycle.EventDraftCreated,
                actor.Kind,
                actor.Label,
                actor.RunId,
                "seeded draft",
                createdAt,
                PriorStatus: null,
                ResultingStatus: BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Draft),
                ReplacementRevisionId: null,
                EventSequence: 1),
            CancellationToken.None);
        await store.ActivateRevisionAsync(
            connection,
            transaction,
            planId,
            revisionId,
            activatedAtUtc: createdAt,
            reason: "seed activate",
            actorKind: actor.Kind,
            actorLabel: actor.Label,
            actorRunId: actor.RunId,
            activateEventId: LedgerId.New().ToString(),
            supersedeEventId: null,
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<string?> GetActiveRevisionIdAsync(string planId)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var plan = await store.GetPlanAsync(connection, null, planId, CancellationToken.None);
        return plan?.ActiveRevisionId;
    }

    private async Task<BudgetRevisionStatus> GetRevisionStatusAsync(string revisionId)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var revision = await store.GetRevisionAsync(connection, null, revisionId, CancellationToken.None);
        return revision!.Status;
    }

    private async Task<long> CountBudgetAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

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

    private string NextKey() => $"budget-position-{Interlocked.Increment(ref keySeq)}";

    private static ActualsPageItem Item(
        int ordinal,
        string transactionId,
        string date,
        string? categoryId,
        string budgetActual) =>
        new(
            ordinal,
            transactionId,
            date,
            categoryId is null ? TransactionCategoryState.Uncategorized : TransactionCategoryState.Categorized,
            categoryId,
            FrozenAncestryIds: [],
            PoolState: TransactionPoolState.Unassigned,
            PoolId: null,
            InstrumentState: TransactionKnowledgeState.Unknown,
            InstrumentId: null,
            CardholderState: TransactionKnowledgeState.Unknown,
            CardholderId: null,
            EvidenceKinds: [],
            ReconciliationState: TransactionReconciliationState.RecordedUnreconciled,
            RelationshipState: ActualsRelationshipRole.None,
            Contribution: new ActualsTotalsResult(budgetActual, budgetActual, budgetActual));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
