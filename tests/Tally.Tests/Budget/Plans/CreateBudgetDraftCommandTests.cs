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
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Plans;

/// <summary>
/// TASK-BUDGET-DRAFT-CREATION / TC-BUDGET-PLAN-DRAFT-CONTRACT / FR-BUDGET-PLAN-DRAFT
/// Create immutable Draft Budget Plan Revisions through real BudgetStateStore + public Ledger client.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CreateBudgetDraftCommandTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-draft-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-draft", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private CreateBudgetDraftCommand command = null!;
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
        command = new CreateBudgetDraftCommand(executor, ledger, clock);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success paths ────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-DRAFT / current period
    [Fact]
    public async Task Current_period_draft_creates_plan_revision_entries_and_draft_created_event()
    {
        var groceries = await CreateCategoryAsync("Groceries");
        var travel = await CreateCategoryAsync("Travel");

        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(groceries.CategoryId, 12_500), Entry(travel.CategoryId, 3_000)],
            reason: "july plan");

        Assert.True(result.IsSuccess, result.ErrorCode);
        var revision = result.Value!.Revision;
        Assert.Equal(BudgetRevisionStatus.Draft, revision.Status);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(15_500, revision.PlannedTotalMinorUnits);
        Assert.Equal(2, revision.Entries.Count);
        Assert.Equal(["july plan"], [revision.Reason]);
        Assert.Equal(actor.Kind, revision.ActorKind);
        Assert.Equal(actor.Label, revision.ActorLabel);
        Assert.Equal(actor.RunId, revision.ActorRunId);
        Assert.Equal(CategoryContractVersions.Current, revision.CategoryContractVersion);
        Assert.Equal(64, revision.PayloadHash.Length);
        Assert.Null(revision.ActivatedAt);
        Assert.Equal(BudgetPeriodState.Current, revision.Period.State);
        Assert.Equal("2026-07-01", revision.Period.StartInclusive);
        Assert.Equal("2026-08-01", revision.Period.EndExclusive);

        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'DraftCreated';"));
        Assert.Null(await GetActiveRevisionIdAsync(revision.PlanId));
    }

    // FR-BUDGET-PLAN-DRAFT / future period
    [Fact]
    public async Task Future_period_draft_succeeds_without_activation()
    {
        var cat = await CreateCategoryAsync("FutureCat");
        var result = await HandleAsync(Period(2026, 8), [Entry(cat.CategoryId, 100)], "future plan");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetPeriodState.Future, result.Value!.Revision.Period.State);
        Assert.Equal(BudgetRevisionStatus.Draft, result.Value.Revision.Status);
        Assert.Null(await GetActiveRevisionIdAsync(result.Value.Revision.PlanId));
    }

    // FR-BUDGET-PLAN-DRAFT / empty draft
    [Fact]
    public async Task Empty_entry_collection_creates_distinct_empty_draft()
    {
        var result = await HandleAsync(Period(2026, 7), [], "empty draft");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Empty(result.Value!.Revision.Entries);
        Assert.Equal(0, result.Value.Revision.PlannedTotalMinorUnits);
        Assert.Empty(result.Value.Revision.CategoryLifecycle);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // FR-BUDGET-PLAN-DRAFT / all-zero
    [Fact]
    public async Task All_zero_draft_preserves_explicit_zero_rows_and_total()
    {
        var a = await CreateCategoryAsync("ZeroA");
        var b = await CreateCategoryAsync("ZeroB");

        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, 0), Entry(b.CategoryId, 0)],
            "all zero");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.Revision.Entries.Count);
        Assert.All(result.Value.Revision.Entries, e => Assert.Equal(0, e.PlannedMinorUnits));
        Assert.Equal(0, result.Value.Revision.PlannedTotalMinorUnits);
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM budget_plan_entry WHERE planned_minor_units = 0;"));
    }

    // FR-BUDGET-PLAN-DRAFT / explicit zero vs omission
    [Fact]
    public async Task Explicit_zero_is_stored_while_omitted_category_has_no_row()
    {
        var budgeted = await CreateCategoryAsync("Budgeted");
        var zeroed = await CreateCategoryAsync("ZeroBudget");
        var omitted = await CreateCategoryAsync("Unbudgeted");

        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(budgeted.CategoryId, 500), Entry(zeroed.CategoryId, 0)],
            "omit one");

        Assert.True(result.IsSuccess, result.ErrorCode);
        var ids = result.Value!.Revision.Entries.Select(e => e.CategoryId).ToArray();
        Assert.Contains(budgeted.CategoryId, ids);
        Assert.Contains(zeroed.CategoryId, ids);
        Assert.DoesNotContain(omitted.CategoryId, ids);
        Assert.Equal(0, result.Value.Revision.Entries.Single(e => e.CategoryId == zeroed.CategoryId).PlannedMinorUnits);
        Assert.Equal(500, result.Value.Revision.PlannedTotalMinorUnits);
    }

    // FR-BUDGET-PLAN-DRAFT / exact totals
    [Fact]
    public async Task Exact_minor_unit_amounts_round_trip_and_checked_total_matches()
    {
        var a = await CreateCategoryAsync("ExactA");
        var b = await CreateCategoryAsync("ExactB");
        var amounts = new[] { (a.CategoryId, 1L), (b.CategoryId, 99_999_999_999L) };

        var result = await HandleAsync(
            Period(2026, 7),
            amounts.Select(x => Entry(x.Item1, x.Item2)).ToArray(),
            "exact");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(100_000_000_000L, result.Value!.Revision.PlannedTotalMinorUnits);
        Assert.Equal(
            amounts.OrderBy(x => x.Item1, StringComparer.Ordinal).Select(x => (x.Item1, x.Item2)),
            result.Value.Revision.Entries.Select(e => (e.CategoryId, e.PlannedMinorUnits)));
    }

    // FR-BUDGET-PLAN-IDENTITY / plan reuse
    [Fact]
    public async Task Second_draft_for_same_period_reuses_plan_and_sequences_revision_number()
    {
        var cat = await CreateCategoryAsync("SeqCat");
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "r1", key: "k-seq-1");
        var second = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 20)], "r2", key: "k-seq-2");

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.Revision.PlanId, second.Value!.Revision.PlanId);
        Assert.Equal(1, first.Value.Revision.RevisionNumber);
        Assert.Equal(2, second.Value.Revision.RevisionNumber);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // FR-BUDGET-PLAN-DRAFT / revise draft
    [Fact]
    public async Task Revising_draft_content_creates_new_draft_without_mutating_source()
    {
        var cat = await CreateCategoryAsync("ReviseDraft");
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "v1", key: "k-rev-d1");
        var second = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 99)], "v2", key: "k-rev-d2");

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.NotEqual(first.Value!.Revision.RevisionId, second.Value!.Revision.RevisionId);
        Assert.Equal(10, first.Value.Revision.Entries.Single().PlannedMinorUnits);

        // Source row remains byte-stable.
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var source = await store.GetRevisionAsync(connection, null, first.Value.Revision.RevisionId, CancellationToken.None);
        var sourceEntries = await store.GetEntriesAsync(connection, null, first.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(BudgetRevisionStatus.Draft, source!.Status);
        Assert.Equal(10, sourceEntries.Single().PlannedMinorUnits);
        Assert.Equal(first.Value.Revision.PayloadHash, source.PayloadHash);
    }

    // FR-BUDGET-PLAN-DRAFT / revise active
    [Fact]
    public async Task Revising_while_active_exists_creates_new_draft_and_leaves_active_pointer()
    {
        var cat = await CreateCategoryAsync("ReviseActive");
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "seed", key: "k-act-1");
        Assert.True(first.IsSuccess, first.ErrorCode);
        await ActivateOutsideAsync(first.Value!.Revision.PlanId, first.Value.Revision.RevisionId);

        var before = await GetActiveRevisionIdAsync(first.Value.Revision.PlanId);
        var second = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 50)], "after active", key: "k-act-2");

        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, second.Value!.Revision.Status);
        Assert.Equal(before, await GetActiveRevisionIdAsync(first.Value.Revision.PlanId));
        Assert.Equal(before, first.Value.Revision.RevisionId);
        Assert.NotEqual(before, second.Value.Revision.RevisionId);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var activeEntries = await store.GetEntriesAsync(connection, null, before!, CancellationToken.None);
        Assert.Equal(10, activeEntries.Single().PlannedMinorUnits);
    }

    // FR-BUDGET-PLAN-DRAFT / revise superseded
    [Fact]
    public async Task Revising_after_supersession_creates_new_draft_without_copying_hidden_intent()
    {
        var cat = await CreateCategoryAsync("ReviseSuperseded");
        var r1 = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 11)], "s1", key: "k-sup-1");
        await ActivateOutsideAsync(r1.Value!.Revision.PlanId, r1.Value.Revision.RevisionId);
        var r2 = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 22)], "s2", key: "k-sup-2");
        await ActivateOutsideAsync(r2.Value!.Revision.PlanId, r2.Value.Revision.RevisionId);

        // r1 is superseded; new draft must not invent omitted categories or resurrect r1 content.
        var other = await CreateCategoryAsync("OnlyNew");
        var r3 = await HandleAsync(Period(2026, 7), [Entry(other.CategoryId, 33)], "s3", key: "k-sup-3");

        Assert.True(r3.IsSuccess, r3.ErrorCode);
        Assert.Equal(BudgetRevisionStatus.Draft, r3.Value!.Revision.Status);
        Assert.Equal([other.CategoryId], r3.Value.Revision.Entries.Select(e => e.CategoryId));
        Assert.DoesNotContain(r3.Value.Revision.Entries, e => e.CategoryId == cat.CategoryId);
        Assert.Equal(r2.Value.Revision.RevisionId, await GetActiveRevisionIdAsync(r1.Value.Revision.PlanId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var superseded = await store.GetRevisionAsync(connection, null, r1.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(BudgetRevisionStatus.Superseded, superseded!.Status);
        var supersededEntries = await store.GetEntriesAsync(connection, null, r1.Value.Revision.RevisionId, CancellationToken.None);
        Assert.Equal(11, supersededEntries.Single().PlannedMinorUnits);
    }

    // FR-BUDGET-PLAN-DRAFT / active pointer unchanged on first save
    [Fact]
    public async Task Successful_draft_does_not_activate_or_set_active_revision_id()
    {
        var cat = await CreateCategoryAsync("NoActivate");
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "no activate");
        Assert.True(result.IsSuccess);
        Assert.Null(await GetActiveRevisionIdAsync(result.Value!.Revision.PlanId));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    // NFR-BUDGET-ATTRIBUTABLE-HISTORY
    [Fact]
    public async Task DraftCreated_event_is_attributable_with_actor_reason_and_sequence()
    {
        var cat = await CreateCategoryAsync("AttrCat");
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 7)], "because planning");
        Assert.True(result.IsSuccess);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var events = await store.GetLifecycleEventsAsync(connection, null, result.Value!.Revision.PlanId, CancellationToken.None);
        var created = Assert.Single(events);
        Assert.Equal("DraftCreated", created.EventType);
        Assert.Equal(result.Value.Revision.RevisionId, created.RevisionId);
        Assert.Equal(actor.Kind, created.ActorKind);
        Assert.Equal(actor.Label, created.ActorLabel);
        Assert.Equal(actor.RunId, created.ActorRunId);
        Assert.Equal("because planning", created.Reason);
        Assert.Equal("Draft", created.ResultingStatus);
        Assert.Null(created.PriorStatus);
        Assert.Equal(1, created.EventSequence);
    }

    // ── Validation failures (no plan/revision mutation) ──────────────────────

    // FR-BUDGET-PLAN-DRAFT / closed period
    [Fact]
    public async Task Closed_period_fails_with_no_plan_or_revision_change()
    {
        var cat = await CreateCategoryAsync("ClosedCat");
        var beforePlans = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var result = await HandleAsync(Period(2026, 6), [Entry(cat.CategoryId, 10)], "closed");

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
        Assert.Equal(beforePlans, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    // FR-BUDGET-PLAN-DRAFT / negative amount
    [Fact]
    public async Task Negative_amount_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NegCat");
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, -1)], "neg");
        Assert.Equal(BudgetErrors.InvalidAmount, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / overflow total
    [Fact]
    public async Task Overflowing_checked_total_fails_before_mutation()
    {
        var a = await CreateCategoryAsync("OverA");
        var b = await CreateCategoryAsync("OverB");
        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, long.MaxValue), Entry(b.CategoryId, 1)],
            "overflow");

        Assert.Equal(BudgetErrors.InvalidAmount, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / duplicate category
    [Fact]
    public async Task Duplicate_category_ids_fail_before_mutation()
    {
        var cat = await CreateCategoryAsync("DupCat");
        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(cat.CategoryId, 1), Entry(cat.CategoryId, 2)],
            "dup");

        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / display-name-only
    [Fact]
    public async Task Display_name_only_category_reference_fails_before_mutation()
    {
        var result = await HandleAsync(
            Period(2026, 7),
            [new BudgetPlanEntryInput("Groceries", 100)],
            "by-name");

        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / unknown
    [Fact]
    public async Task Unknown_category_id_fails_before_mutation()
    {
        var unknown = LedgerId.New().ToString();
        var result = await HandleAsync(Period(2026, 7), [Entry(unknown, 10)], "unknown");
        Assert.Equal(BudgetErrors.CategoryUnknown, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / archived
    [Fact]
    public async Task Archived_category_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("ArchiveMe");
        await ArchiveCategoryAsync(cat.CategoryId);
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "archived");
        Assert.Equal(BudgetErrors.CategoryInactive, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / missing actor
    [Fact]
    public async Task Missing_actor_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoActor");
        var result = await command.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                [Entry(cat.CategoryId, 1)],
                "no actor"),
            actor: null,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / blank actor label
    [Fact]
    public async Task Blank_actor_label_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("BlankActor");
        var result = await command.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                [Entry(cat.CategoryId, 1)],
                "blank actor"),
            new SafeActor("user", "  ", "run"),
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / missing reason
    [Fact]
    public async Task Missing_or_blank_reason_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoReason");
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 1)], "   ");
        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / missing idempotency key
    [Fact]
    public async Task Missing_idempotency_key_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("NoKey");
        var result = await command.HandleAsync(
            new CreateDraftBudgetPlanInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                [Entry(cat.CategoryId, 1)],
                "no key"),
            actor,
            "  ",
            CancellationToken.None);

        Assert.Equal(BudgetErrors.IdempotencyRequired, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-IDENTITY / invalid currency
    [Fact]
    public async Task Invalid_period_currency_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("BadCurrency");
        var result = await HandleAsync(new BudgetPeriodInput(2026, 7, "USD"), [Entry(cat.CategoryId, 1)], "usd");
        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // FR-BUDGET-PLAN-DRAFT / unsupported contract version
    [Fact]
    public async Task Unsupported_contract_version_fails_before_mutation()
    {
        var cat = await CreateCategoryAsync("BadVersion");
        var result = await command.HandleAsync(
            new CreateDraftBudgetPlanInput(
                "9.9",
                Period(2026, 7),
                [Entry(cat.CategoryId, 1)],
                "bad version"),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.UnsupportedVersion, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // ── Replay / conflict / atomicity ────────────────────────────────────────

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / replay
    [Fact]
    public async Task Equivalent_request_with_same_key_replays_exact_revision_without_duplicate()
    {
        var cat = await CreateCategoryAsync("ReplayCat");
        var key = "idem-replay-1";
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 42)], "replay", key: key);
        var second = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 42)], "replay", key: key);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.Revision.RevisionId, second.Value!.Revision.RevisionId);
        Assert.Equal(first.Value.Revision.PayloadHash, second.Value.Revision.PayloadHash);
        Assert.Equal(first.Value.Revision.PlanId, second.Value.Revision.PlanId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / conflict
    [Fact]
    public async Task Same_key_with_different_content_conflicts_without_plan_change()
    {
        var cat = await CreateCategoryAsync("ConflictCat");
        var key = "idem-conflict-1";
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "a", key: key);
        var conflict = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 99)], "b", key: key);

        Assert.True(first.IsSuccess);
        Assert.Equal(BudgetErrors.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(10L, await ScalarLongAsync(
            $"SELECT planned_minor_units FROM budget_plan_entry WHERE revision_id = '{first.Value!.Revision.RevisionId}';"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / entry order normalization
    [Fact]
    public async Task Entry_order_does_not_create_duplicate_or_conflict_on_replay()
    {
        var a = await CreateCategoryAsync("OrderA");
        var b = await CreateCategoryAsync("OrderB");
        var key = "idem-order-1";
        var first = await HandleAsync(
            Period(2026, 7),
            [Entry(b.CategoryId, 2), Entry(a.CategoryId, 1)],
            "order",
            key: key);
        var second = await HandleAsync(
            Period(2026, 7),
            [Entry(a.CategoryId, 1), Entry(b.CategoryId, 2)],
            "order",
            key: key);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.Revision.RevisionId, second.Value!.Revision.RevisionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // Domain payload hash stability
    [Fact]
    public async Task Payload_hash_is_stable_for_equivalent_entries_and_independent_of_display_names()
    {
        var cat = await CreateCategoryAsync("HashName");
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "hash1", key: "k-hash-1");
        await RenameCategoryAsync(cat.CategoryId, "RenamedHashName");
        var second = await HandleAsync(Period(2026, 8), [Entry(cat.CategoryId, 100)], "hash2", key: "k-hash-2");

        Assert.True(first.IsSuccess && second.IsSuccess);
        var expected = BudgetPlanRevision.ComputePayloadHash(
            CategoryContractVersions.Current,
            [new BudgetPlanEntry(cat.CategoryId, 100)]);
        Assert.Equal(expected, first.Value!.Revision.PayloadHash);
        Assert.Equal(expected, second.Value!.Revision.PayloadHash);
    }

    // Empty vs all-zero distinction remains after persistence
    [Fact]
    public async Task Empty_and_all_zero_drafts_remain_distinct_after_persistence()
    {
        var cat = await CreateCategoryAsync("DistinctZero");
        var empty = await HandleAsync(Period(2026, 7), [], "empty", key: "k-empty");
        var zero = await HandleAsync(Period(2026, 8), [Entry(cat.CategoryId, 0)], "zero", key: "k-zero");

        Assert.True(empty.IsSuccess && zero.IsSuccess);
        Assert.Empty(empty.Value!.Revision.Entries);
        Assert.Single(zero.Value!.Revision.Entries);
        Assert.Equal(0, empty.Value.Revision.PlannedTotalMinorUnits);
        Assert.Equal(0, zero.Value.Revision.PlannedTotalMinorUnits);
        Assert.NotEqual(empty.Value.Revision.PayloadHash, zero.Value.Revision.PayloadHash);
    }

    // Failure after successful draft does not roll back prior work
    [Fact]
    public async Task Validation_failure_does_not_mutate_existing_plan_history()
    {
        var cat = await CreateCategoryAsync("KeepHistory");
        var first = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 5)], "keep", key: "k-keep");
        Assert.True(first.IsSuccess);
        var plans = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revisions = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;");

        var failed = await HandleAsync(Period(2026, 6), [Entry(cat.CategoryId, 5)], "closed fail", key: "k-keep-fail");
        Assert.Equal(BudgetErrors.InvalidPeriod, failed.ErrorCode);
        Assert.Equal(plans, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revisions, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // Category lifecycle evidence is supplemental on the success result
    [Fact]
    public async Task Success_result_includes_supplemental_active_category_lifecycle_evidence()
    {
        var cat = await CreateCategoryAsync("EvidenceCat");
        var result = await HandleAsync(Period(2026, 7), [Entry(cat.CategoryId, 8)], "evidence");
        Assert.True(result.IsSuccess);
        var evidence = Assert.Single(result.Value!.Revision.CategoryLifecycle);
        Assert.Equal(cat.CategoryId, evidence.CategoryId);
        Assert.Equal("EvidenceCat", evidence.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, evidence.Lifecycle);
        Assert.Equal(CategoryContractVersions.Current, evidence.CategoryContractVersion);
        Assert.Equal("EvidenceCat", result.Value.Revision.Entries.Single().CurrentDisplayName);
    }

    // Partial multi-category rejection: one archived fails whole request
    [Fact]
    public async Task Mixed_active_and_archived_categories_fail_entire_request_atomically()
    {
        var active = await CreateCategoryAsync("MixedActive");
        var archived = await CreateCategoryAsync("MixedArchived");
        await ArchiveCategoryAsync(archived.CategoryId);

        var result = await HandleAsync(
            Period(2026, 7),
            [Entry(active.CategoryId, 1), Entry(archived.CategoryId, 2)],
            "mixed");

        Assert.Equal(BudgetErrors.CategoryInactive, result.ErrorCode);
        await AssertNoBudgetMutationAsync();
    }

    // Domain helper coverage for payload total
    [Fact]
    public void Domain_payload_hash_and_total_helpers_are_order_insensitive_and_overflow_safe()
    {
        var entries = new[]
        {
            new BudgetPlanEntry("b", 2),
            new BudgetPlanEntry("a", 1)
        };
        var reverse = entries.Reverse().ToArray();
        Assert.Equal(
            BudgetPlanRevision.ComputePayloadHash("1.0", entries),
            BudgetPlanRevision.ComputePayloadHash("1.0", reverse));
        Assert.True(BudgetPlanRevision.TrySumPlannedMinorUnits(entries, out var total));
        Assert.Equal(3, total);
        Assert.False(BudgetPlanRevision.TrySumPlannedMinorUnits(
            [new BudgetPlanEntry("a", long.MaxValue), new BudgetPlanEntry("b", 1)],
            out _));
        Assert.False(BudgetPlanRevision.TrySumPlannedMinorUnits(
            [new BudgetPlanEntry("a", -1)],
            out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CommandResult<CreateDraftBudgetPlanResult>> HandleAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason,
        string? key = null) =>
        command.HandleAsync(
            new CreateDraftBudgetPlanInput(BudgetOperationIds.ContractVersion, period, entries, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private async Task AssertNoBudgetMutationAsync()
    {
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    private async Task ActivateOutsideAsync(string planId, string revisionId)
    {
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
            BudgetPlanRevision.FormatUtc(clock.GetUtcNow()),
            "activate for test",
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

    private async Task<long> ScalarLongAsync(string sql)
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
            new ArchiveCategoryInput(categoryId, "draft-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "draft-test"),
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

    private string NextKey() => $"draft-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
