using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.CreateDraft;
using Tally.Features.Budget.Plans.GetRevision;
using Tally.Features.Budget.Plans.ListRevisions;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Plans;

/// <summary>
/// TASK-BUDGET-PLAN-READS / TC-BUDGET-PLAN-HISTORY-CONTRACT / FR-BUDGET-PLAN-HISTORY
/// Exact revision get and deterministic period revision list with supplemental category evidence.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetPlanReadQueryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-read-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-read", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private CreateBudgetDraftCommand createDraft = null!;
    private GetBudgetPlanRevisionQuery getRevision = null!;
    private ListBudgetPlanRevisionsQuery listRevisions = null!;
    private ManualTimeProvider clock = null!;
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

        // Mid-July 2026: July is Current; August is Future; June is Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        createDraft = new CreateBudgetDraftCommand(executor, ledger, clock);
        getRevision = new GetBudgetPlanRevisionQuery(store, ledger, clock);
        listRevisions = new ListBudgetPlanRevisionsQuery(store, clock);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Get: success detail ──────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / exact immutable detail
    [Fact]
    public async Task Get_returns_exact_entries_total_period_status_attribution_and_payload()
    {
        var groceries = await CreateCategoryAsync("Groceries");
        var travel = await CreateCategoryAsync("Travel");
        var created = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(groceries.CategoryId, 12_500), Entry(travel.CategoryId, 3_000)],
            "july plan");
        Assert.True(created.IsSuccess, created.ErrorCode);

        var result = await GetAsync(created.Value!.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var revision = result.Value!;
        Assert.Equal(created.Value.Revision.PlanId, revision.PlanId);
        Assert.Equal(created.Value.Revision.RevisionId, revision.RevisionId);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(BudgetRevisionStatus.Draft, revision.Status);
        Assert.Equal(15_500, revision.PlannedTotalMinorUnits);
        Assert.Equal(2, revision.Entries.Count);
        Assert.Equal("july plan", revision.Reason);
        Assert.Equal(actor.Kind, revision.ActorKind);
        Assert.Equal(actor.Label, revision.ActorLabel);
        Assert.Equal(actor.RunId, revision.ActorRunId);
        Assert.Equal(created.Value.Revision.PayloadHash, revision.PayloadHash);
        Assert.Equal(created.Value.Revision.CategoryContractVersion, revision.CategoryContractVersion);
        Assert.Equal(created.Value.Revision.CreatedAt, revision.CreatedAt);
        Assert.Null(revision.ActivatedAt);
        Assert.Null(revision.SupersededAt);
        Assert.Null(revision.SupersededByRevisionId);
        Assert.Equal(BudgetPeriodState.Current, revision.Period.State);
        Assert.Equal("2026-07-01", revision.Period.StartInclusive);
        Assert.Equal("2026-08-01", revision.Period.EndExclusive);
        Assert.Equal("ZAR", revision.Period.CurrencyCode);
        Assert.Equal(
            new[] { groceries.CategoryId, travel.CategoryId }.OrderBy(id => id, StringComparer.Ordinal),
            revision.Entries.Select(e => e.CategoryId));
        Assert.Equal(15_500, revision.Entries.Sum(e => e.PlannedMinorUnits));
    }

    // FR-BUDGET-PLAN-HISTORY / activation provenance
    [Fact]
    public async Task Get_returns_activation_provenance_for_active_revision()
    {
        var cat = await CreateCategoryAsync("ActivateMe");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "activate");
        Assert.True(created.IsSuccess, created.ErrorCode);
        await ActivateOutsideAsync(created.Value!.Revision.PlanId, created.Value.Revision.RevisionId);

        var result = await GetAsync(created.Value.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Active, result.Value!.Status);
        Assert.NotNull(result.Value.ActivatedAt);
        Assert.Null(result.Value.SupersededAt);
        Assert.Null(result.Value.SupersededByRevisionId);
        Assert.Equal(created.Value.Revision.PayloadHash, result.Value.PayloadHash);
        Assert.Equal(100, result.Value.PlannedTotalMinorUnits);
    }

    // FR-BUDGET-PLAN-HISTORY / supersession provenance
    [Fact]
    public async Task Get_returns_supersession_provenance_for_replaced_revision()
    {
        var cat = await CreateCategoryAsync("SupersedeMe");
        var r1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 11)], "first", key: "k-sup-1");
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId);
        var r2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 22)], "second", key: "k-sup-2");
        await ActivateOutsideAsync(r2.Value!.Revision.PlanId, r2.Value.Revision.RevisionId);

        var superseded = await GetAsync(r1.Value.Revision.RevisionId);
        var active = await GetAsync(r2.Value.Revision.RevisionId);

        Assert.True(superseded.IsSuccess && active.IsSuccess);
        Assert.Equal(BudgetRevisionStatus.Superseded, superseded.Value!.Status);
        Assert.NotNull(superseded.Value.ActivatedAt);
        Assert.NotNull(superseded.Value.SupersededAt);
        Assert.Equal(r2.Value.Revision.RevisionId, superseded.Value.SupersededByRevisionId);
        Assert.Equal(11, superseded.Value.PlannedTotalMinorUnits);
        Assert.Equal(r1.Value.Revision.PayloadHash, superseded.Value.PayloadHash);

        Assert.Equal(BudgetRevisionStatus.Active, active.Value!.Status);
        Assert.Null(active.Value.SupersededAt);
        Assert.Null(active.Value.SupersededByRevisionId);
        Assert.Equal(22, active.Value.PlannedTotalMinorUnits);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / rename is supplemental
    [Fact]
    public async Task Get_after_rename_keeps_stored_id_amount_hash_and_exposes_current_name()
    {
        var cat = await CreateCategoryAsync("OriginalName");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 500)], "named");
        Assert.True(created.IsSuccess, created.ErrorCode);
        var originalHash = created.Value!.Revision.PayloadHash;
        var originalPayload = created.Value.Revision.Entries.Single();

        await RenameCategoryAsync(cat.CategoryId, "RenamedName");
        var result = await GetAsync(created.Value.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var entry = Assert.Single(result.Value!.Entries);
        Assert.Equal(originalPayload.CategoryId, entry.CategoryId);
        Assert.Equal(500, entry.PlannedMinorUnits);
        Assert.Equal("RenamedName", entry.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, entry.CurrentLifecycle);
        Assert.Equal(originalHash, result.Value.PayloadHash);
        Assert.Equal(500, result.Value.PlannedTotalMinorUnits);

        var evidence = Assert.Single(result.Value.CategoryLifecycle);
        Assert.Equal(cat.CategoryId, evidence.CategoryId);
        Assert.Equal("RenamedName", evidence.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, evidence.Lifecycle);

        // Durable row is byte-stable (no rewrite of stored intent).
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var row = await store.GetRevisionAsync(connection, null, created.Value.Revision.RevisionId, CancellationToken.None);
        var rows = await store.GetEntriesAsync(connection, null, created.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(originalHash, row!.PayloadHash);
        Assert.Equal(cat.CategoryId, rows.Single().CategoryId);
        Assert.Equal(500, rows.Single().PlannedMinorUnits);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / archive is supplemental inactive evidence
    [Fact]
    public async Task Get_after_archive_keeps_entry_readable_with_archived_lifecycle()
    {
        var cat = await CreateCategoryAsync("ArchiveLater");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 77)], "will archive");
        Assert.True(created.IsSuccess, created.ErrorCode);
        var originalHash = created.Value!.Revision.PayloadHash;

        await ArchiveCategoryAsync(cat.CategoryId);
        var result = await GetAsync(created.Value.Revision.RevisionId);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var entry = Assert.Single(result.Value!.Entries);
        Assert.Equal(cat.CategoryId, entry.CategoryId);
        Assert.Equal(77, entry.PlannedMinorUnits);
        Assert.Equal(CategoryLifecycleStatus.Archived, entry.CurrentLifecycle);
        Assert.Equal("ArchiveLater", entry.CurrentDisplayName);
        Assert.Equal(originalHash, result.Value.PayloadHash);
        Assert.Equal(CategoryLifecycleStatus.Archived, result.Value.CategoryLifecycle.Single().Lifecycle);
    }

    // FR-BUDGET-PLAN-HISTORY / empty and all-zero remain distinct
    [Fact]
    public async Task Get_preserves_empty_and_all_zero_as_distinct_states()
    {
        var cat = await CreateCategoryAsync("ZeroOnly");
        var empty = await CreateDraftAsync(Period(2026, 7), [], "empty", key: "k-empty");
        var zero = await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 0)], "zero", key: "k-zero");
        Assert.True(empty.IsSuccess && zero.IsSuccess);

        var emptyGet = await GetAsync(empty.Value!.Revision.RevisionId);
        var zeroGet = await GetAsync(zero.Value!.Revision.RevisionId);

        Assert.True(emptyGet.IsSuccess && zeroGet.IsSuccess);
        Assert.Empty(emptyGet.Value!.Entries);
        Assert.Empty(emptyGet.Value.CategoryLifecycle);
        Assert.Equal(0, emptyGet.Value.PlannedTotalMinorUnits);
        Assert.Single(zeroGet.Value!.Entries);
        Assert.Equal(0, zeroGet.Value.PlannedTotalMinorUnits);
        Assert.NotEqual(emptyGet.Value.PayloadHash, zeroGet.Value.PayloadHash);
    }

    // FR-BUDGET-PLAN-HISTORY / closed period remains readable
    [Fact]
    public async Task Get_reads_closed_period_history_with_closed_period_state()
    {
        // Seed while June is still "current" relative to a temporary past clock, then read with July clock.
        var pastClock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var pastCreate = new CreateBudgetDraftCommand(executor, ledger, pastClock);
        var cat = await CreateCategoryAsync("ClosedRead");
        var created = await pastCreate.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 6),
                [Entry(cat.CategoryId, 9)],
                "june draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(created.IsSuccess, created.ErrorCode);

        var result = await GetAsync(created.Value!.Revision.RevisionId);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetPeriodState.Closed, result.Value!.Period.State);
        Assert.Equal("2026-06-01", result.Value.Period.StartInclusive);
        Assert.Equal("2026-07-01", result.Value.Period.EndExclusive);
        Assert.Equal(9, result.Value.PlannedTotalMinorUnits);
        Assert.Equal(BudgetRevisionStatus.Draft, result.Value.Status);
    }

    // FR-BUDGET-PLAN-HISTORY / unknown revision
    [Fact]
    public async Task Get_unknown_revision_returns_stable_not_found_without_mutation()
    {
        var cat = await CreateCategoryAsync("KeepMe");
        var existing = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "seed");
        Assert.True(existing.IsSuccess);
        var plansBefore = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revisionsBefore = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idemBefore = await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");

        var unknown = LedgerId.New().ToString();
        var result = await GetAsync(unknown);

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.RevisionNotFound, result.ErrorCode);
        Assert.Equal(plansBefore, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revisionsBefore, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idemBefore, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    // FR-BUDGET-PLAN-HISTORY / blank revision id
    [Fact]
    public async Task Get_blank_revision_id_is_invalid_input()
    {
        var result = await getRevision.HandleAsync(
            new GetBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, "  "),
            actor,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
    }

    // Contract version / actor validation
    [Fact]
    public async Task Get_rejects_unsupported_version_and_missing_actor()
    {
        var version = await getRevision.HandleAsync(
            new GetBudgetPlanRevisionInput("9.9", LedgerId.New().ToString()),
            actor,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.UnsupportedVersion, version.ErrorCode);

        var noActor = await getRevision.HandleAsync(
            new GetBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, LedgerId.New().ToString()),
            actor: null,
            CancellationToken.None);
        Assert.Equal(BudgetErrors.ActorRequired, noActor.ErrorCode);
    }

    // Cancellation
    [Fact]
    public async Task Get_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            getRevision.HandleAsync(
                new GetBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, LedgerId.New().ToString()),
                actor,
                cts.Token));
    }

    // No mutation / no idempotency on get
    [Fact]
    public async Task Get_does_not_mutate_plan_state_or_write_idempotency()
    {
        var cat = await CreateCategoryAsync("ImmutableGet");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 3)], "immutable");
        Assert.True(created.IsSuccess);
        var hashBefore = created.Value!.Revision.PayloadHash;
        var idemBefore = await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");
        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var activeBefore = await GetActiveRevisionIdAsync(created.Value.Revision.PlanId);

        var result = await GetAsync(created.Value.Revision.RevisionId);
        Assert.True(result.IsSuccess);

        Assert.Equal(hashBefore, result.Value!.PayloadHash);
        Assert.Equal(idemBefore, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(activeBefore, await GetActiveRevisionIdAsync(created.Value.Revision.PlanId));
        Assert.Equal(BudgetRevisionStatus.Draft, result.Value.Status);
    }

    // ── List: ordering, states, distinctions ─────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / ordering
    [Fact]
    public async Task List_returns_all_statuses_ordered_by_created_at_then_revision_id()
    {
        var cat = await CreateCategoryAsync("OrderCat");
        // Advance clock slightly between creates so createdAt ordering is observable.
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "r1", key: "k-o1");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 1, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "r2", key: "k-o2");
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId);
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 2, TimeSpan.Zero));
        var r3 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 3)], "r3", key: "k-o3");
        await ActivateOutsideAsync(r3.Value!.Revision.PlanId, r3.Value.Revision.RevisionId);

        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.Equal(3, list.Value!.Items.Count);

        var ids = list.Value.Items.Select(i => i.RevisionId).ToArray();
        Assert.Equal(
            new[]
            {
                r1.Value.Revision.RevisionId,
                r2.Value!.Revision.RevisionId,
                r3.Value.Revision.RevisionId
            },
            ids);

        // Tie-breaker stability: re-list is deterministic.
        var again = await ListAsync(Period(2026, 7));
        Assert.Equal(ids, again.Value!.Items.Select(i => i.RevisionId));

        Assert.Equal(BudgetRevisionStatus.Superseded, list.Value.Items[0].Status);
        Assert.Equal(BudgetRevisionStatus.Draft, list.Value.Items[1].Status);
        Assert.Equal(BudgetRevisionStatus.Active, list.Value.Items[2].Status);
        Assert.All(list.Value.Items, i => Assert.Equal(BudgetPeriodState.Current, i.Period.State));
        Assert.All(list.Value.Items, i => Assert.Equal(r1.Value.Revision.PlanId, i.PlanId));
        Assert.Equal(1, list.Value.Items[0].PlannedTotalMinorUnits);
        Assert.Equal(2, list.Value.Items[1].PlannedTotalMinorUnits);
        Assert.Equal(3, list.Value.Items[2].PlannedTotalMinorUnits);
        Assert.All(list.Value.Items, i => Assert.Equal(1, i.EntryCount));
        Assert.True(list.Value.Items.Count <= ListBudgetPlanRevisionsQuery.MaxLimit);
    }

    // FR-BUDGET-PLAN-IDENTITY / NoBudgetPlan
    [Fact]
    public async Task List_no_budget_plan_returns_empty_success_not_not_found()
    {
        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.NotNull(list.Value);
        Assert.Empty(list.Value!.Items);
        Assert.Null(list.ErrorCode);
    }

    // FR-BUDGET-PLAN-HISTORY / no-active distinction
    [Fact]
    public async Task List_no_active_returns_drafts_without_collapsing_states()
    {
        var cat = await CreateCategoryAsync("DraftOnly");
        var a = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "k-d1");
        var b = await CreateDraftAsync(Period(2026, 7), [], "d2 empty", key: "k-d2");
        Assert.True(a.IsSuccess && b.IsSuccess);

        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.Equal(2, list.Value!.Items.Count);
        Assert.All(list.Value.Items, i => Assert.Equal(BudgetRevisionStatus.Draft, i.Status));
        Assert.DoesNotContain(list.Value.Items, i => i.Status == BudgetRevisionStatus.Active);
        Assert.Equal(1, list.Value.Items.Single(i => i.RevisionId == a.Value!.Revision.RevisionId).EntryCount);
        Assert.Equal(0, list.Value.Items.Single(i => i.RevisionId == b.Value!.Revision.RevisionId).EntryCount);
        Assert.Equal(0, list.Value.Items.Single(i => i.RevisionId == b.Value!.Revision.RevisionId).PlannedTotalMinorUnits);
    }

    // FR-BUDGET-PLAN-IDENTITY / period state classification on list
    [Fact]
    public async Task List_reports_current_future_and_closed_period_states()
    {
        var cat = await CreateCategoryAsync("PeriodStates");
        var pastClock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var pastCreate = new CreateBudgetDraftCommand(executor, ledger, pastClock);
        var june = await pastCreate.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 6),
                [Entry(cat.CategoryId, 1)],
                "june"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(june.IsSuccess, june.ErrorCode);

        var july = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "july", key: "k-jul");
        var august = await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 3)], "aug", key: "k-aug");
        Assert.True(july.IsSuccess && august.IsSuccess);

        var closed = await ListAsync(Period(2026, 6));
        var current = await ListAsync(Period(2026, 7));
        var future = await ListAsync(Period(2026, 8));

        Assert.True(closed.IsSuccess && current.IsSuccess && future.IsSuccess);
        Assert.Equal(BudgetPeriodState.Closed, Assert.Single(closed.Value!.Items).Period.State);
        Assert.Equal(BudgetPeriodState.Current, Assert.Single(current.Value!.Items).Period.State);
        Assert.Equal(BudgetPeriodState.Future, Assert.Single(future.Value!.Items).Period.State);
    }

    // FR-BUDGET-PLAN-HISTORY / closed history remains listable
    [Fact]
    public async Task List_closed_period_history_remains_readable()
    {
        var pastClock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 5, 8, 0, 0, TimeSpan.Zero));
        var pastCreate = new CreateBudgetDraftCommand(executor, ledger, pastClock);
        var cat = await CreateCategoryAsync("ClosedList");
        var r1 = await pastCreate.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 6),
                [Entry(cat.CategoryId, 10)],
                "c1"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.ErrorCode);
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId, pastClock);
        pastClock.Set(new DateTimeOffset(2026, 6, 6, 8, 0, 0, TimeSpan.Zero));
        var r2 = await pastCreate.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 6),
                [Entry(cat.CategoryId, 20)],
                "c2"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.ErrorCode);

        var list = await ListAsync(Period(2026, 6));
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.Equal(2, list.Value!.Items.Count);
        Assert.All(list.Value.Items, i => Assert.Equal(BudgetPeriodState.Closed, i.Period.State));
        Assert.Equal(BudgetRevisionStatus.Active, list.Value.Items[0].Status);
        Assert.Equal(BudgetRevisionStatus.Draft, list.Value.Items[1].Status);
    }

    // List does not call Ledger / no entry payloads
    [Fact]
    public async Task List_summaries_have_no_entry_payloads_and_survive_category_archive()
    {
        var cat = await CreateCategoryAsync("ListNoLedger");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 40)], "sum");
        Assert.True(created.IsSuccess);
        await ArchiveCategoryAsync(cat.CategoryId);

        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess, list.ErrorCode);
        var summary = Assert.Single(list.Value!.Items);
        Assert.Equal(40, summary.PlannedTotalMinorUnits);
        Assert.Equal(1, summary.EntryCount);
        Assert.Equal(BudgetRevisionStatus.Draft, summary.Status);
        // Summary contract has no Entries / CategoryLifecycle — totals only.
        Assert.Equal(created.Value!.Revision.RevisionId, summary.RevisionId);
    }

    // Status filter
    [Fact]
    public async Task List_status_filter_returns_only_matching_revisions()
    {
        var cat = await CreateCategoryAsync("FilterCat");
        var r1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "f1", key: "k-f1");
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId);
        var r2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "f2", key: "k-f2");
        Assert.True(r2.IsSuccess);

        var drafts = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                BudgetRevisionStatus.Draft,
                Limit: null),
            CancellationToken.None);
        var actives = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                BudgetRevisionStatus.Active,
                Limit: null),
            CancellationToken.None);

        Assert.True(drafts.IsSuccess && actives.IsSuccess);
        Assert.Equal([r2.Value!.Revision.RevisionId], drafts.Value!.Items.Select(i => i.RevisionId));
        Assert.Equal([r1.Value.Revision.RevisionId], actives.Value!.Items.Select(i => i.RevisionId));
    }

    // Limit validation
    [Fact]
    public async Task List_invalid_limit_returns_resource_limit()
    {
        var zero = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null, 0),
            CancellationToken.None);
        var over = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                null,
                ListBudgetPlanRevisionsQuery.MaxLimit + 1),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ResourceLimit, zero.ErrorCode);
        Assert.Equal(BudgetErrors.ResourceLimit, over.ErrorCode);
    }

    // Limit bounding — no cursor exists in the contract (DM-BUDGET-OPERATION-CONTRACTS)
    [Fact]
    public async Task List_limit_bounds_result_without_cursor()
    {
        var cat = await CreateCategoryAsync("PageCat");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "p1", key: "k-p1");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 1, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "p2", key: "k-p2");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 2, TimeSpan.Zero));
        var r3 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 3)], "p3", key: "k-p3");
        Assert.True(r1.IsSuccess && r2.IsSuccess && r3.IsSuccess);

        var page = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null, 2),
            CancellationToken.None);

        Assert.True(page.IsSuccess, page.ErrorCode);
        Assert.Equal(2, page.Value!.Items.Count);
        Assert.Equal(r1.Value!.Revision.RevisionId, page.Value.Items[0].RevisionId);
        Assert.Equal(r2.Value!.Revision.RevisionId, page.Value.Items[1].RevisionId);
        Assert.True(page.Value.Items.Count <= ListBudgetPlanRevisionsQuery.MaxLimit);
    }

    // DM-BUDGET-OPERATION-CONTRACTS / bounded list — exact row-count-equals-limit boundary
    [Fact]
    public async Task List_exact_boundary_returns_all_rows_when_row_count_equals_requested_limit()
    {
        var cat = await CreateCategoryAsync("ExactLimitCat");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "e1", key: "k-e1");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 1, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "e2", key: "k-e2");
        Assert.True(r1.IsSuccess && r2.IsSuccess);

        var exact = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null, 2),
            CancellationToken.None);

        Assert.True(exact.IsSuccess, exact.ErrorCode);
        Assert.Equal(2, exact.Value!.Items.Count);
        Assert.Equal(
            new[] { r1.Value!.Revision.RevisionId, r2.Value!.Revision.RevisionId },
            exact.Value.Items.Select(i => i.RevisionId));
        // Bounded contract carries no continuation cursor — nothing signals more remain (DM-BUDGET-OPERATION-CONTRACTS).
        Assert.True(exact.Value.Items.Count <= ListBudgetPlanRevisionsQuery.MaxLimit);
    }

    // Invalid period / omitted period
    [Fact]
    public async Task List_rejects_invalid_or_omitted_period_before_state_read()
    {
        var omitted = await listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, Period: null, null, null),
            CancellationToken.None);
        var usd = await ListAsync(new BudgetPeriodInput(2026, 7, "USD"));
        var badMonth = await ListAsync(new BudgetPeriodInput(2026, 13, "ZAR"));

        Assert.Equal(BudgetErrors.InvalidPeriod, omitted.ErrorCode);
        Assert.Equal(BudgetErrors.InvalidPeriod, usd.ErrorCode);
        Assert.Equal(BudgetErrors.InvalidPeriod, badMonth.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
    }

    // Cancellation
    [Fact]
    public async Task List_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            listRevisions.HandleAsync(
                new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null, null),
                cts.Token));
    }

    // List no mutation
    [Fact]
    public async Task List_does_not_mutate_or_reserve_idempotency()
    {
        var cat = await CreateCategoryAsync("ListNoMut");
        var created = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5)], "list-immut");
        Assert.True(created.IsSuccess);
        var plans = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revisions = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var events = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idem = await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");
        var active = await GetActiveRevisionIdAsync(created.Value!.Revision.PlanId);

        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess);

        Assert.Equal(plans, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revisions, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(events, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idem, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(active, await GetActiveRevisionIdAsync(created.Value.Revision.PlanId));
    }

    // Get after list consistency for mixed lifecycle
    [Fact]
    public async Task Get_and_list_agree_on_totals_for_mixed_lifecycle_history()
    {
        var a = await CreateCategoryAsync("AgreeA");
        var b = await CreateCategoryAsync("AgreeB");
        clock.Set(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, 100), Entry(b.CategoryId, 0)],
            "mixed-1",
            key: "k-ag1");
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId);
        clock.Set(new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(Period(2026, 7), [], "mixed-empty", key: "k-ag2");
        Assert.True(r1.IsSuccess && r2.IsSuccess);

        await RenameCategoryAsync(a.CategoryId, "AgreeA-Renamed");
        await ArchiveCategoryAsync(b.CategoryId);

        var list = await ListAsync(Period(2026, 7));
        Assert.True(list.IsSuccess);
        Assert.Equal(2, list.Value!.Items.Count);

        foreach (var summary in list.Value.Items)
        {
            var detail = await GetAsync(summary.RevisionId);
            Assert.True(detail.IsSuccess, detail.ErrorCode);
            Assert.Equal(summary.PlannedTotalMinorUnits, detail.Value!.PlannedTotalMinorUnits);
            Assert.Equal(summary.EntryCount, detail.Value.Entries.Count);
            Assert.Equal(summary.Status, detail.Value.Status);
            Assert.Equal(summary.CreatedAt, detail.Value.CreatedAt);
        }

        var activeDetail = await GetAsync(r1.Value.Revision.RevisionId);
        Assert.Equal("AgreeA-Renamed", activeDetail.Value!.Entries.Single(e => e.CategoryId == a.CategoryId).CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Archived, activeDetail.Value.Entries.Single(e => e.CategoryId == b.CategoryId).CurrentLifecycle);
        Assert.Equal(100, activeDetail.Value.PlannedTotalMinorUnits);
    }

    // Future period list/get
    [Fact]
    public async Task Get_and_list_future_period_revision()
    {
        var cat = await CreateCategoryAsync("FutureRead");
        var created = await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 8)], "future");
        Assert.True(created.IsSuccess);

        var get = await GetAsync(created.Value!.Revision.RevisionId);
        var list = await ListAsync(Period(2026, 8));

        Assert.True(get.IsSuccess && list.IsSuccess);
        Assert.Equal(BudgetPeriodState.Future, get.Value!.Period.State);
        Assert.Equal(BudgetPeriodState.Future, Assert.Single(list.Value!.Items).Period.State);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CommandResult<CreateDraftBudgetPlanResult>> CreateDraftAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason,
        string? key = null) =>
        createDraft.HandleAsync(
            new CreateDraftBudgetPlanInput(BudgetOperationIds.ContractVersion, period, entries, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private Task<CommandResult<BudgetPlanRevisionDetail>> GetAsync(string revisionId) =>
        getRevision.HandleAsync(
            new GetBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, revisionId),
            actor,
            CancellationToken.None);

    private Task<CommandResult<ListBudgetPlanRevisionsResult>> ListAsync(BudgetPeriodInput period) =>
        listRevisions.HandleAsync(
            new ListBudgetPlanRevisionsInput(BudgetOperationIds.ContractVersion, period, Status: null, Limit: null),
            CancellationToken.None);

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private async Task ActivateOutsideAsync(string planId, string revisionId, TimeProvider? clockOverride = null)
    {
        var when = (clockOverride ?? clock).GetUtcNow();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        var plan = await store.GetPlanAsync(connection, transaction, planId, CancellationToken.None)
            ?? throw new InvalidOperationException("Plan missing for activation helper.");
        var supersedeEventId = plan.ActiveRevisionId is null ? null : LedgerId.New().ToString();
        await store.ActivateRevisionAsync(
            connection,
            transaction,
            planId,
            revisionId,
            BudgetPlanRevision.FormatUtc(when),
            "activate for read test",
            actor.Kind,
            actor.Label,
            actor.RunId,
            LedgerId.New().ToString(),
            supersedeEventId,
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<string?> GetActiveRevisionIdAsync(string planId)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var plan = await store.GetPlanAsync(connection, null, planId, CancellationToken.None);
        return plan?.ActiveRevisionId;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
            new ArchiveCategoryInput(categoryId, "read-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "read-test"),
            NextKey(),
            LedgerJsonContext.Default.RenameCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? idempotencyKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)
            ?? throw new InvalidOperationException($"Missing operation {operationId}");
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
            ?? throw new InvalidOperationException("No result envelope");
        Assert.Equal(0, processResult.ExitCode);
        Assert.Equal("success", envelope.Outcome);
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"read-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset now) => this.now = now;

        public void Set(DateTimeOffset value) => now = value;

        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
