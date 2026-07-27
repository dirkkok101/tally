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
using Tally.Features.Budget.Plans.Activate;
using Tally.Features.Budget.Plans.CreateDraft;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Plans;

/// <summary>
/// TASK-BUDGET-ACTIVATION-LIFECYCLE / TC-BUDGET-PLAN-ACTIVATION-CONTRACT / FR-BUDGET-PLAN-ACTIVATION
/// Atomic Draft activation with supersession, category revalidation, attribution, and cutpoint recovery.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ActivateBudgetPlanRevisionCommandTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-activate-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-activate", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private CreateBudgetDraftCommand draftCommand = null!;
    private ActivateBudgetPlanRevisionCommand command = null!;
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

        // Mid-July 2026: July Current; August Future; June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        draftCommand = new CreateBudgetDraftCommand(executor, ledger, clock);
        command = new ActivateBudgetPlanRevisionCommand(executor, ledger, clock);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Domain policy ────────────────────────────────────────────────────────

    [Fact]
    public void Lifecycle_policy_allows_only_draft_to_active_and_active_to_superseded()
    {
        Assert.True(BudgetPlanLifecycle.IsActivatable(BudgetRevisionStatus.Draft));
        Assert.False(BudgetPlanLifecycle.IsActivatable(BudgetRevisionStatus.Active));
        Assert.False(BudgetPlanLifecycle.IsActivatable(BudgetRevisionStatus.Superseded));

        Assert.True(BudgetPlanLifecycle.IsAllowedTransition(BudgetRevisionStatus.Draft, BudgetRevisionStatus.Active));
        Assert.True(BudgetPlanLifecycle.IsAllowedTransition(BudgetRevisionStatus.Active, BudgetRevisionStatus.Superseded));
        Assert.False(BudgetPlanLifecycle.IsAllowedTransition(BudgetRevisionStatus.Draft, BudgetRevisionStatus.Superseded));
        Assert.False(BudgetPlanLifecycle.IsAllowedTransition(BudgetRevisionStatus.Superseded, BudgetRevisionStatus.Active));
        Assert.False(BudgetPlanLifecycle.IsAllowedTransition(BudgetRevisionStatus.Active, BudgetRevisionStatus.Draft));
    }

    [Fact]
    public void Lifecycle_policy_rejects_closed_period_and_non_draft_with_stable_codes()
    {
        Assert.Equal(
            BudgetErrors.InvalidPeriod,
            BudgetPlanLifecycle.ValidateActivationEligibility(BudgetRevisionStatus.Draft, BudgetPeriodState.Closed));
        Assert.Equal(
            BudgetErrors.Conflict,
            BudgetPlanLifecycle.ValidateActivationEligibility(BudgetRevisionStatus.Active, BudgetPeriodState.Current));
        Assert.Equal(
            BudgetErrors.Conflict,
            BudgetPlanLifecycle.ValidateActivationEligibility(BudgetRevisionStatus.Superseded, BudgetPeriodState.Future));
        Assert.Equal(
            BudgetErrors.RevisionNotFound,
            BudgetPlanLifecycle.ValidateActivationEligibility(null, BudgetPeriodState.Current));
        Assert.Null(
            BudgetPlanLifecycle.ValidateActivationEligibility(BudgetRevisionStatus.Draft, BudgetPeriodState.Current));
        Assert.Null(
            BudgetPlanLifecycle.ValidateActivationEligibility(BudgetRevisionStatus.Draft, BudgetPeriodState.Future));
    }

    [Fact]
    public void Lifecycle_policy_orders_supersession_before_activation_event_ids()
    {
        Assert.Equal(["act"], BudgetPlanLifecycle.OrderedActivationEventIds(null, "act"));
        Assert.Equal(["sup", "act"], BudgetPlanLifecycle.OrderedActivationEventIds("sup", "act"));
        Assert.True(BudgetPlanLifecycle.RequiresSupersession("rev-prior"));
        Assert.False(BudgetPlanLifecycle.RequiresSupersession(null));
        Assert.False(BudgetPlanLifecycle.RequiresSupersession(" "));
    }

    // ── Success paths ────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-ACTIVATION / first activation current period
    [Fact]
    public async Task Current_period_draft_activates_with_actor_reason_and_timestamp()
    {
        var cat = await CreateCategoryAsync("Groceries");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 12_500)], "july draft");
        Assert.True(draft.IsSuccess, draft.ErrorCode);

        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "activate july");

        Assert.True(result.IsSuccess, result.ErrorCode);
        var activated = result.Value!.Activated;
        Assert.Equal(BudgetRevisionStatus.Active, activated.Status);
        Assert.Equal(draft.Value.Revision.RevisionId, activated.RevisionId);
        Assert.Equal(draft.Value.Revision.PlanId, activated.PlanId);
        Assert.NotNull(activated.ActivatedAt);
        Assert.Null(activated.SupersededAt);
        Assert.Null(result.Value.Superseded);
        Assert.Equal(BudgetPeriodState.Current, activated.Period.State);
        Assert.Equal(12_500, activated.PlannedTotalMinorUnits);
        Assert.Equal(draft.Value.Revision.PayloadHash, activated.PayloadHash);

        Assert.Equal(activated.RevisionId, await GetActiveRevisionIdAsync(activated.PlanId));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var events = await store.GetLifecycleEventsAsync(connection, null, activated.PlanId, CancellationToken.None);
        var activateEvent = events.Single(e => e.EventType == BudgetPlanLifecycle.EventRevisionActivated);
        Assert.Equal(actor.Kind, activateEvent.ActorKind);
        Assert.Equal(actor.Label, activateEvent.ActorLabel);
        Assert.Equal(actor.RunId, activateEvent.ActorRunId);
        Assert.Equal("activate july", activateEvent.Reason);
        Assert.Equal(activated.ActivatedAt, activateEvent.OccurredAtUtc);
        Assert.Equal("Draft", activateEvent.PriorStatus);
        Assert.Equal("Active", activateEvent.ResultingStatus);
    }

    // FR-BUDGET-PLAN-ACTIVATION / future period
    [Fact]
    public async Task Future_period_draft_activates_successfully()
    {
        var cat = await CreateCategoryAsync("FutureCat");
        var draft = await CreateDraftAsync(Period(2026, 8), [Entry(cat.CategoryId, 100)], "future draft");
        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "activate future");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetPeriodState.Future, result.Value!.Activated.Period.State);
        Assert.Equal(BudgetRevisionStatus.Active, result.Value.Activated.Status);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    // FR-BUDGET-PLAN-ACTIVATION / empty draft
    [Fact]
    public async Task Empty_draft_activates_as_valid_zero_entry_active_plan()
    {
        var draft = await CreateDraftAsync(Period(2026, 7), [], "empty");
        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "activate empty");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Empty(result.Value!.Activated.Entries);
        Assert.Equal(0, result.Value.Activated.PlannedTotalMinorUnits);
        Assert.Equal(BudgetRevisionStatus.Active, result.Value.Activated.Status);
    }

    // FR-BUDGET-PLAN-ACTIVATION / replacement supersession
    [Fact]
    public async Task Activating_replacement_draft_supersedes_prior_active_atomically()
    {
        var cat = await CreateCategoryAsync("ReplaceCat");
        var firstDraft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "v1", key: "d-1");
        var first = await ActivateAsync(firstDraft.Value!.Revision.RevisionId, "activate v1", key: "a-1");
        Assert.True(first.IsSuccess, first.ErrorCode);

        var secondDraft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 99)], "v2", key: "d-2");
        var second = await ActivateAsync(secondDraft.Value!.Revision.RevisionId, "activate v2", key: "a-2");

        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Active, second.Value!.Activated.Status);
        Assert.Equal(secondDraft.Value.Revision.RevisionId, second.Value.Activated.RevisionId);
        Assert.NotNull(second.Value.Superseded);
        Assert.Equal(firstDraft.Value.Revision.RevisionId, second.Value.Superseded!.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Superseded, second.Value.Superseded.Status);
        Assert.Equal(10, second.Value.Superseded.PlannedTotalMinorUnits);

        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Superseded';"));
        Assert.Equal(second.Value.Activated.RevisionId, await GetActiveRevisionIdAsync(second.Value.Activated.PlanId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var prior = await store.GetRevisionAsync(
            connection, null, firstDraft.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(BudgetRevisionStatus.Superseded, prior!.Status);
        Assert.Equal(second.Value.Activated.RevisionId, prior.SupersededByRevisionId);
        Assert.NotNull(prior.SupersededAtUtc);

        // Immutable content of prior revision is unchanged.
        var priorEntries = await store.GetEntriesAsync(
            connection, null, firstDraft.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(10, priorEntries.Single().PlannedMinorUnits);
        Assert.Equal(firstDraft.Value.Revision.PayloadHash, prior.PayloadHash);
    }

    // FR-BUDGET-PLAN-ACTIVATION / ordered attributable events
    [Fact]
    public async Task Replacement_appends_ordered_supersede_then_activate_events()
    {
        var cat = await CreateCategoryAsync("EventOrder");
        var d1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "eo-d1");
        await ActivateAsync(d1.Value!.Revision.RevisionId, "a1", key: "eo-a1");
        var d2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "d2", key: "eo-d2");
        var second = await ActivateAsync(d2.Value!.Revision.RevisionId, "a2", key: "eo-a2");
        Assert.True(second.IsSuccess, second.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var events = await store.GetLifecycleEventsAsync(
            connection, null, d1.Value.Revision.PlanId, CancellationToken.None);
        // DraftCreated, RevisionActivated, DraftCreated, RevisionSuperseded, RevisionActivated
        Assert.Equal(5, events.Count);
        var lastTwo = events.TakeLast(2).ToArray();
        Assert.Equal(BudgetPlanLifecycle.EventRevisionSuperseded, lastTwo[0].EventType);
        Assert.Equal(d1.Value.Revision.RevisionId, lastTwo[0].RevisionId);
        Assert.Equal("Active", lastTwo[0].PriorStatus);
        Assert.Equal("Superseded", lastTwo[0].ResultingStatus);
        Assert.Equal(d2.Value.Revision.RevisionId, lastTwo[0].ReplacementRevisionId);
        Assert.Equal("a2", lastTwo[0].Reason);

        Assert.Equal(BudgetPlanLifecycle.EventRevisionActivated, lastTwo[1].EventType);
        Assert.Equal(d2.Value.Revision.RevisionId, lastTwo[1].RevisionId);
        Assert.Equal("Draft", lastTwo[1].PriorStatus);
        Assert.Equal("Active", lastTwo[1].ResultingStatus);
        Assert.True(lastTwo[0].EventSequence < lastTwo[1].EventSequence);
    }

    // FR-BUDGET-PLAN-ACTIVATION / exactly one Active after chain
    [Fact]
    public async Task Sequential_activations_leave_exactly_one_active_revision()
    {
        var cat = await CreateCategoryAsync("ChainCat");
        string? lastActive = null;
        for (var i = 1; i <= 3; i++)
        {
            var draft = await CreateDraftAsync(
                Period(2026, 7),
                [Entry(cat.CategoryId, i * 10)],
                $"d{i}",
                key: $"chain-d{i}");
            var activated = await ActivateAsync(draft.Value!.Revision.RevisionId, $"a{i}", key: $"chain-a{i}");
            Assert.True(activated.IsSuccess, activated.ErrorCode);
            lastActive = activated.Value!.Activated.RevisionId;
            Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
            Assert.Equal(lastActive, await GetActiveRevisionIdAsync(activated.Value.Activated.PlanId));
        }

        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Superseded';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.NotNull(lastActive);
    }

    // ── Validation failures ──────────────────────────────────────────────────

    // FR-BUDGET-PLAN-ACTIVATION / closed period
    [Fact]
    public async Task Closed_period_activation_fails_without_lifecycle_change()
    {
        var cat = await CreateCategoryAsync("ClosedCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5)], "will close");
        Assert.True(draft.IsSuccess, draft.ErrorCode);

        // Advance host time so July becomes Closed.
        clock.Now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var activeBefore = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';");
        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "too late");

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(activeBefore, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
        Assert.Null(await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
    }

    // FR-BUDGET-PLAN-ACTIVATION / missing authority
    [Fact]
    public async Task Missing_actor_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoActor");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var result = await command.HandleAsync(
            new ActivateBudgetPlanRevisionInput(
                BudgetOperationIds.ContractVersion,
                draft.Value!.Revision.RevisionId,
                "no actor"),
            actor: null,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
        Assert.Null(await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / blank actor
    [Fact]
    public async Task Blank_actor_label_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("BlankActor");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var result = await command.HandleAsync(
            new ActivateBudgetPlanRevisionInput(
                BudgetOperationIds.ContractVersion,
                draft.Value!.Revision.RevisionId,
                "blank actor"),
            new SafeActor("user", "  ", "run"),
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / missing reason
    [Fact]
    public async Task Missing_or_blank_reason_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoReason");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "   ");
        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / missing key
    [Fact]
    public async Task Missing_idempotency_key_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoKey");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var result = await command.HandleAsync(
            new ActivateBudgetPlanRevisionInput(
                BudgetOperationIds.ContractVersion,
                draft.Value!.Revision.RevisionId,
                "no key"),
            actor,
            "  ",
            CancellationToken.None);

        Assert.Equal(BudgetErrors.IdempotencyRequired, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / non-Draft
    [Fact]
    public async Task Activating_already_active_revision_fails_without_change()
    {
        var cat = await CreateCategoryAsync("AlreadyActive");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var first = await ActivateAsync(draft.Value!.Revision.RevisionId, "first", key: "aa-1");
        Assert.True(first.IsSuccess, first.ErrorCode);

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var second = await ActivateAsync(draft.Value.Revision.RevisionId, "again", key: "aa-2");

        Assert.Equal(BudgetErrors.Conflict, second.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.Value.Revision.RevisionId, await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / superseded cannot reactivate
    [Fact]
    public async Task Activating_superseded_revision_fails_without_change()
    {
        var cat = await CreateCategoryAsync("SupersededCat");
        var d1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "sup-d1");
        await ActivateAsync(d1.Value!.Revision.RevisionId, "a1", key: "sup-a1");
        var d2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "d2", key: "sup-d2");
        await ActivateAsync(d2.Value!.Revision.RevisionId, "a2", key: "sup-a2");

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var pointerBefore = await GetActiveRevisionIdAsync(d1.Value.Revision.PlanId);
        var result = await ActivateAsync(d1.Value.Revision.RevisionId, "reactivate", key: "sup-a3");

        Assert.Equal(BudgetErrors.Conflict, result.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(pointerBefore, await GetActiveRevisionIdAsync(d1.Value.Revision.PlanId));
        Assert.Equal(BudgetRevisionStatus.Superseded, await GetRevisionStatusAsync(d1.Value.Revision.RevisionId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / missing revision
    [Fact]
    public async Task Unknown_revision_id_fails_before_mutation()
    {
        var unknown = LedgerId.New().ToString();
        var result = await ActivateAsync(unknown, "missing");
        Assert.Equal(BudgetErrors.RevisionNotFound, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
    }

    // FR-BUDGET-PLAN-ACTIVATION / blank revision id
    [Fact]
    public async Task Blank_revision_id_fails_before_mutation()
    {
        var result = await ActivateAsync("  ", "blank id");
        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / archived at activation
    [Fact]
    public async Task Category_archived_after_draft_rejects_activation_and_preserves_prior_active()
    {
        var cat = await CreateCategoryAsync("WillArchive");
        var priorDraft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "prior", key: "arch-d0");
        var prior = await ActivateAsync(priorDraft.Value!.Revision.RevisionId, "activate prior", key: "arch-a0");
        Assert.True(prior.IsSuccess, prior.ErrorCode);

        var other = await CreateCategoryAsync("StillActive");
        // Draft that includes both — then archive one category before activate.
        var mixedDraft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(cat.CategoryId, 20), Entry(other.CategoryId, 5)],
            "mixed",
            key: "arch-d1");
        await ArchiveCategoryAsync(cat.CategoryId);

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var result = await ActivateAsync(mixedDraft.Value!.Revision.RevisionId, "should fail", key: "arch-a1");

        Assert.Equal(BudgetErrors.CategoryInactive, result.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(prior.Value!.Activated.RevisionId, await GetActiveRevisionIdAsync(prior.Value.Activated.PlanId));
        Assert.Equal(BudgetRevisionStatus.Active, await GetRevisionStatusAsync(prior.Value.Activated.RevisionId));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(mixedDraft.Value.Revision.RevisionId));
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / unknown category at activation
    [Fact]
    public async Task Unknown_category_on_draft_rejects_activation()
    {
        // Seed a draft whose entry references a category never created in Ledger.
        var unknownCat = LedgerId.New().ToString();
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedDraftWithCategoryAsync(planId, revisionId, unknownCat, planned: 7, categoryContractVersion: CategoryContractVersions.Current);

        var result = await ActivateAsync(revisionId, "unknown cat");
        Assert.Equal(BudgetErrors.CategoryUnknown, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(revisionId));
        Assert.Null(await GetActiveRevisionIdAsync(planId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / stale category contract
    [Fact]
    public async Task Stale_category_contract_version_rejects_activation()
    {
        var cat = await CreateCategoryAsync("StaleContract");
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedDraftWithCategoryAsync(planId, revisionId, cat.CategoryId, planned: 3, categoryContractVersion: "0.9");

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var result = await ActivateAsync(revisionId, "stale");

        Assert.Equal(BudgetErrors.LedgerIncompatible, result.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(revisionId));
        Assert.Null(await GetActiveRevisionIdAsync(planId));
    }

    // FR-BUDGET-PLAN-ACTIVATION / unsupported budget contract version
    [Fact]
    public async Task Unsupported_contract_version_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("BadVersion");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var result = await command.HandleAsync(
            new ActivateBudgetPlanRevisionInput(
                "9.9",
                draft.Value!.Revision.RevisionId,
                "bad version"),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.UnsupportedVersion, result.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value.Revision.RevisionId));
    }

    // ── Replay / conflict / cutpoints ────────────────────────────────────────

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / replay
    [Fact]
    public async Task Equivalent_activate_request_replays_exact_event_snapshots()
    {
        var cat = await CreateCategoryAsync("ReplayCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 42)], "draft");
        var key = "idem-activate-replay";
        var first = await ActivateAsync(draft.Value!.Revision.RevisionId, "go", key: key);
        var second = await ActivateAsync(draft.Value.Revision.RevisionId, "go", key: key);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.Activated.RevisionId, second.Value!.Activated.RevisionId);
        Assert.Equal(first.Value.Activated.ActivatedAt, second.Value.Activated.ActivatedAt);
        Assert.Equal(first.Value.Activated.PayloadHash, second.Value.Activated.PayloadHash);
        Assert.Equal(BudgetRevisionStatus.Active, second.Value.Activated.Status);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / replay after later supersession
    [Fact]
    public async Task Replay_after_later_replacement_returns_event_time_active_and_original_events()
    {
        var cat = await CreateCategoryAsync("ReplayLater");
        var d1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "rl-d1");
        var key = "idem-activate-later";
        var first = await ActivateAsync(d1.Value!.Revision.RevisionId, "a1", key: key);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var d2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "d2", key: "rl-d2");
        var replacement = await ActivateAsync(d2.Value!.Revision.RevisionId, "a2", key: "rl-a2");
        Assert.True(replacement.IsSuccess, replacement.ErrorCode);

        // Live: d1 is Superseded; replay of first activation must still return event-time Active.
        Assert.Equal(BudgetRevisionStatus.Superseded, await GetRevisionStatusAsync(d1.Value.Revision.RevisionId));
        var replay = await ActivateAsync(d1.Value.Revision.RevisionId, "a1", key: key);

        Assert.True(replay.IsSuccess, replay.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Active, replay.Value!.Activated.Status);
        Assert.Equal(first.Value!.Activated.RevisionId, replay.Value.Activated.RevisionId);
        Assert.Equal(first.Value.Activated.ActivatedAt, replay.Value.Activated.ActivatedAt);
        Assert.Null(replay.Value.Activated.SupersededAt);
        Assert.Null(replay.Value.Activated.SupersededByRevisionId);
        Assert.Null(replay.Value.Superseded);

        // Live state remains: exactly one Active (the replacement).
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(d2.Value.Revision.RevisionId, await GetActiveRevisionIdAsync(d1.Value.Revision.PlanId));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / replacement replay preserves supersession refs
    [Fact]
    public async Task Replacement_activate_replay_preserves_prior_active_and_event_ids()
    {
        var cat = await CreateCategoryAsync("ReplaySup");
        var d1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "rs-d1");
        await ActivateAsync(d1.Value!.Revision.RevisionId, "a1", key: "rs-a1");
        var d2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "d2", key: "rs-d2");
        var key = "idem-replace-replay";
        var first = await ActivateAsync(d2.Value!.Revision.RevisionId, "a2", key: key);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var eventsAfterFirst = await store.GetLifecycleEventsAsync(
            connection, null, d1.Value.Revision.PlanId, CancellationToken.None);
        var supersedeId = eventsAfterFirst.Single(e => e.EventType == BudgetPlanLifecycle.EventRevisionSuperseded).EventId;
        var activateId = eventsAfterFirst.Last(e => e.EventType == BudgetPlanLifecycle.EventRevisionActivated).EventId;

        var replay = await ActivateAsync(d2.Value.Revision.RevisionId, "a2", key: key);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        Assert.Equal(d1.Value.Revision.RevisionId, replay.Value!.Superseded!.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, replay.Value.Activated.Status);

        // No duplicate supersession/activation events on replay.
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionSuperseded';"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(supersedeId, eventsAfterFirst.Single(e => e.EventType == BudgetPlanLifecycle.EventRevisionSuperseded).EventId);
        Assert.Equal(activateId, eventsAfterFirst.Last(e => e.EventType == BudgetPlanLifecycle.EventRevisionActivated).EventId);
        Assert.True(first.IsSuccess);
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / conflict
    [Fact]
    public async Task Same_key_with_different_reason_conflicts_without_lifecycle_change()
    {
        var cat = await CreateCategoryAsync("ConflictCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        var key = "idem-activate-conflict";
        var first = await ActivateAsync(draft.Value!.Revision.RevisionId, "reason-a", key: key);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var conflict = await ActivateAsync(draft.Value.Revision.RevisionId, "reason-b", key: key);

        Assert.Equal(BudgetErrors.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / different revision same key
    [Fact]
    public async Task Same_key_with_different_revision_conflicts()
    {
        var cat = await CreateCategoryAsync("ConflictRev");
        var d1 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "d1", key: "cr-d1");
        var d2 = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 2)], "d2", key: "cr-d2");
        var key = "idem-rev-conflict";
        var first = await ActivateAsync(d1.Value!.Revision.RevisionId, "go", key: key);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var conflict = await ActivateAsync(d2.Value!.Revision.RevisionId, "go", key: key);
        Assert.Equal(BudgetErrors.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(d1.Value.Revision.RevisionId, await GetActiveRevisionIdAsync(d1.Value.Revision.PlanId));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(d2.Value.Revision.RevisionId));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / pre-commit cutpoint
    [Fact]
    public async Task Pre_commit_interruption_leaves_prior_or_no_active_and_key_reusable()
    {
        var cat = await CreateCategoryAsync("PreCommit");
        var priorDraft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "prior", key: "pc-d0");
        var prior = await ActivateAsync(priorDraft.Value!.Revision.RevisionId, "prior", key: "pc-a0");
        Assert.True(prior.IsSuccess, prior.ErrorCode);

        var nextDraft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 20)], "next", key: "pc-d1");
        var key = "idem-pre-commit";
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
            ActivateAsync(nextDraft.Value!.Revision.RevisionId, "cut", key: key));

        executor.FaultPoint = BudgetMutationFaultPoint.None;

        // Prior complete state remains authoritative; never multi-active.
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(prior.Value!.Activated.RevisionId, await GetActiveRevisionIdAsync(prior.Value.Activated.PlanId));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(nextDraft.Value!.Revision.RevisionId));
        Assert.Equal(0L, await CountAsync(
            "SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate' AND key_digest = '"
            + BudgetMutationCanonicalizer.DigestKey(key) + "';"));

        var retry = await ActivateAsync(nextDraft.Value.Revision.RevisionId, "cut", key: key);
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(nextDraft.Value.Revision.RevisionId, retry.Value!.Activated.RevisionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(BudgetRevisionStatus.Superseded, await GetRevisionStatusAsync(prior.Value.Activated.RevisionId));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / post-commit cutpoint
    [Fact]
    public async Task Post_commit_interruption_then_retry_replays_single_activation()
    {
        var cat = await CreateCategoryAsync("PostCommit");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 11)], "draft");
        var key = "idem-post-commit";
        executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;

        var fault = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
            ActivateAsync(draft.Value!.Revision.RevisionId, "go", key: key));
        Assert.Equal(BudgetMutationFaultPoint.AfterCommit, fault.Point);

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.Value!.Revision.RevisionId, await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));

        var replay = await ActivateAsync(draft.Value.Revision.RevisionId, "go", key: key);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Active, replay.Value!.Activated.Status);
        Assert.Equal(draft.Value.Revision.RevisionId, replay.Value.Activated.RevisionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    // First activation pre-commit leaves no multi-active
    [Fact]
    public async Task Pre_commit_fault_on_first_activation_leaves_no_active_revision()
    {
        var cat = await CreateCategoryAsync("FirstFault");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
            ActivateAsync(draft.Value!.Revision.RevisionId, "cut", key: "first-fault"));

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value!.Revision.RevisionId));
        Assert.Null(await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    // Success result includes category lifecycle evidence
    [Fact]
    public async Task Success_result_includes_supplemental_category_lifecycle_evidence()
    {
        var cat = await CreateCategoryAsync("EvidenceCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 8)], "draft");
        var result = await ActivateAsync(draft.Value!.Revision.RevisionId, "evidence");

        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = Assert.Single(result.Value!.Activated.CategoryLifecycle);
        Assert.Equal(cat.CategoryId, evidence.CategoryId);
        Assert.Equal("EvidenceCat", evidence.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, evidence.Lifecycle);
        Assert.Equal(CategoryContractVersions.Current, evidence.CategoryContractVersion);
    }

    // Cancellation before work does not mutate
    [Fact]
    public async Task Cancelled_token_before_handle_does_not_activate()
    {
        var cat = await CreateCategoryAsync("CancelCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "draft");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            command.HandleAsync(
                new ActivateBudgetPlanRevisionInput(
                    BudgetOperationIds.ContractVersion,
                    draft.Value!.Revision.RevisionId,
                    "cancel"),
                actor,
                NextKey(),
                cts.Token));

        Assert.Equal(BudgetRevisionStatus.Draft, await GetRevisionStatusAsync(draft.Value!.Revision.RevisionId));
        Assert.Null(await GetActiveRevisionIdAsync(draft.Value.Revision.PlanId));
    }

    // Domain reason helper
    [Fact]
    public void Lifecycle_reason_normalization_rejects_blank_and_control_characters()
    {
        Assert.True(BudgetPlanLifecycle.TryNormalizeReason(" ok ", out var normalized));
        Assert.Equal("ok", normalized);
        Assert.False(BudgetPlanLifecycle.TryNormalizeReason(" ", out _));
        Assert.False(BudgetPlanLifecycle.TryNormalizeReason("bad\nreason", out _));
        Assert.False(BudgetPlanLifecycle.TryNormalizeReason(new string('x', BudgetPlanLifecycle.MaxReasonLength + 1), out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
        command.HandleAsync(
            new ActivateBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, revisionId, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private async Task SeedDraftWithCategoryAsync(
        string planId,
        string revisionId,
        string categoryId,
        long planned,
        string categoryContractVersion)
    {
        var createdAt = BudgetPlanRevision.FormatUtc(clock.GetUtcNow());
        var entries = new[] { new BudgetPlanEntry(categoryId, planned) };
        var payloadHash = BudgetPlanRevision.ComputePayloadHash(categoryContractVersion, entries);

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
                categoryContractVersion,
                payloadHash,
                ActivatedAtUtc: null,
                SupersededAtUtc: null,
                SupersededByRevisionId: null),
            [new BudgetPlanEntryRow(revisionId, categoryId, planned)],
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
        var revision = await store.GetRevisionAsync(connection, null, revisionId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Revision {revisionId} missing.");
        return revision.Status;
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
            new ArchiveCategoryInput(categoryId, "activate-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
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

    private string NextKey() => $"activate-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
