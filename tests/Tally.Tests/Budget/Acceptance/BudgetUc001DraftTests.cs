using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Common;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// UC-BUDGET-001 published-surface acceptance gate (VerifiedBudgetUc001).
/// Invokes only TallyProcess + OperationRegistry — never private command handlers.
/// Proves current/future draft creation, empty/zero/omission distinctions, validation,
/// category lifecycle rejections, replay/conflict, attribution, atomicity, and no Ledger
/// mutation on failure (DD-BUDGET-IDEMPOTENT-MUTATIONS, DD-BUDGET-LEDGER-PUBLIC-COMPOSITION,
/// DD-BUDGET-PLAN-REVISION-LIFECYCLE).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetUc001DraftTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-uc001-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
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
        services = services with { Budget = budget.Operations };
        process = new TallyProcess(registry, services);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success: current / future ────────────────────────────────────────────

    [Fact]
    public async Task Current_period_published_draft_creates_one_attributed_immutable_draft()
    {
        var groceries = await CreateCategoryAsync("Uc001Groceries");
        var travel = await CreateCategoryAsync("Uc001Travel");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((groceries, 12_500), (travel, 3_000)),
            reason: "july-plan",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revision = doc.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal("draft", revision.GetProperty("status").GetString());
        Assert.Equal(1, revision.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(15_500, revision.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(2, revision.GetProperty("entries").GetArrayLength());
        Assert.Equal("july-plan", revision.GetProperty("reason").GetString());
        Assert.Equal("automation", revision.GetProperty("actorKind").GetString());
        Assert.Equal("budget-uc001", revision.GetProperty("actorLabel").GetString());
        Assert.Equal("run-01", revision.GetProperty("actorRunId").GetString());
        Assert.Equal("current", revision.GetProperty("period").GetProperty("state").GetString());
        Assert.Equal("2026-07-01", revision.GetProperty("period").GetProperty("startInclusive").GetString());
        Assert.Equal("2026-08-01", revision.GetProperty("period").GetProperty("endExclusive").GetString());
        Assert.Equal(JsonValueKind.Null, revision.GetProperty("activatedAt").ValueKind);

        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(2L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'DraftCreated';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;"));
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Future_period_published_draft_succeeds_without_activation()
    {
        var cat = await CreateCategoryAsync("Uc001Future");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 8),
            EntriesJson((cat, 100)),
            reason: "future-plan",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revision = doc.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal("future", revision.GetProperty("period").GetProperty("state").GetString());
        Assert.Equal("draft", revision.GetProperty("status").GetString());
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    // ── Empty / all-zero / omission distinctions ─────────────────────────────

    [Fact]
    public async Task Empty_entries_create_distinct_empty_draft_with_zero_total()
    {
        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            "[]",
            reason: "empty-draft",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revision = doc.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal(0, revision.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, revision.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(0, revision.GetProperty("categoryLifecycle").GetArrayLength());
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    [Fact]
    public async Task All_zero_draft_preserves_explicit_zero_rows_and_zero_total()
    {
        var a = await CreateCategoryAsync("Uc001ZeroA");
        var b = await CreateCategoryAsync("Uc001ZeroB");

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((a, 0), (b, 0)),
            reason: "all-zero",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revision = doc.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal(2, revision.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, revision.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.All(
            revision.GetProperty("entries").EnumerateArray(),
            e => Assert.Equal(0, e.GetProperty("plannedMinorUnits").GetInt64()));
        Assert.Equal(2L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_entry WHERE planned_minor_units = 0;"));
    }

    [Fact]
    public async Task Explicit_zero_is_stored_while_omitted_category_has_no_row()
    {
        var budgeted = await CreateCategoryAsync("Uc001Budgeted");
        var zeroed = await CreateCategoryAsync("Uc001ZeroBudget");
        var omitted = await CreateCategoryAsync("Uc001Unbudgeted");

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((budgeted, 500), (zeroed, 0)),
            reason: "omit-one",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var entries = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("entries").EnumerateArray().ToArray();
        var ids = entries.Select(e => e.GetProperty("categoryId").GetString()).ToArray();
        Assert.Contains(budgeted, ids);
        Assert.Contains(zeroed, ids);
        Assert.DoesNotContain(omitted, ids);
        Assert.Equal(0, entries.Single(e => e.GetProperty("categoryId").GetString() == zeroed)
            .GetProperty("plannedMinorUnits").GetInt64());
        Assert.Equal(500, doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(0L, await BudgetCountAsync(
            $"SELECT COUNT(*) FROM budget_plan_entry WHERE category_id = '{omitted}';"));
    }

    [Fact]
    public async Task Empty_and_all_zero_drafts_remain_distinct_after_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001DistinctZero");
        var empty = await DraftCreateAsync(PeriodJson(2026, 7), "[]", "empty", NextKey());
        var zero = await DraftCreateAsync(
            PeriodJson(2026, 8),
            EntriesJson((cat, 0)),
            "zero",
            NextKey());

        AssertSuccess(empty, BudgetOperationIds.DraftCreate);
        AssertSuccess(zero, BudgetOperationIds.DraftCreate);
        using var emptyDoc = JsonDocument.Parse(empty.Stdout);
        using var zeroDoc = JsonDocument.Parse(zero.Stdout);
        var emptyHash = emptyDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("payloadHash").GetString();
        var zeroHash = zeroDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("payloadHash").GetString();
        Assert.NotEqual(emptyHash, zeroHash);
        Assert.Equal(0, emptyDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("entries").GetArrayLength());
        Assert.Equal(1, zeroDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Exact_minor_unit_totals_reconcile_on_published_result_and_store()
    {
        var a = await CreateCategoryAsync("Uc001ExactA");
        var b = await CreateCategoryAsync("Uc001ExactB");

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((a, 1), (b, 99_999_999_999L)),
            reason: "exact",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal(100_000_000_000L, doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(100_000_000_000L, await BudgetScalarAsync(
            "SELECT SUM(planned_minor_units) FROM budget_plan_entry;"));
    }

    // ── Active pointer / plan identity ───────────────────────────────────────

    [Fact]
    public async Task Successful_draft_does_not_set_active_revision_or_activate()
    {
        var cat = await CreateCategoryAsync("Uc001NoActivate");
        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 1)),
            reason: "no-activate",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'RevisionActivated';"));
    }

    [Fact]
    public async Task Second_draft_for_same_period_reuses_plan_and_sequences_revision()
    {
        var cat = await CreateCategoryAsync("Uc001Seq");
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 10)), "r1", NextKey());
        var second = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 20)), "r2", NextKey());

        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertSuccess(second, BudgetOperationIds.DraftCreate);
        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        var plan1 = d1.RootElement.GetProperty("result").GetProperty("revision").GetProperty("planId").GetString();
        var plan2 = d2.RootElement.GetProperty("result").GetProperty("revision").GetProperty("planId").GetString();
        Assert.Equal(plan1, plan2);
        Assert.Equal(1, d1.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionNumber").GetInt32());
        Assert.Equal(2, d2.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(2L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // ── Validation failures (no persistence) ─────────────────────────────────

    [Fact]
    public async Task Closed_period_is_rejected_without_budget_or_ledger_mutation()
    {
        var cat = await CreateCategoryAsync("Uc001Closed");
        var ledgerBefore = await LedgerFingerprintAsync();
        var budgetBefore = await BudgetMutationSnapshotAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 6),
            EntriesJson((cat, 10)),
            reason: "closed",
            key: NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidPeriod);
        Assert.Equal(budgetBefore, await BudgetMutationSnapshotAsync());
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Invalid_currency_is_rejected_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001Usd");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            """{"year":2026,"month":7,"currencyCode":"USD"}""",
            EntriesJson((cat, 1)),
            reason: "usd",
            key: NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidPeriod);
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Negative_amount_is_rejected_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001Neg");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, -1)),
            reason: "neg",
            key: NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidAmount);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Overflowing_checked_total_is_rejected_without_persistence()
    {
        var a = await CreateCategoryAsync("Uc001OverA");
        var b = await CreateCategoryAsync("Uc001OverB");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((a, long.MaxValue), (b, 1)),
            reason: "overflow",
            key: NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidAmount);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Duplicate_category_ids_are_rejected_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001Dup");
        var ledgerBefore = await LedgerFingerprintAsync();

        var entries = $$"""[{"categoryId":"{{cat}}","plannedMinorUnits":1},{"categoryId":"{{cat}}","plannedMinorUnits":2}]""";
        var result = await DraftCreateAsync(PeriodJson(2026, 7), entries, "dup", NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidInput);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Display_name_only_category_reference_is_rejected_without_persistence()
    {
        var ledgerBefore = await LedgerFingerprintAsync();
        var entries = """[{"categoryId":"Groceries","plannedMinorUnits":100}]""";
        var result = await DraftCreateAsync(PeriodJson(2026, 7), entries, "by-name", NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidInput);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Missing_actor_is_rejected_before_effects()
    {
        var body = """
            {"contractVersion":"1.0","input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"no-actor"},"idempotencyKey":"k-no-actor"}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input");
        await AssertNoBudgetMutationAsync();
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected_before_effects()
    {
        var body = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-uc001","runId":"run-01"},"input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"no-key"}}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input");
        await AssertNoBudgetMutationAsync();
    }

    [Fact]
    public async Task Blank_reason_is_rejected_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001NoReason");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 1)),
            reason: "   ",
            key: NextKey());

        AssertDomainError(result, 3, BudgetErrors.InvalidInput);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Unsupported_contract_version_is_compatibility_failure_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001BadVer");
        var ledgerBefore = await LedgerFingerprintAsync();
        var input = $$"""
            {"contractVersion":"9.9","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{cat}}","plannedMinorUnits":1}],"reason":"bad-version"}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);

        AssertDomainError(result, 7, BudgetErrors.UnsupportedVersion);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    // ── Category lifecycle rejections ────────────────────────────────────────

    [Fact]
    public async Task Unknown_category_is_rejected_without_persistence()
    {
        // Valid ULID shape that is not present in the catalogue.
        const string unknown = "01JZZZZZZZZZZZZZZZZZZZZZZZ";
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((unknown, 10)),
            reason: "unknown",
            key: NextKey());

        AssertDomainError(result, 4, BudgetErrors.CategoryUnknown);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Archived_category_is_rejected_without_persistence()
    {
        var cat = await CreateCategoryAsync("Uc001ArchiveMe");
        await ArchiveCategoryAsync(cat);
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 10)),
            reason: "archived",
            key: NextKey());

        AssertDomainError(result, 6, BudgetErrors.CategoryInactive);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Mixed_active_and_archived_categories_fail_atomically_with_no_partial_draft()
    {
        var active = await CreateCategoryAsync("Uc001MixedActive");
        var archived = await CreateCategoryAsync("Uc001MixedArchived");
        await ArchiveCategoryAsync(archived);
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((active, 1), (archived, 2)),
            reason: "mixed",
            key: NextKey());

        AssertDomainError(result, 6, BudgetErrors.CategoryInactive);
        await AssertNoBudgetMutationAsync();
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    // ── Replay / conflict ────────────────────────────────────────────────────

    [Fact]
    public async Task Equivalent_replay_returns_same_stable_plan_revision_and_event()
    {
        var cat = await CreateCategoryAsync("Uc001Replay");
        var key = NextKey();
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 42)), "replay", key);
        var second = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 42)), "replay", key);

        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertSuccess(second, BudgetOperationIds.DraftCreate);
        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        var r1 = d1.RootElement.GetProperty("result").GetProperty("revision");
        var r2 = d2.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal(r1.GetProperty("revisionId").GetString(), r2.GetProperty("revisionId").GetString());
        Assert.Equal(r1.GetProperty("planId").GetString(), r2.GetProperty("planId").GetString());
        Assert.Equal(r1.GetProperty("payloadHash").GetString(), r2.GetProperty("payloadHash").GetString());
        Assert.Equal(r1.GetProperty("revisionNumber").GetInt32(), r2.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
    }

    [Fact]
    public async Task Same_key_with_different_input_conflicts_and_creates_no_duplicate()
    {
        var cat = await CreateCategoryAsync("Uc001Conflict");
        var key = NextKey();
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 10)), "a", key);
        var conflict = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 99)), "b", key);

        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertDomainError(conflict, 5, BudgetErrors.IdempotencyConflict);
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        using var doc = JsonDocument.Parse(first.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        Assert.Equal(10L, await BudgetScalarAsync(
            $"SELECT planned_minor_units FROM budget_plan_entry WHERE revision_id = '{revisionId}';"));
    }

    [Fact]
    public async Task Entry_order_normalization_does_not_conflict_on_replay()
    {
        var a = await CreateCategoryAsync("Uc001OrderA");
        var b = await CreateCategoryAsync("Uc001OrderB");
        var key = NextKey();
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((b, 2), (a, 1)),
            "order",
            key);
        var second = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((a, 1), (b, 2)),
            "order",
            key);

        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertSuccess(second, BudgetOperationIds.DraftCreate);
        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        Assert.Equal(
            d1.RootElement.GetProperty("result").GetProperty("revision").GetProperty("revisionId").GetString(),
            d2.RootElement.GetProperty("result").GetProperty("revision").GetProperty("revisionId").GetString());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // ── Attribution ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DraftCreated_lifecycle_event_is_attributable_with_actor_reason_and_sequence()
    {
        var cat = await CreateCategoryAsync("Uc001Attr");
        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 7)),
            reason: "because-planning",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(result.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        var planId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("planId").GetString()!;

        Assert.Equal(1L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_lifecycle_event WHERE event_type = 'DraftCreated';"));
        Assert.Equal("automation", await BudgetTextAsync(
            $"SELECT actor_kind FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal("budget-uc001", await BudgetTextAsync(
            $"SELECT actor_label FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal("run-01", await BudgetTextAsync(
            $"SELECT actor_run_id FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal("because-planning", await BudgetTextAsync(
            $"SELECT reason FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal(revisionId, await BudgetTextAsync(
            $"SELECT revision_id FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal(1L, await BudgetScalarAsync(
            $"SELECT event_sequence FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT resulting_status FROM budget_lifecycle_event WHERE plan_id = '{planId}';"));
    }

    // ── Atomicity / failure isolation ────────────────────────────────────────

    [Fact]
    public async Task Validation_failure_does_not_mutate_existing_plan_history()
    {
        var cat = await CreateCategoryAsync("Uc001KeepHistory");
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 5)), "keep", NextKey());
        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        var snapshot = await BudgetMutationSnapshotAsync();
        var ledgerBefore = await LedgerFingerprintAsync();

        var failed = await DraftCreateAsync(
            PeriodJson(2026, 6), EntriesJson((cat, 5)), "closed-fail", NextKey());
        AssertDomainError(failed, 3, BudgetErrors.InvalidPeriod);
        Assert.Equal(snapshot, await BudgetMutationSnapshotAsync());
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task Successful_draft_leaves_ledger_byte_equivalent_and_no_active_pointer()
    {
        var cat = await CreateCategoryAsync("Uc001LedgerStable");
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 2500)),
            reason: "ledger-stable",
            key: NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;"));
        // Budget mutated; ledger did not.
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // ── Concurrency (published surface) ──────────────────────────────────────

    // UC-BUDGET-001 / concurrent draft mutation — distinct keys settle as two sequenced revisions
    // (no shared active-slot resource, so both mutations commit rather than one winning a conflict).
    [Fact]
    public async Task Concurrent_draft_creates_for_same_period_settle_as_two_distinct_sequenced_revisions()
    {
        var cat = await CreateCategoryAsync("Uc001Concurrent");

        var t1 = DraftCreateAsync(PeriodJson(2026, 7), EntriesJson((cat, 10)), "concurrent-a", NextKey());
        var t2 = DraftCreateAsync(PeriodJson(2026, 7), EntriesJson((cat, 20)), "concurrent-b", NextKey());
        await Task.WhenAll(t1, t2);
        var r1 = await t1;
        var r2 = await t2;

        AssertSuccess(r1, BudgetOperationIds.DraftCreate);
        AssertSuccess(r2, BudgetOperationIds.DraftCreate);
        using var d1 = JsonDocument.Parse(r1.Stdout);
        using var d2 = JsonDocument.Parse(r2.Stdout);
        var rev1 = d1.RootElement.GetProperty("result").GetProperty("revision");
        var rev2 = d2.RootElement.GetProperty("result").GetProperty("revision");

        // Same plan (shared period identity); two distinct revisions with dense sequential numbering.
        Assert.Equal(rev1.GetProperty("planId").GetString(), rev2.GetProperty("planId").GetString());
        Assert.NotEqual(rev1.GetProperty("revisionId").GetString(), rev2.GetProperty("revisionId").GetString());
        var numbers = new[]
        {
            rev1.GetProperty("revisionNumber").GetInt32(),
            rev2.GetProperty("revisionNumber").GetInt32()
        }.OrderBy(n => n).ToArray();
        Assert.Equal([1, 2], numbers);

        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(2L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ProcessResult> DraftCreateAsync(
        string periodJson,
        string entriesJson,
        string reason,
        string key)
    {
        var input = $$"""
            {"contractVersion":"1.0","period":{{periodJson}},"entries":{{entriesJson}},"reason":{{JsonSerializer.Serialize(reason)}}}
            """;
        return await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, key),
            CancellationToken.None);
    }

    private static string PeriodJson(int year, int month) =>
        $$"""{"year":{{year}},"month":{{month}},"currencyCode":"ZAR"}""";

    private static string EntriesJson(params (string CategoryId, long Amount)[] entries) =>
        "[" + string.Join(
            ",",
            entries.Select(e =>
                $$"""{"categoryId":"{{e.CategoryId}}","plannedMinorUnits":{{e.Amount.ToString(CultureInfo.InvariantCulture)}}}"""))
        + "]";

    private static string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc001\",\"runId\":\"run-01\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc001\",\"runId\":\"run-01\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";

    private async Task<string> CreateCategoryAsync(string name)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc001\",\"runId\":\"run-01\"},\"idempotencyKey\":\""
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
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc001\",\"runId\":\"run-01\"},\"idempotencyKey\":\""
            + NextKey()
            + "\",\"input\":{\"categoryId\":\"" + categoryId + "\",\"reason\":\"uc001-archive\"}}";
        var result = await process.RunAsync(
            ["ledger", "category", "archive", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private string NextKey() =>
        "uc001-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task AssertNoBudgetMutationAsync()
    {
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_entry;"));
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

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

    private async Task<string> LedgerFingerprintAsync()
    {
        var path = LedgerDatabasePath();
        if (!File.Exists(path))
        {
            return "absent";
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();

        // Logical durable fingerprint — not a live file hash (WAL sidecars make byte hashes flaky).
        var builder = new StringBuilder();
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM spend_category;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM catalogue_lifecycle_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM category_parent_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM account;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM transaction_fact;");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT category_id || '|' || COALESCE(
                    (SELECT action FROM catalogue_lifecycle_event e
                     WHERE e.catalogue_kind = 'category' AND e.entity_id = spend_category.category_id
                     ORDER BY occurred_at DESC, lifecycle_event_id DESC LIMIT 1), '')
                FROM spend_category
                ORDER BY category_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.Append(reader.GetString(0)).Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task AppendScalarAsync(SqliteConnection connection, StringBuilder builder, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)).Append('#');
    }

    private string BudgetDatabasePath() => Path.Combine(root, "budget", "budget.db");

    private string LedgerDatabasePath()
    {
        var current = File.ReadAllText(Path.Combine(root, "CURRENT")).Trim();
        return Path.Combine(root, "generations", current, "ledger.db");
    }

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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
