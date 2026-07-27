using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// UC-BUDGET-004 / TASK-BUDGET-VERIFY-UC-004 / TC-BUDGET-PLAN-HISTORY-CONTRACT /
/// TC-BUDGET-ATTRIBUTABLE-HISTORY / TC-BUDGET-CATEGORY-LIFECYCLE-CONTRACT
/// Published-surface E2E for plan and revision history inspection: order, lifecycle,
/// state distinction, rename, archive, closed, missing, integrity, attribution, no-mutation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetUc004HistoryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-uc004-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-uc004", "run-uc004");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private BudgetStateStore store = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);

        // Mid-July 2026: July is Current; August is Future; June is Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        store = budget.State.Store;
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

    // ── Order ────────────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / complete ordered history
    [Fact]
    public async Task List_returns_every_draft_active_and_superseded_in_created_at_revision_id_order()
    {
        var cat = await CreateCategoryAsync("OrderCat");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(2026, 7, [Entry(cat, 1)], "r1");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 1, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(2026, 7, [Entry(cat, 2)], "r2");
        await ActivateAsync(r1.RevisionId, "activate-r1");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 2, TimeSpan.Zero));
        var r3 = await CreateDraftAsync(2026, 7, [Entry(cat, 3)], "r3");
        await ActivateAsync(r3.RevisionId, "activate-r3");

        var list = await ListSuccessAsync(2026, 7);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(
            new[] { r1.RevisionId, r2.RevisionId, r3.RevisionId },
            list.Items.Select(i => i.RevisionId));

        var again = await ListSuccessAsync(2026, 7);
        Assert.Equal(list.Items.Select(i => i.RevisionId), again.Items.Select(i => i.RevisionId));

        Assert.Equal(BudgetRevisionStatus.Superseded, list.Items[0].Status);
        Assert.Equal(BudgetRevisionStatus.Draft, list.Items[1].Status);
        Assert.Equal(BudgetRevisionStatus.Active, list.Items[2].Status);
        Assert.All(list.Items, i => Assert.Equal(BudgetPeriodState.Current, i.Period.State));
        Assert.All(list.Items, i => Assert.Equal(r1.PlanId, i.PlanId));
        Assert.Equal(1, list.Items[0].PlannedTotalMinorUnits);
        Assert.Equal(2, list.Items[1].PlannedTotalMinorUnits);
        Assert.Equal(3, list.Items[2].PlannedTotalMinorUnits);
        Assert.Null(list.NextCursor);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / activation and supersession provenance
    [Fact]
    public async Task Get_returns_activation_and_supersession_lifecycle_provenance()
    {
        var cat = await CreateCategoryAsync("LifecycleCat");
        var first = await CreateDraftAsync(2026, 7, [Entry(cat, 11)], "first");
        await ActivateAsync(first.RevisionId, "go-live-first");
        var second = await CreateDraftAsync(2026, 7, [Entry(cat, 22)], "second");
        await ActivateAsync(second.RevisionId, "go-live-second");

        var superseded = await GetSuccessAsync(first.RevisionId);
        var active = await GetSuccessAsync(second.RevisionId);

        Assert.Equal(BudgetRevisionStatus.Superseded, superseded.Status);
        Assert.NotNull(superseded.ActivatedAt);
        Assert.NotNull(superseded.SupersededAt);
        Assert.Equal(second.RevisionId, superseded.SupersededByRevisionId);
        Assert.Equal(11, superseded.PlannedTotalMinorUnits);
        Assert.Equal(first.PayloadHash, superseded.PayloadHash);

        Assert.Equal(BudgetRevisionStatus.Active, active.Status);
        Assert.NotNull(active.ActivatedAt);
        Assert.Null(active.SupersededAt);
        Assert.Null(active.SupersededByRevisionId);
        Assert.Equal(22, active.PlannedTotalMinorUnits);
        Assert.Equal(second.PayloadHash, active.PayloadHash);
    }

    // ── State distinction ────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-IDENTITY / NoBudgetPlan
    [Fact]
    public async Task List_no_budget_plan_is_empty_success_not_not_found()
    {
        var list = await ListSuccessAsync(2026, 7);
        Assert.Empty(list.Items);
        Assert.Null(list.NextCursor);
    }

    // FR-BUDGET-PLAN-HISTORY / no-active distinction
    [Fact]
    public async Task List_no_active_returns_drafts_without_collapsing_absent_empty_or_active()
    {
        var cat = await CreateCategoryAsync("DraftOnly");
        var a = await CreateDraftAsync(2026, 7, [Entry(cat, 1)], "d1");
        var b = await CreateDraftAsync(2026, 7, [], "d2-empty");

        var list = await ListSuccessAsync(2026, 7);
        Assert.Equal(2, list.Items.Count);
        Assert.All(list.Items, i => Assert.Equal(BudgetRevisionStatus.Draft, i.Status));
        Assert.DoesNotContain(list.Items, i => i.Status == BudgetRevisionStatus.Active);
        Assert.Equal(1, list.Items.Single(i => i.RevisionId == a.RevisionId).EntryCount);
        Assert.Equal(0, list.Items.Single(i => i.RevisionId == b.RevisionId).EntryCount);
        Assert.Equal(0, list.Items.Single(i => i.RevisionId == b.RevisionId).PlannedTotalMinorUnits);
    }

    // FR-BUDGET-PLAN-IDENTITY / Current / Future / Closed
    [Fact]
    public async Task List_reports_current_future_and_closed_period_states()
    {
        var cat = await CreateCategoryAsync("PeriodStates");
        clock.Set(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        await CreateDraftAsync(2026, 6, [Entry(cat, 1)], "june");
        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await CreateDraftAsync(2026, 7, [Entry(cat, 2)], "july");
        await CreateDraftAsync(2026, 8, [Entry(cat, 3)], "aug");

        var closed = await ListSuccessAsync(2026, 6);
        var current = await ListSuccessAsync(2026, 7);
        var future = await ListSuccessAsync(2026, 8);

        Assert.Equal(BudgetPeriodState.Closed, Assert.Single(closed.Items).Period.State);
        Assert.Equal(BudgetPeriodState.Current, Assert.Single(current.Items).Period.State);
        Assert.Equal(BudgetPeriodState.Future, Assert.Single(future.Items).Period.State);
    }

    // ── Get detail ───────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / exact immutable detail
    [Fact]
    public async Task Get_returns_exact_entries_total_stable_ids_status_and_payload()
    {
        var groceries = await CreateCategoryAsync("Groceries");
        var travel = await CreateCategoryAsync("Travel");
        var created = await CreateDraftAsync(
            2026,
            7,
            [Entry(groceries, 12_500), Entry(travel, 3_000)],
            "july plan");

        var revision = await GetSuccessAsync(created.RevisionId);

        Assert.Equal(created.PlanId, revision.PlanId);
        Assert.Equal(created.RevisionId, revision.RevisionId);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(BudgetRevisionStatus.Draft, revision.Status);
        Assert.Equal(15_500, revision.PlannedTotalMinorUnits);
        Assert.Equal(2, revision.Entries.Count);
        Assert.Equal(created.PayloadHash, revision.PayloadHash);
        Assert.Equal(created.CategoryContractVersion, revision.CategoryContractVersion);
        Assert.Null(revision.ActivatedAt);
        Assert.Null(revision.SupersededAt);
        Assert.Null(revision.SupersededByRevisionId);
        Assert.Equal(BudgetPeriodState.Current, revision.Period.State);
        Assert.Equal("2026-07-01", revision.Period.StartInclusive);
        Assert.Equal("2026-08-01", revision.Period.EndExclusive);
        Assert.Equal("ZAR", revision.Period.CurrencyCode);
        Assert.Equal(
            new[] { groceries, travel }.OrderBy(id => id, StringComparer.Ordinal),
            revision.Entries.Select(e => e.CategoryId));
        Assert.Equal(15_500, revision.Entries.Sum(e => e.PlannedMinorUnits));
    }

    // ── Rename / archive (supplemental category lifecycle) ───────────────────

    // FR-BUDGET-CATEGORY-LIFECYCLE / rename is supplemental
    [Fact]
    public async Task Get_after_rename_keeps_immutable_id_amount_hash_and_exposes_current_name()
    {
        var cat = await CreateCategoryAsync("OriginalName");
        var created = await CreateDraftAsync(2026, 7, [Entry(cat, 500)], "named");
        var originalHash = created.PayloadHash;

        await RenameCategoryAsync(cat, "RenamedName");
        var revision = await GetSuccessAsync(created.RevisionId);

        var entry = Assert.Single(revision.Entries);
        Assert.Equal(cat, entry.CategoryId);
        Assert.Equal(500, entry.PlannedMinorUnits);
        Assert.Equal("RenamedName", entry.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, entry.CurrentLifecycle);
        Assert.Equal(originalHash, revision.PayloadHash);
        Assert.Equal(500, revision.PlannedTotalMinorUnits);

        var evidence = Assert.Single(revision.CategoryLifecycle);
        Assert.Equal(cat, evidence.CategoryId);
        Assert.Equal("RenamedName", evidence.CurrentDisplayName);
        Assert.Equal(CategoryLifecycleStatus.Active, evidence.Lifecycle);

        // Durable stored intent is unchanged (hash + amount authority).
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var row = await store.GetRevisionAsync(connection, null, created.RevisionId, CancellationToken.None);
        var rows = await store.GetEntriesAsync(connection, null, created.RevisionId, CancellationToken.None);
        Assert.Equal(originalHash, row!.PayloadHash);
        Assert.Equal(cat, rows.Single().CategoryId);
        Assert.Equal(500, rows.Single().PlannedMinorUnits);
    }

    // FR-BUDGET-CATEGORY-LIFECYCLE / archive is supplemental inactive evidence
    [Fact]
    public async Task Get_after_archive_keeps_entry_readable_with_archived_lifecycle()
    {
        var cat = await CreateCategoryAsync("ArchiveLater");
        var created = await CreateDraftAsync(2026, 7, [Entry(cat, 77)], "will archive");
        var originalHash = created.PayloadHash;

        await ArchiveCategoryAsync(cat);
        var revision = await GetSuccessAsync(created.RevisionId);

        var entry = Assert.Single(revision.Entries);
        Assert.Equal(cat, entry.CategoryId);
        Assert.Equal(77, entry.PlannedMinorUnits);
        Assert.Equal(CategoryLifecycleStatus.Archived, entry.CurrentLifecycle);
        Assert.Equal("ArchiveLater", entry.CurrentDisplayName);
        Assert.Equal(originalHash, revision.PayloadHash);
        Assert.Equal(CategoryLifecycleStatus.Archived, revision.CategoryLifecycle.Single().Lifecycle);
    }

    // Review gate: payload hash stable across rename + archive on multi-revision history
    [Fact]
    public async Task Rename_and_archive_leave_payload_hashes_stable_across_history()
    {
        var a = await CreateCategoryAsync("StableA");
        var b = await CreateCategoryAsync("StableB");
        clock.Set(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(2026, 7, [Entry(a, 100), Entry(b, 0)], "mixed-1");
        await ActivateAsync(r1.RevisionId, "activate-mixed");
        clock.Set(new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(2026, 7, [], "mixed-empty");

        var hash1 = r1.PayloadHash;
        var hash2 = r2.PayloadHash;

        await RenameCategoryAsync(a, "StableA-Renamed");
        await ArchiveCategoryAsync(b);

        var detail1 = await GetSuccessAsync(r1.RevisionId);
        var detail2 = await GetSuccessAsync(r2.RevisionId);
        var list = await ListSuccessAsync(2026, 7);

        Assert.Equal(hash1, detail1.PayloadHash);
        Assert.Equal(hash2, detail2.PayloadHash);
        Assert.Equal(100, detail1.PlannedTotalMinorUnits);
        Assert.Equal("StableA-Renamed", detail1.Entries.Single(e => e.CategoryId == a).CurrentDisplayName);
        Assert.Equal(
            CategoryLifecycleStatus.Archived,
            detail1.Entries.Single(e => e.CategoryId == b).CurrentLifecycle);
        Assert.Empty(detail2.Entries);

        foreach (var summary in list.Items)
        {
            var detail = await GetSuccessAsync(summary.RevisionId);
            Assert.Equal(summary.PlannedTotalMinorUnits, detail.PlannedTotalMinorUnits);
            Assert.Equal(summary.EntryCount, detail.Entries.Count);
            Assert.Equal(summary.Status, detail.Status);
        }
    }

    // ── Closed ───────────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / closed history remains readable
    [Fact]
    public async Task Closed_period_history_remains_listable_and_gettable()
    {
        var cat = await CreateCategoryAsync("ClosedRead");
        clock.Set(new DateTimeOffset(2026, 6, 5, 8, 0, 0, TimeSpan.Zero));
        var r1 = await CreateDraftAsync(2026, 6, [Entry(cat, 10)], "c1");
        await ActivateAsync(r1.RevisionId, "activate-june");
        clock.Set(new DateTimeOffset(2026, 6, 6, 8, 0, 0, TimeSpan.Zero));
        var r2 = await CreateDraftAsync(2026, 6, [Entry(cat, 20)], "c2");

        clock.Set(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        var list = await ListSuccessAsync(2026, 6);
        Assert.Equal(2, list.Items.Count);
        Assert.All(list.Items, i => Assert.Equal(BudgetPeriodState.Closed, i.Period.State));
        Assert.Equal(BudgetRevisionStatus.Active, list.Items[0].Status);
        Assert.Equal(BudgetRevisionStatus.Draft, list.Items[1].Status);

        var detail = await GetSuccessAsync(r1.RevisionId);
        Assert.Equal(BudgetPeriodState.Closed, detail.Period.State);
        Assert.Equal("2026-06-01", detail.Period.StartInclusive);
        Assert.Equal("2026-07-01", detail.Period.EndExclusive);
        Assert.Equal(10, detail.PlannedTotalMinorUnits);
        Assert.Equal(BudgetRevisionStatus.Active, detail.Status);
        Assert.Equal(r2.RevisionId, (await GetSuccessAsync(r2.RevisionId)).RevisionId);
    }

    // ── Missing ──────────────────────────────────────────────────────────────

    // UC-BUDGET-004 failure path: unknown revision
    [Fact]
    public async Task Get_unknown_revision_is_not_found_without_leaking_other_data()
    {
        var cat = await CreateCategoryAsync("KeepMe");
        var existing = await CreateDraftAsync(2026, 7, [Entry(cat, 42_424)], "seed-secret");
        var plansBefore = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revisionsBefore = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var eventsBefore = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idemBefore = await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");

        var unknown = LedgerId.New().ToString();
        var result = await GetRawAsync(unknown);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(BudgetErrors.RevisionNotFound, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(existing.RevisionId, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(existing.PlanId, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("42424", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("seed-secret", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("plannedMinorUnits", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadHash", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(plansBefore, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revisionsBefore, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(eventsBefore, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idemBefore, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    // UC-BUDGET-004 failure path: invalid period before plan read
    [Fact]
    public async Task List_invalid_period_is_validation_error_before_any_plan_read()
    {
        var omitted = await ListRawAsync(periodJson: "null");
        var usd = await ListRawAsync(periodJson: """{"year":2026,"month":7,"currencyCode":"USD"}""");
        var badMonth = await ListRawAsync(periodJson: """{"year":2026,"month":13,"currencyCode":"ZAR"}""");

        Assert.Equal(3, omitted.ExitCode);
        Assert.Contains(BudgetErrors.InvalidPeriod, omitted.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, usd.ExitCode);
        Assert.Contains(BudgetErrors.InvalidPeriod, usd.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, badMonth.ExitCode);
        Assert.Contains(BudgetErrors.InvalidPeriod, badMonth.Stdout, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
    }

    // ── Integrity ────────────────────────────────────────────────────────────

    // UC-BUDGET-004 failure path: inconsistent evidence fails closed
    [Fact]
    public async Task Get_inconsistent_overflow_evidence_is_integrity_without_financial_payload()
    {
        var catA = await CreateCategoryAsync("OverflowA");
        var catB = await CreateCategoryAsync("OverflowB");
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedDraftWithEntriesAsync(
            planId,
            revisionId,
            [
                new BudgetPlanEntryRow(revisionId, catA, long.MaxValue),
                new BudgetPlanEntryRow(revisionId, catB, 1)
            ]);

        var result = await GetRawAsync(revisionId);

        Assert.Equal(8, result.ExitCode);
        Assert.Contains(BudgetErrors.Integrity, result.Stdout, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.False(document.RootElement.TryGetProperty("result", out var resultEl) && resultEl.ValueKind is not JsonValueKind.Null);
        Assert.DoesNotContain("plannedMinorUnits", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("entries", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(long.MaxValue.ToString(CultureInfo.InvariantCulture), result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(planId, result.Stdout, StringComparison.Ordinal);
    }

    // ── Attribution ──────────────────────────────────────────────────────────

    // NFR-BUDGET-ATTRIBUTABLE-HISTORY / actor, reason, times
    [Fact]
    public async Task Get_preserves_actor_reason_timestamps_and_activation_attribution()
    {
        var cat = await CreateCategoryAsync("AttribCat");
        clock.Set(new DateTimeOffset(2026, 7, 15, 9, 30, 0, TimeSpan.Zero));
        var created = await CreateDraftAsync(2026, 7, [Entry(cat, 9)], "owner-reason");
        clock.Set(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero));
        await ActivateAsync(created.RevisionId, "activate-reason");

        var revision = await GetSuccessAsync(created.RevisionId);

        Assert.Equal(actor.Kind, revision.ActorKind);
        Assert.Equal(actor.Label, revision.ActorLabel);
        Assert.Equal(actor.RunId, revision.ActorRunId);
        Assert.Equal("owner-reason", revision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(revision.CreatedAt));
        Assert.False(string.IsNullOrWhiteSpace(revision.ActivatedAt));
        Assert.Equal(created.CreatedAt, revision.CreatedAt);
        Assert.Equal(BudgetRevisionStatus.Active, revision.Status);
    }

    // ── No mutation ──────────────────────────────────────────────────────────

    // FR-BUDGET-PLAN-HISTORY / reads never mutate
    [Fact]
    public async Task List_and_get_do_not_mutate_plan_state_or_write_idempotency()
    {
        var cat = await CreateCategoryAsync("ImmutableRead");
        var created = await CreateDraftAsync(2026, 7, [Entry(cat, 5)], "immutable");
        var plans = await CountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revisions = await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var events = await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idem = await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");
        var entries = await CountAsync("SELECT COUNT(*) FROM budget_plan_entry;");
        var activeBefore = await GetActiveRevisionIdAsync(created.PlanId);
        var hashBefore = created.PayloadHash;

        var list = await ListSuccessAsync(2026, 7);
        var detail = await GetSuccessAsync(created.RevisionId);

        Assert.Single(list.Items);
        Assert.Equal(hashBefore, detail.PayloadHash);
        Assert.Equal(BudgetRevisionStatus.Draft, detail.Status);
        Assert.Equal(plans, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(revisions, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(events, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(idem, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(entries, await CountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(activeBefore, await GetActiveRevisionIdAsync(created.PlanId));
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
                $"{{\"categoryId\":\"{e.CategoryId}\",\"plannedMinorUnits\":{e.Amount.ToString(CultureInfo.InvariantCulture)}}}"));
        var input =
            $"{{\"contractVersion\":\"1.0\",\"period\":{{\"year\":{year},\"month\":{month},\"currencyCode\":\"ZAR\"}},\"entries\":[{entryJson}],\"reason\":{JsonSerializer.Serialize(reason)}}}";
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var typed = DeserializeResult(result.Stdout, BudgetJsonContext.Default.CreateDraftBudgetPlanResult);
        var revision = typed.Revision;
        return new DraftSnapshot(
            revision.PlanId,
            revision.RevisionId,
            revision.PayloadHash,
            revision.CategoryContractVersion,
            revision.CreatedAt);
    }

    private async Task ActivateAsync(string revisionId, string reason)
    {
        var input =
            $"{{\"contractVersion\":\"1.0\",\"revisionId\":\"{revisionId}\",\"reason\":{JsonSerializer.Serialize(reason)}}}";
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        AssertSuccess(result, BudgetOperationIds.RevisionActivate);
    }

    private async Task<BudgetPlanRevisionDetail> GetSuccessAsync(string revisionId)
    {
        var result = await GetRawAsync(revisionId);
        Assert.Equal(0, result.ExitCode);
        AssertSuccess(result, BudgetOperationIds.RevisionGet);
        return DeserializeResult(result.Stdout, BudgetJsonContext.Default.BudgetPlanRevisionDetail);
    }

    private Task<ProcessResult> GetRawAsync(string revisionId)
    {
        var input = $"{{\"contractVersion\":\"1.0\",\"revisionId\":\"{revisionId}\"}}";
        return process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
    }

    private async Task<ListBudgetPlanRevisionsResult> ListSuccessAsync(int year, int month)
    {
        var result = await ListRawAsync(
            periodJson: $"{{\"year\":{year},\"month\":{month},\"currencyCode\":\"ZAR\"}}");
        Assert.Equal(0, result.ExitCode);
        AssertSuccess(result, BudgetOperationIds.RevisionList);
        return DeserializeResult(result.Stdout, BudgetJsonContext.Default.ListBudgetPlanRevisionsResult);
    }

    private Task<ProcessResult> ListRawAsync(string periodJson)
    {
        var input = $"{{\"contractVersion\":\"1.0\",\"period\":{periodJson}}}";
        return process.RunAsync(
            ["budget", "plan", "revision", "list", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
    }

    private async Task SeedDraftWithEntriesAsync(
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
            new BudgetPlanRow(planId, "2026-07-01", "2026-08-01", "ZAR", ActiveRevisionId: null, createdAt),
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
                "seeded overflow draft",
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
                "seeded overflow draft",
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
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc004\",\"runId\":\"run-uc004\"},\"idempotencyKey\":\""
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

    private async Task RenameCategoryAsync(string categoryId, string newName)
    {
        var input = new RenameCategoryInput(categoryId, newName, "uc004-rename");
        var request = SerializeLedgerRequest(input, LedgerJsonContext.Default.RenameCategoryInput, NextKey());
        var result = await process.RunAsync(
            ["ledger", "category", "rename", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task ArchiveCategoryAsync(string categoryId)
    {
        var input = new ArchiveCategoryInput(categoryId, "uc004-archive");
        var request = SerializeLedgerRequest(input, LedgerJsonContext.Default.ArchiveCategoryInput, NextKey());
        var result = await process.RunAsync(
            ["ledger", "category", "archive", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private string SerializeLedgerRequest<TInput>(
        TInput input,
        JsonTypeInfo<TInput> inputType,
        string idempotencyKey)
    {
        var inputElement = JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope("1.0", actor, inputElement, idempotencyKey);
        return JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
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

    private static (string CategoryId, long Amount) Entry(string categoryId, long amount) =>
        (categoryId, amount);

    private string NextKey() =>
        "uc004-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture);

    private string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc004\",\"runId\":\"run-uc004\"},\"input\":"
              + inputJson
              + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc004\",\"runId\":\"run-uc004\"},\"idempotencyKey\":\""
              + idempotencyKey
              + "\",\"input\":"
              + inputJson
              + "}";

    private static void AssertSuccess(ProcessResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
    }

    private static T DeserializeResult<T>(string stdout, JsonTypeInfo<T> typeInfo)
    {
        using var document = JsonDocument.Parse(stdout);
        var result = document.RootElement.GetProperty("result");
        return JsonSerializer.Deserialize(result.GetRawText(), typeInfo)
            ?? throw new InvalidOperationException("Missing typed result payload.");
    }

    private sealed record DraftSnapshot(
        string PlanId,
        string RevisionId,
        string PayloadHash,
        string CategoryContractVersion,
        string CreatedAt);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset now) => this.now = now;

        public void Set(DateTimeOffset value) => now = value;

        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
