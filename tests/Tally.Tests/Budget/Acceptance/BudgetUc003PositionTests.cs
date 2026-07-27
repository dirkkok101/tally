using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Budget.Projection;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget.Position;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Projection;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// UC-BUDGET-003 / TASK-BUDGET-VERIFY-UC-003 published-surface acceptance matrix.
/// Exercises budget.position.get (and INSIGHTS parity) through the public process only:
/// revision selection, four-bucket accounting, plan-absence states, snapshot provenance,
/// mismatch/failure paths, concurrency, owner↔INSIGHTS parity, and no retention.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetUc003PositionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-uc003-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-uc003", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private BudgetServices budgetServices = null!;
    private BudgetStateStore store = null!;
    private ManualTimeProvider clock = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var ledgerServices = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, ledgerServices);
        var ledger = new LedgerContractClient(registry, bootstrap);

        // Mid-July 2026: July Current; August Future; June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        budgetServices = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        store = budgetServices.State.Store;
        process = new TallyProcess(registry, ledgerServices with { Budget = budgetServices.Operations });

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

    // ── Selection ────────────────────────────────────────────────────────────

    // UC-BUDGET-003 / selection / active default
    [Fact]
    public async Task Selection_active_default_binds_revision_with_full_provenance()
    {
        var cat = await CreateCategoryAsync("SelActive");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10_000)], "july");
        var activated = await ActivateAsync(draft.RevisionId, "go-live");

        var result = await GetPositionSuccessAsync(Period(2026, 7));

        Assert.True(result.HasActiveBudgetPlanRevision);
        Assert.NotNull(result.Position);
        var position = result.Position!;
        Assert.Equal(activated.RevisionId, position.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, position.RevisionStatus);
        Assert.Equal(activated.PlanId, position.PlanId);
        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, position.CalculationSchemaVersion);
        Assert.Equal("ZAR", position.CurrencyCode);
        Assert.Equal(CategoryContractVersions.Current, position.CategoryContractVersion);
        Assert.Equal(BudgetPeriodState.Current, position.Period.State);
        Assert.Equal("2026-07-01", position.Period.StartInclusive);
        Assert.Equal("2026-08-01", position.Period.EndExclusive);
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.ExpiresAt));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.StoreGenerationFingerprint));
        Assert.Equal(ActualsContractVersions.Current, position.Ledger.ContractVersion);
    }

    // UC-BUDGET-003 / selection / explicit Draft
    [Fact]
    public async Task Selection_explicit_draft_is_evaluated_without_activation()
    {
        var cat = await CreateCategoryAsync("SelDraft");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2_500)], "draft only");

        var result = await GetPositionSuccessAsync(Period(2026, 7), draft.RevisionId);

        Assert.False(result.HasActiveBudgetPlanRevision);
        Assert.NotNull(result.Position);
        var position = result.Position!;
        Assert.Equal(BudgetRevisionStatus.Draft, position.RevisionStatus);
        Assert.Equal(draft.RevisionId, position.RevisionId);
        Assert.Equal(2_500, position.Totals.PlannedMinorUnits);
        Assert.Null(await GetActiveRevisionIdAsync(draft.PlanId));
    }

    // UC-BUDGET-003 / selection / explicit Active equals default
    [Fact]
    public async Task Selection_explicit_active_matches_default_active_path()
    {
        var cat = await CreateCategoryAsync("SelBoth");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 77)], "both");
        await ActivateAsync(draft.RevisionId, "act");

        var byDefault = await GetPositionSuccessAsync(Period(2026, 7));
        var byId = await GetPositionSuccessAsync(Period(2026, 7), draft.RevisionId);

        Assert.NotNull(byDefault.Position);
        Assert.NotNull(byId.Position);
        Assert.Equal(byDefault.Position!.RevisionId, byId.Position!.RevisionId);
        Assert.Equal(byDefault.Position.Totals.PlannedMinorUnits, byId.Position.Totals.PlannedMinorUnits);
        Assert.Equal(byDefault.Position.Totals.ActualMinorUnits, byId.Position.Totals.ActualMinorUnits);
        Assert.Equal(BudgetRevisionStatus.Active, byId.Position.RevisionStatus);
    }

    // UC-BUDGET-003 / selection / explicit Superseded
    [Fact]
    public async Task Selection_explicit_superseded_binds_immutable_prior_revision()
    {
        var cat = await CreateCategoryAsync("SelSuper");
        var first = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "v1", key: "s-d1");
        await ActivateAsync(first.RevisionId, "a1", key: "s-a1");
        var second = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 999)], "v2", key: "s-d2");
        await ActivateAsync(second.RevisionId, "a2", key: "s-a2");

        var result = await GetPositionSuccessAsync(Period(2026, 7), first.RevisionId);

        Assert.True(result.HasActiveBudgetPlanRevision);
        Assert.NotNull(result.Position);
        var position = result.Position!;
        Assert.Equal(BudgetRevisionStatus.Superseded, position.RevisionStatus);
        Assert.Equal(first.RevisionId, position.RevisionId);
        Assert.Equal(100, position.Totals.PlannedMinorUnits);
        Assert.Equal(second.RevisionId, await GetActiveRevisionIdAsync(first.PlanId));
    }

    // ── Four-bucket accounting ───────────────────────────────────────────────

    // UC-BUDGET-003 / four-bucket / once-only reconciliation
    [Fact]
    public async Task Four_bucket_actuals_account_every_ordinal_once_and_reconcile_totals()
    {
        var budgeted = await CreateCategoryAsync("BucketBudgeted");
        var zero = await CreateCategoryAsync("BucketZero");
        var omitted = await CreateCategoryAsync("BucketOmitted");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(budgeted.CategoryId, 1_000), Entry(zero.CategoryId, 0)],
            "four-bucket");
        await ActivateAsync(draft.RevisionId, "act");

        var tBudgeted = await RecordAsync("-4.00", "2026-07-01", "b-spend");
        await AssignCategoryAsync(tBudgeted.TransactionId, budgeted.CategoryId);
        var tZero = await RecordAsync("-1.00", "2026-07-02", "z-spend");
        await AssignCategoryAsync(tZero.TransactionId, zero.CategoryId);
        var tUnbudgeted = await RecordAsync("-2.00", "2026-07-03", "u-spend");
        await AssignCategoryAsync(tUnbudgeted.TransactionId, omitted.CategoryId);
        await RecordAsync("-0.50", "2026-07-04", "uncat");

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var totals = position.Totals;

        var budgetedRow = Assert.Single(
            position.CategoryPositions,
            p => p.Kind == BudgetCategoryPositionKind.Budgeted);
        Assert.Equal(budgeted.CategoryId, budgetedRow.CategoryId);
        Assert.Equal(1_000, budgetedRow.PlannedMinorUnits);
        Assert.Equal(400, budgetedRow.ActualMinorUnits);
        Assert.Equal(600, budgetedRow.RemainingMinorUnits);
        Assert.Equal(0, budgetedRow.OverMinorUnits);

        var zeroRow = Assert.Single(
            position.CategoryPositions,
            p => p.Kind == BudgetCategoryPositionKind.ZeroBudget);
        Assert.Equal(zero.CategoryId, zeroRow.CategoryId);
        Assert.Equal(0, zeroRow.PlannedMinorUnits);
        Assert.Equal(100, zeroRow.ActualMinorUnits);
        Assert.Equal(0, zeroRow.RemainingMinorUnits);
        Assert.Equal(100, zeroRow.OverMinorUnits);

        var unbudgetedRow = Assert.Single(
            position.CategoryPositions,
            p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(omitted.CategoryId, unbudgetedRow.CategoryId);
        Assert.Null(unbudgetedRow.PlannedMinorUnits);
        Assert.Null(unbudgetedRow.RemainingMinorUnits);
        Assert.Null(unbudgetedRow.OverMinorUnits);
        Assert.Equal(200, unbudgetedRow.ActualMinorUnits);

        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, position.UncategorizedPosition.Kind);
        Assert.Null(position.UncategorizedPosition.CategoryId);
        Assert.Null(position.UncategorizedPosition.PlannedMinorUnits);
        Assert.Equal(50, position.UncategorizedPosition.ActualMinorUnits);

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
        Assert.DoesNotContain(position.CategoryPositions, p => p.Kind == BudgetCategoryPositionKind.Uncategorized);
    }

    // UC-BUDGET-003 / four-bucket / budgeted remaining
    [Fact]
    public async Task Budgeted_actual_under_plan_reports_remaining_not_over()
    {
        var cat = await CreateCategoryAsync("UnderPlan");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "under");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-15.00", "2026-07-10", "under-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var row = Assert.Single(position.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, row.Kind);
        Assert.Equal(5_000, row.PlannedMinorUnits);
        Assert.Equal(1_500, row.ActualMinorUnits);
        Assert.Equal(3_500, row.RemainingMinorUnits);
        Assert.Equal(0, row.OverMinorUnits);
        Assert.Equal(1_500, position.Totals.BudgetedActualMinorUnits);
    }

    // UC-BUDGET-003 / four-bucket / over variance
    [Fact]
    public async Task Budgeted_actual_over_plan_reports_over_and_zero_remaining()
    {
        var cat = await CreateCategoryAsync("OverPlan");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 500)], "over");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-9.00", "2026-07-14", "over-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var row = Assert.Single(position.CategoryPositions);
        Assert.Equal(0, row.RemainingMinorUnits);
        Assert.Equal(400, row.OverMinorUnits);
        Assert.Equal(0, position.Totals.RemainingMinorUnits);
        Assert.Equal(400, position.Totals.OverMinorUnits);
    }

    // UC-BUDGET-003 / zero
    [Fact]
    public async Task Zero_budget_entry_with_actual_is_zero_budget_bucket()
    {
        var cat = await CreateCategoryAsync("ZeroOnly");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 0)], "zero");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-8.00", "2026-07-11", "zero-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var row = Assert.Single(position.CategoryPositions);
        Assert.Equal(BudgetCategoryPositionKind.ZeroBudget, row.Kind);
        Assert.Equal(0, row.PlannedMinorUnits);
        Assert.Equal(800, row.ActualMinorUnits);
        Assert.Equal(0, row.RemainingMinorUnits);
        Assert.Equal(800, row.OverMinorUnits);
        Assert.Equal(800, position.Totals.ZeroBudgetActualMinorUnits);
    }

    // UC-BUDGET-003 / negative refund
    [Fact]
    public async Task Negative_refund_heavy_actual_nets_exactly_to_zero()
    {
        var cat = await CreateCategoryAsync("RefundCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10_000)], "refund");
        await ActivateAsync(draft.RevisionId, "act");
        var spend = await RecordAsync("-10.00", "2026-07-07", "purchase");
        var credit = await RecordAsync("10.00", "2026-07-08", "refund");
        await AssignCategoryAsync(spend.TransactionId, cat.CategoryId);
        await AssignCategoryAsync(credit.TransactionId, cat.CategoryId);
        await ConfirmRefundAsync(spend.TransactionId, credit.TransactionId);

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var row = Assert.Single(position.CategoryPositions);
        Assert.Equal(0, row.ActualMinorUnits);
        Assert.Equal(0, position.Totals.ActualMinorUnits);
        Assert.Equal(10_000, position.Totals.PlannedMinorUnits);
        Assert.Equal(10_000, position.Totals.RemainingMinorUnits);
    }

    // UC-BUDGET-003 / no-actual
    [Fact]
    public async Task No_matching_actuals_preserve_planned_with_exact_zero_bucket_actuals()
    {
        var a = await CreateCategoryAsync("PlanOnlyA");
        var b = await CreateCategoryAsync("PlanOnlyB");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, 12_500), Entry(b.CategoryId, 0)],
            "plan only");
        await ActivateAsync(draft.RevisionId, "act");

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
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

    // ── Missing plan / no-active ─────────────────────────────────────────────

    // UC-BUDGET-003 / missing-plan
    [Fact]
    public async Task Missing_plan_returns_success_with_null_position_and_no_active_flag()
    {
        var result = await GetPositionSuccessAsync(Period(2026, 7));

        Assert.Null(result.Position);
        Assert.False(result.HasActiveBudgetPlanRevision);
    }

    // UC-BUDGET-003 / no-active
    [Fact]
    public async Task No_active_revision_is_lifecycle_error_distinct_from_missing_plan()
    {
        var missing = await GetPositionAsync(Period(2026, 8));
        Assert.Equal(0, missing.ExitCode);
        Assert.Null(missing.Value!.Position);

        var cat = await CreateCategoryAsync("OnlyDraft");
        await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 10)], "no activate");

        var noActive = await GetPositionAsync(Period(2026, 8));
        Assert.Equal(6, noActive.ExitCode);
        Assert.Equal(BudgetErrors.NoActiveBudgetPlanRevision, noActive.ErrorCode);
        Assert.Equal("lifecycle", noActive.ErrorCategory);
        Assert.Null(noActive.Value);
        Assert.NotEqual(missing.ErrorCode, noActive.ErrorCode);
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

    // UC-BUDGET-003 / snapshot / multi-transaction provenance
    [Fact]
    public async Task Snapshot_multi_transaction_period_cites_one_complete_ledger_snapshot()
    {
        var cat = await CreateCategoryAsync("SnapMulti");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 50_000)], "multi");
        await ActivateAsync(draft.RevisionId, "act");
        for (var day = 1; day <= 5; day++)
        {
            var tx = await RecordAsync($"-{day}.00", $"2026-07-{day:00}", $"m-{day}");
            await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);
        }

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        // 1+2+3+4+5 = 15.00 → 1500 minor
        Assert.Equal(1_500, position.Totals.ActualMinorUnits);
        Assert.Equal(1_500, position.Totals.BudgetedActualMinorUnits);
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(position.Ledger.ExpiresAt));
        Assert.Equal(ActualsContractVersions.Current, position.Ledger.ContractVersion);
    }

    // UC-BUDGET-003 / snapshot / correction cites new snapshot
    [Fact]
    public async Task Snapshot_correction_yields_new_snapshot_id_for_same_revision()
    {
        var cat = await CreateCategoryAsync("SnapCorr");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "corr");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-20.00", "2026-07-09", "will-void");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var firstResult = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(firstResult.Position);
        var first = firstResult.Position!;
        Assert.Equal(2_000, first.Totals.ActualMinorUnits);
        var firstSnapshot = first.Ledger.SnapshotId;
        var revisionId = first.RevisionId;

        await VoidAsync(tx.TransactionId);

        var secondResult = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(secondResult.Position);
        var second = secondResult.Position!;
        Assert.Equal(revisionId, second.RevisionId);
        Assert.Equal(0, second.Totals.ActualMinorUnits);
        Assert.NotEqual(firstSnapshot, second.Ledger.SnapshotId);
    }

    // ── Mismatch / failure ───────────────────────────────────────────────────

    // UC-BUDGET-003 / mismatch / period
    [Fact]
    public async Task Mismatch_explicit_revision_for_other_period_fails_before_ledger()
    {
        var cat = await CreateCategoryAsync("MismatchPeriod");
        var july = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "july");
        await ActivateAsync(july.RevisionId, "act");

        var result = await GetPositionAsync(Period(2026, 8), july.RevisionId);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(BudgetErrors.RevisionPeriodMismatch, result.ErrorCode);
        Assert.Equal("validation", result.ErrorCategory);
        Assert.Null(result.Value);
    }

    // UC-BUDGET-003 / failure / not found
    [Fact]
    public async Task Failure_unknown_revision_is_not_found_with_no_partial_position()
    {
        var result = await GetPositionAsync(Period(2026, 7), LedgerId.New().ToString());
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(BudgetErrors.RevisionNotFound, result.ErrorCode);
        Assert.Equal("not_found", result.ErrorCategory);
        Assert.Null(result.Value);
    }

    // UC-BUDGET-003 / failure / invalid period
    [Fact]
    public async Task Failure_invalid_period_currency_is_validation_before_store()
    {
        var result = await GetPositionAsync(new BudgetPeriodInput(2026, 7, "USD"));
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // UC-BUDGET-003 / failure / unsupported version
    [Fact]
    public async Task Failure_unsupported_contract_version_is_compatibility_error()
    {
        var body = Envelope(
            """{"contractVersion":"9.9","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
            idempotencyKey: null);
        var processResult = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            body,
            CancellationToken.None);
        Assert.Equal(7, processResult.ExitCode);
        Assert.Contains(BudgetErrors.UnsupportedVersion, processResult.Stdout, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(processResult.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.False(
            document.RootElement.TryGetProperty("result", out var resultEl)
            && resultEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);
    }

    // UC-BUDGET-003 / failure / integrity unknown category
    [Fact]
    public async Task Failure_unknown_category_on_revision_is_integrity_with_no_partial()
    {
        var unknownCat = LedgerId.New().ToString();
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedActiveWithCategoryAsync(planId, revisionId, unknownCat, planned: 50);

        var result = await GetPositionAsync(Period(2026, 7), revisionId);
        Assert.Equal(8, result.ExitCode);
        Assert.Equal(BudgetErrors.Integrity, result.ErrorCode);
        Assert.Equal("integrity", result.ErrorCategory);
        Assert.Null(result.Value);
    }

    // UC-BUDGET-003 / failure / overflow integrity
    [Fact]
    public async Task Failure_planned_sum_overflow_is_integrity_with_no_partial_position()
    {
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
        Assert.Equal(8, result.ExitCode);
        Assert.Equal(BudgetErrors.Integrity, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    // UC-BUDGET-003 / concurrency
    [Fact]
    public async Task Concurrency_activation_cannot_change_explicitly_bound_revision_query()
    {
        var cat = await CreateCategoryAsync("ConcCat");
        var first = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "v1", key: "c-d1");
        await ActivateAsync(first.RevisionId, "a1", key: "c-a1");
        var second = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 999)], "v2", key: "c-d2");

        // Explicit v1 query races a concurrent activation of v2.
        var positionTask = GetPositionAsync(Period(2026, 7), first.RevisionId);
        var activateTask = ActivateAsync(second.RevisionId, "a2-race", key: "c-a2");
        await Task.WhenAll(positionTask, activateTask);

        var positionResult = await positionTask;
        Assert.Equal(0, positionResult.ExitCode);
        Assert.NotNull(positionResult.Value!.Position);
        var position = positionResult.Value.Position!;
        Assert.Equal(first.RevisionId, position.RevisionId);
        Assert.Equal(100, position.Totals.PlannedMinorUnits);
        Assert.Contains(
            position.RevisionStatus,
            new[] { BudgetRevisionStatus.Active, BudgetRevisionStatus.Superseded });
        Assert.Equal(second.RevisionId, await GetActiveRevisionIdAsync(first.PlanId));
    }

    // ── Determinism / parity ─────────────────────────────────────────────────

    // UC-BUDGET-003 / parity / deterministic owner reads
    [Fact]
    public async Task Parity_identical_owner_queries_are_semantically_deterministic()
    {
        var cat = await CreateCategoryAsync("DetCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 300)], "det");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-1.00", "2026-07-15", "det-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var aResult = await GetPositionSuccessAsync(Period(2026, 7));
        var bResult = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(aResult.Position);
        Assert.NotNull(bResult.Position);
        var a = aResult.Position!;
        var b = bResult.Position!;

        Assert.Equal(a.RevisionId, b.RevisionId);
        Assert.Equal(a.Totals.PlannedMinorUnits, b.Totals.PlannedMinorUnits);
        Assert.Equal(a.Totals.ActualMinorUnits, b.Totals.ActualMinorUnits);
        Assert.Equal(
            a.CategoryPositions.Select(p => (p.CategoryId, p.Kind, p.ActualMinorUnits)),
            b.CategoryPositions.Select(p => (p.CategoryId, p.Kind, p.ActualMinorUnits)));
        Assert.Equal(a.UncategorizedPosition.ActualMinorUnits, b.UncategorizedPosition.ActualMinorUnits);
    }

    // UC-BUDGET-003 / parity / INSIGHTS evidence equals owner position
    [Fact]
    public async Task Parity_insights_evidence_position_matches_owner_position_get()
    {
        var cat = await CreateCategoryAsync("ParityCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 8_000)], "parity");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-12.00", "2026-07-06", "parity-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var ownerResult = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(ownerResult.Position);
        var owner = ownerResult.Position!;
        var evidence = await GetEvidenceSuccessAsync(Period(2026, 7));

        Assert.Equal(BudgetInsightPlanState.BoundRevision, evidence.PlanState);
        Assert.NotNull(evidence.Revision);
        Assert.NotNull(evidence.Position);
        Assert.Equal(owner.RevisionId, evidence.Position!.RevisionId);
        Assert.Equal(owner.PlanId, evidence.Position.PlanId);
        Assert.Equal(owner.Totals.PlannedMinorUnits, evidence.Position.Totals.PlannedMinorUnits);
        Assert.Equal(owner.Totals.ActualMinorUnits, evidence.Position.Totals.ActualMinorUnits);
        Assert.Equal(owner.Totals.RemainingMinorUnits, evidence.Position.Totals.RemainingMinorUnits);
        Assert.Equal(owner.Totals.OverMinorUnits, evidence.Position.Totals.OverMinorUnits);
        Assert.Equal(owner.Totals.BudgetedActualMinorUnits, evidence.Position.Totals.BudgetedActualMinorUnits);
        Assert.Equal(owner.Totals.ZeroBudgetActualMinorUnits, evidence.Position.Totals.ZeroBudgetActualMinorUnits);
        Assert.Equal(owner.Totals.UnbudgetedActualMinorUnits, evidence.Position.Totals.UnbudgetedActualMinorUnits);
        Assert.Equal(owner.Totals.UncategorizedActualMinorUnits, evidence.Position.Totals.UncategorizedActualMinorUnits);
        Assert.Equal(owner.CategoryPositions.Count, evidence.Position.CategoryPositions.Count);
        Assert.Equal(owner.CalculationSchemaVersion, evidence.CalculationSchemaVersion);
        Assert.Equal(evidence.BudgetActualTotalMinorUnits, evidence.Position.Totals.ActualMinorUnits);
        Assert.Equal(evidence.Ledger.SnapshotId, evidence.Position.Ledger.SnapshotId);
        Assert.Equal(
            evidence.Ledger.StoreGenerationFingerprint,
            evidence.Position.Ledger.StoreGenerationFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(evidence.BindingFingerprint));
        Assert.Equal(64, evidence.BindingFingerprint.Length);
    }

    // UC-BUDGET-003 / parity / INSIGHTS capability is mutation-free
    [Fact]
    public async Task Parity_insights_capability_excludes_mutations_analytics_and_consumer_state()
    {
        var capability = budgetServices.Operations.ReadCapability;
        Assert.Equal(
            BudgetReadCapabilityOperations.All,
            capability.AllowedOperations.Select(o => o.OperationId));
        Assert.Equal(3, capability.AllowedOperations.Count);
        Assert.DoesNotContain(capability.AllowedOperations, o => o.RequiresIdempotencyKey);
        Assert.All(capability.AllowedOperations, o => Assert.Equal("query", o.Kind));
        Assert.DoesNotContain(BudgetOperationIds.DraftCreate, capability.AllowedOperations.Select(o => o.OperationId));
        Assert.DoesNotContain(BudgetOperationIds.RevisionActivate, capability.AllowedOperations.Select(o => o.OperationId));
        Assert.True(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.PositionGet));
        Assert.False(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.DraftCreate));

        var cat = await CreateCategoryAsync("AnalyticsFree");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "analytics");
        await ActivateAsync(draft.RevisionId, "act");
        var evidence = await GetEvidenceSuccessAsync(Period(2026, 7));
        var json = JsonSerializer.Serialize(evidence, BudgetJsonContext.Default.BudgetInsightEvidence);
        Assert.DoesNotContain("pace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forecast", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trend", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anomaly", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recommendation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrative", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("report", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── No retention ─────────────────────────────────────────────────────────

    // UC-BUDGET-003 / no-retention / store unchanged
    [Fact]
    public async Task No_retention_position_query_persists_no_actual_cursor_or_report()
    {
        var cat = await CreateCategoryAsync("NoRetain");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "retain");
        await ActivateAsync(draft.RevisionId, "act");
        var tx = await RecordAsync("-1.00", "2026-07-16", "retain-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var plansBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan;");
        var revsBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var entriesBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_entry;");
        var eventsBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idempBefore = await CountBudgetAsync("SELECT COUNT(*) FROM budget_idempotency_record;");

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);

        Assert.Equal(plansBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revsBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(entriesBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(eventsBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idempBefore, await CountBudgetAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));

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

    // UC-BUDGET-003 / no-retention / schema excludes analytics
    [Fact]
    public async Task No_retention_position_json_has_no_pace_forecast_or_recommendation_fields()
    {
        var cat = await CreateCategoryAsync("SchemaCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "schema");
        await ActivateAsync(draft.RevisionId, "act");

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var json = JsonSerializer.Serialize(result.Position, BudgetJsonContext.Default.BudgetPosition);
        Assert.DoesNotContain("pace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forecast", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trend", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anomaly", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recommendation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrative", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", json, StringComparison.OrdinalIgnoreCase);
    }

    // UC-BUDGET-003 / ordering / category id ascending
    [Fact]
    public async Task Category_positions_ordered_by_category_id_ascending_uncategorized_separate()
    {
        var z = await CreateCategoryAsync("Zzz");
        var a = await CreateCategoryAsync("Aaa");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(z.CategoryId, 10), Entry(a.CategoryId, 20)],
            "order");
        await ActivateAsync(draft.RevisionId, "act");
        await RecordAsync("-1.00", "2026-07-05", "uncat-order");

        var result = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(result.Position);
        var position = result.Position!;
        var ids = position.CategoryPositions.Select(p => p.CategoryId!).ToArray();
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
        Assert.Equal(BudgetCategoryPositionKind.Uncategorized, position.UncategorizedPosition.Kind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<GetBudgetPositionResult> GetPositionSuccessAsync(
        BudgetPeriodInput period,
        string? revisionId = null)
    {
        var result = await GetPositionAsync(period, revisionId);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private async Task<ProcessOpResult<GetBudgetPositionResult>> GetPositionAsync(
        BudgetPeriodInput period,
        string? revisionId = null)
    {
        var revisionJson = revisionId is null ? "null" : "\"" + revisionId + "\"";
        var input =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"},\"revisionId\":"
            + revisionJson
            + "}";
        var processResult = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
        return ParseResult(processResult, BudgetJsonContext.Default.GetBudgetPositionResult);
    }

    private async Task<BudgetInsightEvidence> GetEvidenceSuccessAsync(BudgetPeriodInput period)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"budgetPeriod\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"}}";
        var processResult = await process.RunAsync(
            ["budget", "insights", "evidence", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
        var parsed = ParseResult(processResult, BudgetJsonContext.Default.GetBudgetInsightEvidenceResult);
        Assert.Equal(0, parsed.ExitCode);
        Assert.NotNull(parsed.Value);
        return parsed.Value!.Evidence;
    }

    private async Task<DraftCreated> CreateDraftAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason,
        string? key = null)
    {
        var entriesJson = string.Join(
            ",",
            entries.Select(e =>
                "{\"categoryId\":\""
                + e.CategoryId
                + "\",\"plannedMinorUnits\":"
                + e.PlannedMinorUnits.ToString(CultureInfo.InvariantCulture)
                + "}"));
        var input =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"},\"entries\":["
            + entriesJson
            + "],\"reason\":\""
            + reason
            + "\"}";
        var processResult = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, key ?? NextKey()),
            CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        using var document = JsonDocument.Parse(processResult.Stdout);
        var revision = document.RootElement.GetProperty("result").GetProperty("revision");
        return new DraftCreated(
            revision.GetProperty("planId").GetString()!,
            revision.GetProperty("revisionId").GetString()!);
    }

    private async Task<ActivatedRevision> ActivateAsync(
        string revisionId,
        string reason,
        string? key = null)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + revisionId
            + "\",\"reason\":\""
            + reason
            + "\"}";
        var processResult = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope(input, key ?? NextKey()),
            CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        using var document = JsonDocument.Parse(processResult.Stdout);
        var activated = document.RootElement.GetProperty("result").GetProperty("activated");
        return new ActivatedRevision(
            activated.GetProperty("planId").GetString()!,
            activated.GetProperty("revisionId").GetString()!);
    }

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc003\",\"runId\":\"run-01\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc003\",\"runId\":\"run-01\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                $"Budget UC003 Bank {unique}",
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

    private async Task<TransactionDetail> RecordAsync(string amount, string date, string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(description + date + amount + Guid.NewGuid().ToString("N"))));
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
            new AssignCategoryInput(transactionId, categoryId, "budget-uc003"),
            "cat-" + transactionId + "-" + Guid.NewGuid().ToString("N")[..6],
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task VoidAsync(string transactionId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-uc003-void"),
            "void-" + transactionId,
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task ConfirmRefundAsync(string originalId, string creditId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.refund.confirm",
            new ConfirmRefundInput(originalId, creditId, "budget-uc003-refund"),
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

    private static ProcessOpResult<T> ParseResult<T>(
        ProcessResult processResult,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> resultType)
    {
        using var document = JsonDocument.Parse(processResult.Stdout);
        var root = document.RootElement;
        if (processResult.ExitCode != 0)
        {
            var error = root.GetProperty("error");
            return new(
                processResult.ExitCode,
                default,
                error.GetProperty("code").GetString(),
                error.GetProperty("category").GetString());
        }

        var value = JsonSerializer.Deserialize(root.GetProperty("result").GetRawText(), resultType)!;
        return new(processResult.ExitCode, value, null, null);
    }

    private string NextKey() => $"budget-uc003-{Interlocked.Increment(ref keySeq):D4}";

    private sealed record DraftCreated(string PlanId, string RevisionId);

    private sealed record ActivatedRevision(string PlanId, string RevisionId);

    private sealed record ProcessOpResult<T>(
        int ExitCode,
        T? Value,
        string? ErrorCode,
        string? ErrorCategory);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
