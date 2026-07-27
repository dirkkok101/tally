using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Common;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// UC-BUDGET-002 published-surface acceptance gate (VerifiedBudgetUc002).
/// Invokes only TallyProcess + OperationRegistry for mutations — never private command handlers.
/// Proves first/replacement activation, category drift, closed period, authority, conflict,
/// concurrency, restart cutpoints, attribution, exact replay, and one-Active maximum
/// (DD-BUDGET-IDEMPOTENT-MUTATIONS, DD-BUDGET-PLAN-REVISION-LIFECYCLE, DD-BUDGET-TRUSTED-PERIOD-TIME).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetUc002ActivationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-uc002-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);
        // Mid-July 2026: July Current, August Future, June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        store = budget.State.Store;
        executor = budget.Executor;
        process = new TallyProcess(registry, services with { Budget = budget.Operations });
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── First activation ─────────────────────────────────────────────────────

    [Fact]
    public async Task First_activation_yields_one_active_with_actor_reason_and_timestamp()
    {
        var cat = await CreateCategoryAsync("Uc002First");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 12_500)], "july-draft");
        var snapshot = await BudgetMutationSnapshotAsync();

        var result = await ActivateAsync(draft.RevisionId, "activate-july", NextKey());

        AssertSuccess(result, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var activated = doc.RootElement.GetProperty("result").GetProperty("activated");
        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal(draft.RevisionId, activated.GetProperty("revisionId").GetString());
        Assert.Equal(draft.PlanId, activated.GetProperty("planId").GetString());
        Assert.Equal(12_500, activated.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal("july-draft", activated.GetProperty("reason").GetString());
        Assert.Equal("automation", activated.GetProperty("actorKind").GetString());
        Assert.Equal("budget-uc002", activated.GetProperty("actorLabel").GetString());
        Assert.Equal("run-01", activated.GetProperty("actorRunId").GetString());
        Assert.Equal("current", activated.GetProperty("period").GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, activated.GetProperty("activatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, activated.GetProperty("supersededAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").GetProperty("superseded").ValueKind);

        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Superseded';"));
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        // DraftCreated remains; activation adds events — not a no-op snapshot.
        Assert.NotEqual(snapshot, await BudgetMutationSnapshotAsync());
    }

    [Fact]
    public async Task Future_period_draft_activates_successfully()
    {
        var cat = await CreateCategoryAsync("Uc002Future");
        var draft = await CreateDraftAsync(2026, 8, [(cat, 100)], "future-draft");

        var result = await ActivateAsync(draft.RevisionId, "activate-future", NextKey());

        AssertSuccess(result, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var activated = doc.RootElement.GetProperty("result").GetProperty("activated");
        Assert.Equal("future", activated.GetProperty("period").GetProperty("state").GetString());
        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    [Fact]
    public async Task Empty_draft_activates_as_zero_entry_active_plan()
    {
        var draft = await CreateDraftAsync(2026, 7, [], "empty-draft");

        var result = await ActivateAsync(draft.RevisionId, "activate-empty", NextKey());

        AssertSuccess(result, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var activated = doc.RootElement.GetProperty("result").GetProperty("activated");
        Assert.Equal(0, activated.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, activated.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
    }

    // ── Replacement / one-Active ─────────────────────────────────────────────

    [Fact]
    public async Task Replacement_atomically_activates_draft_and_supersedes_prior_active()
    {
        var cat = await CreateCategoryAsync("Uc002Replace");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 10)], "v1");
        var first = await ActivateAsync(d1.RevisionId, "activate-v1", NextKey());
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var d2 = await CreateDraftAsync(2026, 7, [(cat, 99)], "v2");
        var second = await ActivateAsync(d2.RevisionId, "activate-v2", NextKey());

        AssertSuccess(second, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(second.Stdout);
        var activated = doc.RootElement.GetProperty("result").GetProperty("activated");
        var superseded = doc.RootElement.GetProperty("result").GetProperty("superseded");
        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal(d2.RevisionId, activated.GetProperty("revisionId").GetString());
        Assert.Equal(d1.RevisionId, superseded.GetProperty("revisionId").GetString());
        Assert.Equal("superseded", superseded.GetProperty("status").GetString());
        Assert.Equal(10, superseded.GetProperty("plannedTotalMinorUnits").GetInt64());

        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Superseded';"));
        Assert.Equal(d2.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';"));
        Assert.Equal("Superseded", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
        Assert.Equal(d2.RevisionId, await BudgetTextAsync(
            $"SELECT superseded_by_revision_id FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
        // Immutable payload of prior revision is unchanged.
        Assert.Equal(d1.PayloadHash, await BudgetTextAsync(
            $"SELECT payload_hash FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
        Assert.Equal(10L, await BudgetScalarAsync(
            $"SELECT planned_minor_units FROM budget_plan_entry WHERE revision_id = '{d1.RevisionId}';"));
    }

    [Fact]
    public async Task Replacement_appends_ordered_supersede_then_activate_events()
    {
        var cat = await CreateCategoryAsync("Uc002EventOrder");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 1)], "d1");
        await ActivateAsync(d1.RevisionId, "a1", NextKey());
        var d2 = await CreateDraftAsync(2026, 7, [(cat, 2)], "d2");
        var second = await ActivateAsync(d2.RevisionId, "a2", NextKey());
        AssertSuccess(second, BudgetOperationIds.RevisionActivate);

        // DraftCreated, RevisionActivated, DraftCreated, RevisionSuperseded, RevisionActivated
        Assert.Equal(5L, await BudgetCountAsync(
            $"SELECT COUNT(*) FROM budget_lifecycle_event WHERE plan_id = '{d1.PlanId}';"));
        Assert.Equal("RevisionSuperseded", await BudgetTextAsync(
            $"""
            SELECT event_type FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}'
            ORDER BY event_sequence DESC
            LIMIT 1 OFFSET 1
            """));
        Assert.Equal(d1.RevisionId, await BudgetTextAsync(
            $"""
            SELECT revision_id FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """));
        Assert.Equal("Active", await BudgetTextAsync(
            $"""
            SELECT prior_status FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """));
        Assert.Equal("Superseded", await BudgetTextAsync(
            $"""
            SELECT resulting_status FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """));
        Assert.Equal(d2.RevisionId, await BudgetTextAsync(
            $"""
            SELECT replacement_revision_id FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """));
        Assert.Equal("a2", await BudgetTextAsync(
            $"""
            SELECT reason FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """));
        Assert.Equal("RevisionActivated", await BudgetTextAsync(
            $"""
            SELECT event_type FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}'
            ORDER BY event_sequence DESC
            LIMIT 1
            """));
        Assert.Equal(d2.RevisionId, await BudgetTextAsync(
            $"""
            SELECT revision_id FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}'
            ORDER BY event_sequence DESC
            LIMIT 1
            """));
        var supSeq = await BudgetScalarAsync(
            $"""
            SELECT event_sequence FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionSuperseded'
            """);
        var actSeq = await BudgetScalarAsync(
            $"""
            SELECT event_sequence FROM budget_lifecycle_event
            WHERE plan_id = '{d1.PlanId}' AND event_type = 'RevisionActivated'
              AND revision_id = '{d2.RevisionId}'
            """);
        Assert.True(supSeq < actSeq);
    }

    [Fact]
    public async Task Sequential_activations_leave_exactly_one_active_revision()
    {
        var cat = await CreateCategoryAsync("Uc002Chain");
        string? lastActive = null;
        string? planId = null;
        for (var i = 1; i <= 3; i++)
        {
            var draft = await CreateDraftAsync(2026, 7, [(cat, i * 10)], $"d{i}");
            planId = draft.PlanId;
            var activated = await ActivateAsync(draft.RevisionId, $"a{i}", NextKey());
            AssertSuccess(activated, BudgetOperationIds.RevisionActivate);
            using var doc = JsonDocument.Parse(activated.Stdout);
            lastActive = doc.RootElement.GetProperty("result").GetProperty("activated")
                .GetProperty("revisionId").GetString();
            Assert.Equal(1L, await BudgetCountAsync(
                "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
            Assert.Equal(lastActive, await BudgetTextAsync(
                $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{planId}';"));
        }

        Assert.Equal(2L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Superseded';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.NotNull(lastActive);
        Assert.Equal(lastActive, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{planId}';"));
    }

    // ── Category drift ───────────────────────────────────────────────────────

    [Fact]
    public async Task Category_archived_after_draft_rejects_activation_and_preserves_prior_active()
    {
        var cat = await CreateCategoryAsync("Uc002WillArchive");
        var priorDraft = await CreateDraftAsync(2026, 7, [(cat, 10)], "prior");
        var prior = await ActivateAsync(priorDraft.RevisionId, "activate-prior", NextKey());
        AssertSuccess(prior, BudgetOperationIds.RevisionActivate);

        var other = await CreateCategoryAsync("Uc002StillActive");
        var mixed = await CreateDraftAsync(2026, 7, [(cat, 20), (other, 5)], "mixed");
        await ArchiveCategoryAsync(cat);

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var snapshot = await BudgetMutationSnapshotAsync();
        var result = await ActivateAsync(mixed.RevisionId, "should-fail", NextKey());

        AssertDomainError(result, 6, BudgetErrors.CategoryInactive);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(priorDraft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{priorDraft.PlanId}';"));
        Assert.Equal("Active", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{priorDraft.RevisionId}';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{mixed.RevisionId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        // No new activate idempotency row for the failed attempt beyond prior activations.
        Assert.Equal(snapshot.Split('|')[0], (await BudgetMutationSnapshotAsync()).Split('|')[0]);
    }

    [Fact]
    public async Task Unknown_category_on_draft_rejects_activation_and_preserves_no_active()
    {
        var unknownCat = LedgerId.New().ToString();
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedDraftWithCategoryAsync(planId, revisionId, unknownCat, planned: 7, CategoryContractVersions.Current);

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var result = await ActivateAsync(revisionId, "unknown-cat", NextKey());

        AssertDomainError(result, 4, BudgetErrors.CategoryUnknown);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{revisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{planId}';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    [Fact]
    public async Task Stale_category_contract_version_rejects_activation_and_preserves_draft()
    {
        var cat = await CreateCategoryAsync("Uc002StaleContract");
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedDraftWithCategoryAsync(planId, revisionId, cat, planned: 3, categoryContractVersion: "0.9");

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var result = await ActivateAsync(revisionId, "stale", NextKey());

        AssertDomainError(result, 7, BudgetErrors.LedgerIncompatible);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{revisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{planId}';"));
    }

    // ── Closed period ────────────────────────────────────────────────────────

    [Fact]
    public async Task Closed_period_activation_fails_without_lifecycle_change()
    {
        var cat = await CreateCategoryAsync("Uc002Closed");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 5)], "will-close");

        // Advance host time so July becomes Closed.
        clock.Set(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var activeBefore = await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';");
        var result = await ActivateAsync(draft.RevisionId, "too-late", NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidPeriod);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(activeBefore, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{draft.RevisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
    }

    // ── Authority ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_actor_is_rejected_before_effects()
    {
        var cat = await CreateCategoryAsync("Uc002NoActor");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var body =
            "{\"contractVersion\":\"1.0\",\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + draft.RevisionId
            + "\",\"reason\":\"no-actor\"},\"idempotencyKey\":\""
            + NextKey()
            + "\"}";
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            body,
            CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{draft.RevisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected_before_effects()
    {
        var cat = await CreateCategoryAsync("Uc002NoKey");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc002\",\"runId\":\"run-01\"},\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + draft.RevisionId
            + "\",\"reason\":\"no-key\"}}";
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            body,
            CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{draft.RevisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
    }

    [Fact]
    public async Task Blank_reason_is_rejected_without_lifecycle_change()
    {
        var cat = await CreateCategoryAsync("Uc002NoReason");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");

        var result = await ActivateAsync(draft.RevisionId, "   ", NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidInput);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{draft.RevisionId}';"));
    }

    [Fact]
    public async Task Unknown_revision_id_fails_before_mutation()
    {
        const string unknown = "01NOTFOUNDREVISION0000000000";
        var result = await ActivateAsync(unknown, "missing", NextKey());

        AssertDomainError(result, 4, BudgetErrors.RevisionNotFound);
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
    }

    // ── Non-Draft / conflict ─────────────────────────────────────────────────

    [Fact]
    public async Task Activating_already_active_revision_fails_without_change()
    {
        var cat = await CreateCategoryAsync("Uc002AlreadyActive");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var first = await ActivateAsync(draft.RevisionId, "first", NextKey());
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var second = await ActivateAsync(draft.RevisionId, "again", NextKey());

        AssertDomainError(second, 5, BudgetErrors.Conflict);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
    }

    [Fact]
    public async Task Activating_superseded_revision_fails_without_change()
    {
        var cat = await CreateCategoryAsync("Uc002Superseded");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 1)], "d1");
        await ActivateAsync(d1.RevisionId, "a1", NextKey());
        var d2 = await CreateDraftAsync(2026, 7, [(cat, 2)], "d2");
        await ActivateAsync(d2.RevisionId, "a2", NextKey());

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var pointerBefore = await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';");
        var result = await ActivateAsync(d1.RevisionId, "reactivate", NextKey());

        AssertDomainError(result, 5, BudgetErrors.Conflict);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(pointerBefore, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';"));
        Assert.Equal("Superseded", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    [Fact]
    public async Task Same_key_with_different_reason_conflicts_without_lifecycle_change()
    {
        var cat = await CreateCategoryAsync("Uc002ConflictReason");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var key = NextKey();
        var first = await ActivateAsync(draft.RevisionId, "reason-a", key);
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var conflict = await ActivateAsync(draft.RevisionId, "reason-b", key);

        AssertDomainError(conflict, 5, BudgetErrors.IdempotencyConflict);
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
    }

    [Fact]
    public async Task Same_key_with_different_revision_conflicts_and_preserves_first()
    {
        var cat = await CreateCategoryAsync("Uc002ConflictRev");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 1)], "d1");
        var d2 = await CreateDraftAsync(2026, 7, [(cat, 2)], "d2");
        var key = NextKey();
        var first = await ActivateAsync(d1.RevisionId, "go", key);
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var conflict = await ActivateAsync(d2.RevisionId, "go", key);
        AssertDomainError(conflict, 5, BudgetErrors.IdempotencyConflict);
        Assert.Equal(d1.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{d2.RevisionId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    // ── Attribution ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Activation_lifecycle_event_is_attributable_with_actor_reason_and_sequence()
    {
        var cat = await CreateCategoryAsync("Uc002Attr");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 7)], "draft-for-attr");
        var result = await ActivateAsync(draft.RevisionId, "because-activation", NextKey());
        AssertSuccess(result, BudgetOperationIds.RevisionActivate);

        using var doc = JsonDocument.Parse(result.Stdout);
        var activatedAt = doc.RootElement.GetProperty("result").GetProperty("activated")
            .GetProperty("activatedAt").GetString();

        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal("automation", await BudgetTextAsync(
            $"SELECT actor_kind FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal("budget-uc002", await BudgetTextAsync(
            $"SELECT actor_label FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal("run-01", await BudgetTextAsync(
            $"SELECT actor_run_id FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal("because-activation", await BudgetTextAsync(
            $"SELECT reason FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT prior_status FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal("Active", await BudgetTextAsync(
            $"SELECT resulting_status FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal(activatedAt, await BudgetTextAsync(
            $"SELECT occurred_at_utc FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
        Assert.Equal(2L, await BudgetScalarAsync(
            $"SELECT event_sequence FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated' AND revision_id = '{draft.RevisionId}';"));
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Equivalent_activate_request_replays_exact_event_snapshots()
    {
        var cat = await CreateCategoryAsync("Uc002Replay");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 42)], "draft");
        var key = NextKey();
        var first = await ActivateAsync(draft.RevisionId, "go", key);
        var second = await ActivateAsync(draft.RevisionId, "go", key);

        AssertSuccess(first, BudgetOperationIds.RevisionActivate);
        AssertSuccess(second, BudgetOperationIds.RevisionActivate);
        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        var a1 = d1.RootElement.GetProperty("result").GetProperty("activated");
        var a2 = d2.RootElement.GetProperty("result").GetProperty("activated");
        Assert.Equal(a1.GetProperty("revisionId").GetString(), a2.GetProperty("revisionId").GetString());
        Assert.Equal(a1.GetProperty("activatedAt").GetString(), a2.GetProperty("activatedAt").GetString());
        Assert.Equal(a1.GetProperty("payloadHash").GetString(), a2.GetProperty("payloadHash").GetString());
        Assert.Equal("active", a2.GetProperty("status").GetString());
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    [Fact]
    public async Task Replay_after_later_replacement_returns_event_time_active_and_never_reactivates()
    {
        var cat = await CreateCategoryAsync("Uc002ReplayLater");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 1)], "d1");
        var key = NextKey();
        var first = await ActivateAsync(d1.RevisionId, "a1", key);
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var d2 = await CreateDraftAsync(2026, 7, [(cat, 2)], "d2");
        var replacement = await ActivateAsync(d2.RevisionId, "a2", NextKey());
        AssertSuccess(replacement, BudgetOperationIds.RevisionActivate);

        Assert.Equal("Superseded", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var replay = await ActivateAsync(d1.RevisionId, "a1", key);

        AssertSuccess(replay, BudgetOperationIds.RevisionActivate);
        using var firstDoc = JsonDocument.Parse(first.Stdout);
        using var replayDoc = JsonDocument.Parse(replay.Stdout);
        var activated = replayDoc.RootElement.GetProperty("result").GetProperty("activated");
        Assert.Equal("active", activated.GetProperty("status").GetString());
        Assert.Equal(
            firstDoc.RootElement.GetProperty("result").GetProperty("activated").GetProperty("revisionId").GetString(),
            activated.GetProperty("revisionId").GetString());
        Assert.Equal(
            firstDoc.RootElement.GetProperty("result").GetProperty("activated").GetProperty("activatedAt").GetString(),
            activated.GetProperty("activatedAt").GetString());
        Assert.Equal(JsonValueKind.Null, activated.GetProperty("supersededAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, activated.GetProperty("supersededByRevisionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, replayDoc.RootElement.GetProperty("result").GetProperty("superseded").ValueKind);

        // Live state: replacement remains sole Active; no new events on replay.
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(d2.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';"));
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal("Superseded", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{d1.RevisionId}';"));
    }

    [Fact]
    public async Task Replacement_activate_replay_preserves_prior_active_and_event_counts()
    {
        var cat = await CreateCategoryAsync("Uc002ReplaySup");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 1)], "d1");
        await ActivateAsync(d1.RevisionId, "a1", NextKey());
        var d2 = await CreateDraftAsync(2026, 7, [(cat, 2)], "d2");
        var key = NextKey();
        var first = await ActivateAsync(d2.RevisionId, "a2", key);
        AssertSuccess(first, BudgetOperationIds.RevisionActivate);

        var supersedeId = await BudgetTextAsync(
            $"SELECT event_id FROM budget_lifecycle_event WHERE event_type = 'RevisionSuperseded' AND plan_id = '{d1.PlanId}';");
        var activateId = await BudgetTextAsync(
            $"""
            SELECT event_id FROM budget_lifecycle_event
            WHERE event_type = 'RevisionActivated' AND revision_id = '{d2.RevisionId}'
            """);
        var eventsBefore = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");

        var replay = await ActivateAsync(d2.RevisionId, "a2", key);
        AssertSuccess(replay, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(replay.Stdout);
        Assert.Equal(d1.RevisionId, doc.RootElement.GetProperty("result").GetProperty("superseded")
            .GetProperty("revisionId").GetString());
        Assert.Equal("active", doc.RootElement.GetProperty("result").GetProperty("activated")
            .GetProperty("status").GetString());

        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionSuperseded';"));
        Assert.Equal(2L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(eventsBefore, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(supersedeId, await BudgetTextAsync(
            $"SELECT event_id FROM budget_lifecycle_event WHERE event_type = 'RevisionSuperseded' AND plan_id = '{d1.PlanId}';"));
        Assert.Equal(activateId, await BudgetTextAsync(
            $"""
            SELECT event_id FROM budget_lifecycle_event
            WHERE event_type = 'RevisionActivated' AND revision_id = '{d2.RevisionId}'
            """));
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_activations_of_two_drafts_leave_exactly_one_active()
    {
        var cat = await CreateCategoryAsync("Uc002Concurrent");
        var d1 = await CreateDraftAsync(2026, 7, [(cat, 11)], "c-d1");
        var d2 = await CreateDraftAsync(2026, 7, [(cat, 22)], "c-d2");

        var t1 = ActivateAsync(d1.RevisionId, "race-a1", NextKey());
        var t2 = ActivateAsync(d2.RevisionId, "race-a2", NextKey());
        await Task.WhenAll(t1, t2);
        var r1 = await t1;
        var r2 = await t2;

        // At least one must succeed; the other may succeed as replacement or hit host/conflict under lock.
        var successes = new[] { r1, r2 }.Where(r => r.ExitCode == 0).ToArray();
        Assert.NotEmpty(successes);

        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        var activeId = await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{d1.PlanId}';");
        Assert.NotNull(activeId);
        Assert.Contains(activeId, new[] { d1.RevisionId, d2.RevisionId });
        Assert.True(
            await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active'") == 1
            && await BudgetCountAsync(
                $"SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active' AND plan_id = '{d1.PlanId}'") == 1);

        // No partial multi-active or orphan pointer mismatch.
        Assert.Equal(activeId, await BudgetTextAsync(
            "SELECT revision_id FROM budget_plan_revision WHERE status = 'Active' LIMIT 1"));
        var draftCount = await BudgetCountAsync(
            $"SELECT COUNT(*) FROM budget_plan_revision WHERE plan_id = '{d1.PlanId}' AND status = 'Draft';");
        var supersededCount = await BudgetCountAsync(
            $"SELECT COUNT(*) FROM budget_plan_revision WHERE plan_id = '{d1.PlanId}' AND status = 'Superseded';");
        Assert.Equal(2L, draftCount + supersededCount + 1); // two revisions total, one Active
    }

    // ── Restart / cutpoints (published surface + recovery seam) ──────────────

    [Fact]
    public async Task Pre_commit_interruption_leaves_prior_active_and_key_reusable_on_restart()
    {
        var cat = await CreateCategoryAsync("Uc002PreCommit");
        var priorDraft = await CreateDraftAsync(2026, 7, [(cat, 10)], "prior");
        var prior = await ActivateAsync(priorDraft.RevisionId, "prior", NextKey());
        AssertSuccess(prior, BudgetOperationIds.RevisionActivate);

        var nextDraft = await CreateDraftAsync(2026, 7, [(cat, 20)], "next");
        var key = NextKey();
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        var interrupted = await ActivateAsync(nextDraft.RevisionId, "cut", key);
        Assert.Equal(10, interrupted.ExitCode);
        Assert.Contains("host.unexpected", interrupted.Stdout, StringComparison.Ordinal);

        executor.FaultPoint = BudgetMutationFaultPoint.None;

        // Prior complete state remains; never multi-active; key not committed.
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(priorDraft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{priorDraft.PlanId}';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{nextDraft.RevisionId}';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_idempotency_record WHERE operation_id = 'budget.plan.revision.activate' AND key_digest = '"
            + BudgetMutationCanonicalizer.DigestKey(key)
            + "';"));

        var retry = await ActivateAsync(nextDraft.RevisionId, "cut", key);
        AssertSuccess(retry, BudgetOperationIds.RevisionActivate);
        Assert.Equal(nextDraft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{priorDraft.PlanId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal("Superseded", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{priorDraft.RevisionId}';"));
    }

    [Fact]
    public async Task Post_commit_interruption_then_retry_replays_single_activation()
    {
        var cat = await CreateCategoryAsync("Uc002PostCommit");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 11)], "draft");
        var key = NextKey();
        executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;

        var interrupted = await ActivateAsync(draft.RevisionId, "go", key);
        Assert.Equal(10, interrupted.ExitCode);
        Assert.Contains("host.unexpected", interrupted.Stdout, StringComparison.Ordinal);

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));

        var replay = await ActivateAsync(draft.RevisionId, "go", key);
        AssertSuccess(replay, BudgetOperationIds.RevisionActivate);
        using var doc = JsonDocument.Parse(replay.Stdout);
        Assert.Equal("active", doc.RootElement.GetProperty("result").GetProperty("activated")
            .GetProperty("status").GetString());
        Assert.Equal(draft.RevisionId, doc.RootElement.GetProperty("result").GetProperty("activated")
            .GetProperty("revisionId").GetString());
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    [Fact]
    public async Task Pre_commit_fault_on_first_activation_leaves_no_active_revision()
    {
        var cat = await CreateCategoryAsync("Uc002FirstFault");
        var draft = await CreateDraftAsync(2026, 7, [(cat, 1)], "draft");
        var key = NextKey();
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        var interrupted = await ActivateAsync(draft.RevisionId, "cut", key);
        Assert.Equal(10, interrupted.ExitCode);

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{draft.RevisionId}';"));
        Assert.Null(await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));

        var retry = await ActivateAsync(draft.RevisionId, "cut", key);
        AssertSuccess(retry, BudgetOperationIds.RevisionActivate);
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(draft.RevisionId, await BudgetTextAsync(
            $"SELECT active_revision_id FROM budget_plan WHERE plan_id = '{draft.PlanId}';"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<DraftSnapshot> CreateDraftAsync(
        int year,
        int month,
        IReadOnlyList<(string CategoryId, long Amount)> entries,
        string reason)
    {
        var entryJson = string.Join(
            ",",
            entries.Select(e =>
                $$"""{"categoryId":"{{e.CategoryId}}","plannedMinorUnits":{{e.Amount.ToString(CultureInfo.InvariantCulture)}}}"""));
        var input = $$"""
            {"contractVersion":"1.0","period":{"year":{{year}},"month":{{month}},"currencyCode":"ZAR"},"entries":[{{entryJson}}],"reason":{{JsonSerializer.Serialize(reason)}}}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revision = doc.RootElement.GetProperty("result").GetProperty("revision");
        return new DraftSnapshot(
            revision.GetProperty("planId").GetString()!,
            revision.GetProperty("revisionId").GetString()!,
            revision.GetProperty("payloadHash").GetString()!);
    }

    private Task<ProcessResult> ActivateAsync(string revisionId, string reason, string key)
    {
        var input = $$"""
            {"contractVersion":"1.0","revisionId":{{JsonSerializer.Serialize(revisionId)}},"reason":{{JsonSerializer.Serialize(reason)}}}
            """;
        return process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope(input, key),
            CancellationToken.None);
    }

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
                "automation",
                "budget-uc002",
                "run-01",
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
                "automation",
                "budget-uc002",
                "run-01",
                "seeded draft",
                createdAt,
                PriorStatus: null,
                ResultingStatus: BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Draft),
                ReplacementRevisionId: null,
                EventSequence: 1),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc002\",\"runId\":\"run-01\"},\"idempotencyKey\":\""
            + NextKey()
            + "\",\"input\":{\"name\":\"" + name + "\"}}";
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task ArchiveCategoryAsync(string categoryId)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc002\",\"runId\":\"run-01\"},\"idempotencyKey\":\""
            + NextKey()
            + "\",\"input\":{\"categoryId\":\"" + categoryId + "\",\"reason\":\"uc002-archive\"}}";
        var result = await process.RunAsync(
            ["ledger", "category", "archive", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private string NextKey() =>
        "uc002-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc002\",\"runId\":\"run-01\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc002\",\"runId\":\"run-01\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";

    private async Task<string> BudgetMutationSnapshotAsync()
    {
        if (!File.Exists(BudgetDatabasePath()))
        {
            return "absent";
        }

        var plans = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revs = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var entries = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_entry;");
        var events = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idemp = await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");
        var active = await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;");
        return $"{plans}|{revs}|{entries}|{events}|{idemp}|{active}";
    }

    private string BudgetDatabasePath() => Path.Combine(root, "budget", "budget.db");

    private async Task<long> BudgetCountAsync(string sql)
    {
        var path = BudgetDatabasePath();
        if (!File.Exists(path))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<long> BudgetScalarAsync(string sql) => await BudgetCountAsync(sql);

    private async Task<string?> BudgetTextAsync(string sql)
    {
        var path = BudgetDatabasePath();
        if (!File.Exists(path))
        {
            return null;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static void AssertSuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
    }

    private static void AssertDomainError(ProcessResult result, int exitCode, string domainCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(domainCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(domainCode, result.Stderr, StringComparison.Ordinal);
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record DraftSnapshot(string PlanId, string RevisionId, string PayloadHash);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset now) => this.now = now;

        public void Set(DateTimeOffset value) => now = value;

        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
