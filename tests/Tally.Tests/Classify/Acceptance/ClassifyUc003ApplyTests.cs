using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// UC-CLASSIFY-003 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-003 / bd-9oqw
/// VerifiedClassifyUc003 — published-boundary acceptance and recovery matrix.
///
/// CLASSIFY mutations and reads under test go through TallyProcess
/// (evaluate / apply.preview / apply.run / rule lifecycle / outcome.get).
/// Durable crash injection freezes planned apply_run rows only so resume can
/// prove the exact unresolved frontier. Ledger allocation history
/// (apply_preflight CurrentAllocationId + category_allocation_event counts)
/// is the authoritative duplicate-mutation oracle.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc003ApplyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc003-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private readonly ClassificationApplyPreviewStore previewStore = new();
    private readonly ClassificationApplyRunStore runStore = new();
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        ledger = new LedgerContractClient(registry, bootstrap);
        var classify = await ClassifyOperationBundle.CreateServicesAsync(
            root, ledger, cancellationToken: CancellationToken.None);
        services = services with { Classify = classify.Operations };
        process = new TallyProcess(registry, services);
        store = classify.State.Store;
        accountId = await CreateAccountAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Evaluation / preview are read-only ───────────────────────────────────

    [Fact]
    public async Task UC003_evaluate_alone_mutates_no_ledger_allocation()
    {
        var category = await CreateCategoryAsync("Uc003EvalOnly");
        await SaveAndActivateAsync(category, "uc003 eval only", broadApply: false);
        var tx = await RecordTransactionAsync("uc003 eval only");
        var before = await SnapshotAllocationsAsync([tx]);
        var allocCountBefore = await AllocationEventCountAsync(tx);

        var evalId = await EvaluateSuccessAsync();
        Assert.False(string.IsNullOrWhiteSpace(evalId));

        var after = await SnapshotAllocationsAsync([tx]);
        Assert.Equal(before, after);
        Assert.Equal(allocCountBefore, await AllocationEventCountAsync(tx));
        Assert.Null(after[tx]);
    }

    [Fact]
    public async Task UC003_preview_is_exact_read_only_with_selection_hash()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 preview shop");
        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var allocBefore = await AllocationEventCountAsync(seeded.TransactionIds[0]);

        using var doc = ParseResult(seeded.PreviewResult);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(seeded.EvaluationId, body.GetProperty("evaluationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("previewId").GetString()));
        Assert.True(body.GetProperty("selectedCount").GetInt32() >= 1);
        Assert.True(body.GetProperty("assignableCount").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("correctableCount").GetInt32());
        Assert.Equal(64, body.GetProperty("selectionHash").GetString()!.Length);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("expiresAt").GetString()));

        Assert.Equal(before, await SnapshotAllocationsAsync(seeded.TransactionIds));
        Assert.Equal(allocBefore, await AllocationEventCountAsync(seeded.TransactionIds[0]));
    }

    // ── Authority / exclusion / stale preflight ──────────────────────────────

    [Fact]
    public async Task UC003_exact_rule_without_broad_authority_rejected_before_ledger()
    {
        var category = await CreateCategoryAsync("Uc003Narrow");
        var ruleVersionId = await SaveAndActivateAsync(category, "uc003 narrow shop", broadApply: false);
        var tx = await RecordTransactionAsync("uc003 narrow shop");
        var evalId = await EvaluateSuccessAsync();
        var before = await SnapshotAllocationsAsync([tx]);

        var preview = await PreviewExactRuleAsync(evalId, ruleVersionId, NextKey());
        AssertClassifyError(preview, ClassifyErrors.Lifecycle);
        Assert.Equal(before, await SnapshotAllocationsAsync([tx]));
        Assert.Equal(0, await ApplyRunCountAsync());
    }

    [Fact]
    public async Task UC003_exact_rule_with_broad_authority_authorizes_assignments_only()
    {
        var category = await CreateCategoryAsync("Uc003Broad");
        var ruleVersionId = await SaveAndActivateAsync(category, "uc003 broad shop", broadApply: true);
        var tx = await RecordTransactionAsync("uc003 broad shop");
        var evalId = await EvaluateSuccessAsync();
        var before = await SnapshotAllocationsAsync([tx]);

        var preview = await PreviewExactRuleAsync(evalId, ruleVersionId, NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var doc = ParseResult(preview);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("selectedCount").GetInt32() >= 1);
        Assert.True(body.GetProperty("assignableCount").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("correctableCount").GetInt32());
        Assert.Equal(before, await SnapshotAllocationsAsync([tx]));
    }

    [Fact]
    public async Task UC003_preview_excludes_no_suggestion_and_conflict_from_scope()
    {
        var catA = await CreateCategoryAsync("Uc003MixA");
        var catB = await CreateCategoryAsync("Uc003MixB");
        var vMatch = await SaveRuleAsync(catA, "uc003 mix-match", "rule-uc003-mix-a");
        await ActivateRulesAsync([vMatch], [("uc003 mix-match", "suggestion", catA)], broadApply: false);
        // Second activation path: conflict pair for clash description.
        var vClashA = await SaveRuleAsync(catA, "uc003 mix-clash", "rule-uc003-mix-ca");
        var vClashB = await SaveRuleAsync(catB, "uc003 mix-clash", "rule-uc003-mix-cb");
        // Activate conflict set (replaces active set membership with both clash rules).
        await ActivateRulesAsync([vClashA, vClashB], [("uc003 mix-clash", "conflict", null)], broadApply: false);

        var matched = await RecordTransactionAsync("uc003 mix-match"); // may be no-suggestion after clash activation
        var clash = await RecordTransactionAsync("uc003 mix-clash");
        var unmatched = await RecordTransactionAsync("uc003 mix-other");
        var evalId = await EvaluateSuccessAsync();

        var outcomes = await ListOutcomeIdsByKindAsync(evalId, [matched, clash, unmatched]);
        Assert.True(outcomes.Count >= 1);
        var allIds = outcomes.Values.Select(v => v.OutcomeId).ToArray();
        var before = await SnapshotAllocationsAsync([matched, clash, unmatched]);

        var preview = await PreviewSelectedAsync(evalId, allIds, NextKey());
        // Either succeeds with only assignable suggestions selected, or rejects invalid selection.
        if (preview.ExitCode == 0)
        {
            using var doc = ParseResult(preview);
            var body = doc.RootElement.GetProperty("result_or_error");
            var selected = body.GetProperty("selectedCount").GetInt32();
            Assert.True(selected <= allIds.Length);
            // Conflict / no-suggestion must not become correctable selections.
            Assert.Equal(0, body.GetProperty("correctableCount").GetInt32());
            if (outcomes.Values.Any(o => o.Kind is "conflict" or "no_suggestion"))
            {
                Assert.True(
                    selected < allIds.Length
                    || body.GetProperty("assignableCount").GetInt32() == selected);
            }
        }
        else
        {
            using var doc = ParseResult(preview);
            var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
            Assert.True(
                code is ClassifyErrors.SelectionInvalid or ClassifyErrors.Stale or ClassifyErrors.Lifecycle,
                code);
        }

        Assert.Equal(before, await SnapshotAllocationsAsync([matched, clash, unmatched]));
    }

    [Fact]
    public async Task UC003_stale_whole_selection_rejects_before_ledger_mutation()
    {
        // Preview must authorize at least two current suggestions; then stale exactly one sibling
        // and prove whole-selection rejection before any apply_run or allocation mutation for all.
        var category = await CreateCategoryAsync("Uc003StaleWhole");
        await SaveAndActivateAsync(category, "uc003 stale multi", broadApply: false);
        var txStale = await RecordTransactionAsync("uc003 stale multi");
        var txCurrent = await RecordTransactionAsync("uc003 stale multi");
        var evalId = await EvaluateSuccessAsync();
        var oStale = await OutcomeGetBodyAsync(evalId, txStale);
        var oCurrent = await OutcomeGetBodyAsync(evalId, txCurrent);
        Assert.Equal("suggestion", oStale.Kind);
        Assert.Equal("suggestion", oCurrent.Kind);

        var preview = await PreviewSelectedAsync(
            evalId, [oStale.OutcomeId, oCurrent.OutcomeId], NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var previewDoc = ParseResult(preview);
        var previewBody = previewDoc.RootElement.GetProperty("result_or_error");
        Assert.True(previewBody.GetProperty("selectedCount").GetInt32() >= 2);
        var previewId = previewBody.GetProperty("previewId").GetString()!;
        var selectedTxs = previewBody.GetProperty("selectedTransactionIds").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Contains(txStale, selectedTxs);
        Assert.Contains(txCurrent, selectedTxs);
        Assert.True(selectedTxs.Length >= 2);

        // Stale exactly one selected transaction after preview; sibling remains current.
        await VoidTransactionAsync(txStale);

        var allSelected = selectedTxs.Distinct(StringComparer.Ordinal).ToArray();
        var before = await SnapshotAllocationsAsync(allSelected);
        var runsBefore = await ApplyRunCountAsync();
        var allocBeforeByTx = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var tx in allSelected)
        {
            allocBeforeByTx[tx] = await AllocationEventCountAsync(tx);
        }

        var run = await ApplyRunAsync(previewId, "apply-stale-" + Guid.NewGuid().ToString("N")[..8], NextKey());
        Assert.NotEqual(0, run.ExitCode);
        using var doc = ParseResult(run);
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.True(
            code is ClassifyErrors.Stale or ClassifyErrors.SelectionInvalid or ClassifyErrors.LedgerUnavailable
                or ClassifyErrors.Lifecycle or ClassifyErrors.Integrity,
            code);

        // Whole-selection rejection: no apply_run and no allocation mutation for EVERY selected tx,
        // including the still-current sibling that was never voided.
        Assert.Equal(runsBefore, await ApplyRunCountAsync());
        Assert.Equal(before, await SnapshotAllocationsAsync(allSelected));
        foreach (var tx in allSelected)
        {
            Assert.Equal(allocBeforeByTx[tx], await AllocationEventCountAsync(tx));
            Assert.Null(before[tx]);
            Assert.Null((await SnapshotAllocationsAsync([tx]))[tx]);
        }
    }

    // ── Assignment / correction / item partition ─────────────────────────────

    [Fact]
    public async Task UC003_assign_applies_uncategorized_suggestion_exactly_once()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 assign shop");
        var applyId = "apply-uc003-a-" + Guid.NewGuid().ToString("N")[..8];
        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var allocBefore = await AllocationEventCountAsync(seeded.TransactionIds[0]);

        var run = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var doc = ParseResult(run);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(applyId, body.GetProperty("applyId").GetString());
        Assert.Equal(seeded.PreviewId, body.GetProperty("previewId").GetString());
        Assert.True(body.GetProperty("appliedCount").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("unresolvedCount").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        Assert.Equal(
            items.Length,
            body.GetProperty("appliedCount").GetInt32()
            + body.GetProperty("alreadyAppliedCount").GetInt32()
            + body.GetProperty("rejectedCount").GetInt32()
            + body.GetProperty("failedCount").GetInt32()
            + body.GetProperty("unresolvedCount").GetInt32());
        Assert.All(items, i =>
        {
            Assert.Equal("applied", i.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("allocationEventId").GetString()));
            Assert.Equal(seeded.TransactionIds[0], i.GetProperty("transactionId").GetString());
        });

        var after = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.NotEqual(before[seeded.TransactionIds[0]], after[seeded.TransactionIds[0]]);
        Assert.False(string.IsNullOrWhiteSpace(after[seeded.TransactionIds[0]]));
        Assert.Equal(allocBefore + 1, await AllocationEventCountAsync(seeded.TransactionIds[0]));
    }

    [Fact]
    public async Task UC003_explicit_correction_requires_reason_and_mutates_once()
    {
        var catA = await CreateCategoryAsync("Uc003CorrA");
        var catB = await CreateCategoryAsync("Uc003CorrB");
        await SaveAndActivateAsync(catA, "uc003 corr shop", broadApply: false);
        var tx = await RecordTransactionAsync("uc003 corr shop");
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);

        // Seed current allocation so correction is the authorized path.
        await AssignCategoryAsync(tx, catA, "uc003 seed assign");
        var before = await SnapshotAllocationsAsync([tx]);
        var allocBefore = await AllocationEventCountAsync(tx);

        var preview = await PreviewCorrectionsAsync(
            evalId,
            tx,
            outcome.OutcomeId,
            catA,
            catB,
            "owner-explicit-reason",
            NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var previewDoc = ParseResult(preview);
        var previewBody = previewDoc.RootElement.GetProperty("result_or_error");
        Assert.True(previewBody.GetProperty("correctableCount").GetInt32() >= 1);
        Assert.Equal(before, await SnapshotAllocationsAsync([tx]));

        var applyId = "apply-corr-" + Guid.NewGuid().ToString("N")[..8];
        var run = await ApplyRunAsync(previewBody.GetProperty("previewId").GetString()!, applyId, NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var runDoc = ParseResult(run);
        var runBody = runDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(1, runBody.GetProperty("appliedCount").GetInt32());
        Assert.Equal("applied", runBody.GetProperty("items")[0].GetProperty("kind").GetString());

        var after = await SnapshotAllocationsAsync([tx]);
        Assert.NotEqual(before[tx], after[tx]);
        Assert.Equal(allocBefore + 1, await AllocationEventCountAsync(tx));
    }

    [Fact]
    public async Task UC003_broad_mode_never_includes_correction()
    {
        var catA = await CreateCategoryAsync("Uc003BroadCorrA");
        var catB = await CreateCategoryAsync("Uc003BroadCorrB");
        var ruleVersionId = await SaveAndActivateAsync(catA, "uc003 broad-corr", broadApply: true);
        var tx = await RecordTransactionAsync("uc003 broad-corr");
        var evalId = await EvaluateSuccessAsync();
        await AssignCategoryAsync(tx, catA, "seed categorized");

        // Exact-rule broad preview must remain assignable-only (no correctable rows).
        var preview = await PreviewExactRuleAsync(evalId, ruleVersionId, NextKey());
        if (preview.ExitCode == 0)
        {
            using var doc = ParseResult(preview);
            var body = doc.RootElement.GetProperty("result_or_error");
            Assert.Equal(0, body.GetProperty("correctableCount").GetInt32());
        }
        else
        {
            // Already-categorized may yield empty/invalid selection for assign-only broad mode.
            using var doc = ParseResult(preview);
            var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
            Assert.True(
                code is ClassifyErrors.SelectionInvalid or ClassifyErrors.Lifecycle or ClassifyErrors.Stale,
                code);
        }

        // Explicit correction mixed into exact_rule JSON is rejected as mixed mode.
        var mixedInput =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evalId)
            + ",\"selection\":{\"mode\":\"exact_rule\",\"ruleVersionId\":" + JsonSerializer.Serialize(ruleVersionId)
            + ",\"correctionItems\":[{\"transactionId\":" + JsonSerializer.Serialize(tx)
            + ",\"outcomeId\":\"x\",\"currentCategoryId\":" + JsonSerializer.Serialize(catA)
            + ",\"targetCategoryId\":" + JsonSerializer.Serialize(catB)
            + ",\"reason\":\"mixed\"}]}}";
        var mixed = await process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(mixedInput, NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, mixed.ExitCode);
    }

    [Fact]
    public async Task UC003_item_results_cover_closed_kind_set_on_success()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 kinds shop");
        var run = await ApplyRunAsync(
            seeded.PreviewId,
            "apply-kinds-" + Guid.NewGuid().ToString("N")[..8],
            NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var doc = ParseResult(run);
        var body = doc.RootElement.GetProperty("result_or_error");
        var kinds = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()!)
            .ToArray();
        Assert.NotEmpty(kinds);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "applied", "already_applied", "rejected", "failed", "unresolved"
        };
        Assert.All(kinds, k => Assert.Contains(k, allowed));
        Assert.Equal(
            body.GetProperty("items").GetArrayLength(),
            body.GetProperty("appliedCount").GetInt32()
            + body.GetProperty("alreadyAppliedCount").GetInt32()
            + body.GetProperty("rejectedCount").GetInt32()
            + body.GetProperty("failedCount").GetInt32()
            + body.GetProperty("unresolvedCount").GetInt32());
    }

    // ── Replay / conflict identity ───────────────────────────────────────────

    [Fact]
    public async Task UC003_identical_replay_returns_prior_results_without_second_allocation()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 replay shop");
        var applyId = "apply-replay-" + Guid.NewGuid().ToString("N")[..8];
        var first = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(first, ClassifyOperationIds.ApplyRun);
        using var firstDoc = ParseResult(first);
        var firstBody = firstDoc.RootElement.GetProperty("result_or_error");
        var firstAllocs = firstBody.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("allocationEventId").GetString())
            .ToArray();
        var afterFirst = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var countAfterFirst = await AllocationEventCountAsync(seeded.TransactionIds[0]);

        var second = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(second, ClassifyOperationIds.ApplyRun);
        using var secondDoc = ParseResult(second);
        var secondBody = secondDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(applyId, secondBody.GetProperty("applyId").GetString());
        Assert.Equal(firstBody.GetProperty("appliedCount").GetInt32(), secondBody.GetProperty("appliedCount").GetInt32());
        var secondAllocs = secondBody.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("allocationEventId").GetString())
            .ToArray();
        Assert.Equal(firstAllocs, secondAllocs);
        Assert.Equal(afterFirst, await SnapshotAllocationsAsync(seeded.TransactionIds));
        Assert.Equal(countAfterFirst, await AllocationEventCountAsync(seeded.TransactionIds[0]));
    }

    [Fact]
    public async Task UC003_conflicting_apply_identity_preserves_original_results()
    {
        // Apply the first authorized preview to completion before seeding a second rule set.
        // A later activation supersedes the active pointer / catalogue and would otherwise make
        // the first preview CLASSIFY-STALE before the shared applyId conflict can be proven.
        var seededA = await SeedSuggestionPreviewAsync("uc003 conf-a");
        var applyId = "apply-conflict-" + Guid.NewGuid().ToString("N")[..8];

        var first = await ApplyRunAsync(seededA.PreviewId, applyId, NextKey());
        AssertClassifySuccess(first, ClassifyOperationIds.ApplyRun);
        using var firstDoc = ParseResult(first);
        var firstBody = firstDoc.RootElement.GetProperty("result_or_error");
        var originalAllocs = firstBody.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("transactionId").GetString()!, i.GetProperty("allocationEventId").GetString()!))
            .ToArray();
        var snapshotA = await SnapshotAllocationsAsync(seededA.TransactionIds);

        var seededB = await SeedSuggestionPreviewAsync("uc003 conf-b");
        var second = await ApplyRunAsync(seededB.PreviewId, applyId, NextKey());
        AssertClassifyError(second, ClassifyErrors.Conflict);

        Assert.Equal(snapshotA, await SnapshotAllocationsAsync(seededA.TransactionIds));
        // Original apply_item terminal allocations remain.
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var items = await runStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
        Assert.Equal(originalAllocs.Length, items.Count);
        foreach (var (tx, alloc) in originalAllocs)
        {
            var row = Assert.Single(items, i => i.TransactionId == tx);
            Assert.Equal(alloc, row.LedgerAllocationId);
            Assert.True(ApplyReplayPolicy.IsTerminalItemState(row.ItemState));
        }
    }

    // ── Interruption / frontier recovery ─────────────────────────────────────

    [Fact]
    public void UC003_policy_frontier_orders_planned_and_unresolved_only()
    {
        Assert.True(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStatePlanned));
        Assert.True(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateUnresolved));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateApplied));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateAlreadyApplied));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateRejected));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateFailed));

        var items = new[]
        {
            (Ordinal: 1, Tx: "tx-b", State: ApplyReplayPolicy.ItemStatePlanned),
            (Ordinal: 0, Tx: "tx-z", State: ApplyReplayPolicy.ItemStateApplied),
            (Ordinal: 0, Tx: "tx-a", State: ApplyReplayPolicy.ItemStateUnresolved),
            (Ordinal: 2, Tx: "tx-c", State: ApplyReplayPolicy.ItemStateRejected)
        };
        var frontier = ApplyReplayPolicy.SelectReplayFrontier(
            items, i => i.Ordinal, i => i.Tx, i => i.State);
        Assert.Equal(2, frontier.Count);
        Assert.Equal("tx-a", frontier[0].Tx);
        Assert.Equal("tx-b", frontier[1].Tx);
        Assert.Equal(2, ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.State)));
    }

    [Fact]
    public void UC003_policy_item_kind_mapping_is_closed()
    {
        Assert.Equal(
            (ApplyReplayPolicy.ItemStateApplied, ClassifyApplyItemResultKind.Applied),
            ApplyReplayPolicy.MapLedgerOutcome(true, null, false));
        Assert.Equal(
            (ApplyReplayPolicy.ItemStateRejected, ClassifyApplyItemResultKind.Rejected),
            ApplyReplayPolicy.MapLedgerOutcome(false, "LEDGER-CATEGORY-ALLOCATION-CARDINALITY", false));
        Assert.Equal(
            (ApplyReplayPolicy.ItemStateAlreadyApplied, ClassifyApplyItemResultKind.AlreadyApplied),
            ApplyReplayPolicy.MapLedgerOutcome(false, "LEDGER-CATEGORY-ALLOCATION-UNCHANGED", true));
        Assert.Equal(
            (ApplyReplayPolicy.ItemStateFailed, ClassifyApplyItemResultKind.Failed),
            ApplyReplayPolicy.MapLedgerOutcome(false, "SOMETHING-WEIRD", false));
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition("planned", "unresolved"));
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition("unresolved", "applied"));
        Assert.False(ApplyReplayPolicy.IsValidItemStateTransition("applied", "planned"));
    }

    [Fact]
    public async Task UC003_crash_before_ledger_leaves_planned_and_resume_applies_once()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 crash-before");
        var applyId = "apply-cbl-" + Guid.NewGuid().ToString("N")[..8];
        await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        {
            var items = await runStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.Equal(ApplyReplayPolicy.ItemStatePlanned, i.ItemState));
            var run = await runStore.GetRunAsync(connection, null, applyId, CancellationToken.None);
            Assert.Equal(ApplyReplayPolicy.RunLifecycleRunning, run!.LifecycleState);
            Assert.True(run.UnresolvedFrontier >= 1);
        }

        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var resumed = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(resumed, ClassifyOperationIds.ApplyRun);
        using var doc = ParseResult(resumed);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("appliedCount").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("unresolvedCount").GetInt32());

        var after = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.NotEqual(before[seeded.TransactionIds[0]], after[seeded.TransactionIds[0]]);

        var again = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(again, ClassifyOperationIds.ApplyRun);
        Assert.Equal(after, await SnapshotAllocationsAsync(seeded.TransactionIds));
        Assert.Equal(1, await AllocationEventCountAsync(seeded.TransactionIds[0]));
    }

    [Fact]
    public async Task UC003_crash_after_ledger_before_classify_result_resume_is_at_most_once()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 crash-after");
        var applyId = "apply-cal-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);
        var item = planned[0];

        // External Ledger commit with the frozen per-item key (process died before CLASSIFY wrote result).
        var assign = await ledger.AssignCategoryAsync(
            ClassifyContractMapper.ToAssignInput(item),
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc003", "run-01"),
            item.LedgerIdempotencyKey,
            CancellationToken.None);
        Assert.True(assign.IsSuccess, assign.Error?.Code);
        var externalAlloc = assign.Value!.AllocationEventId;
        var countAfterExternal = await AllocationEventCountAsync(item.TransactionId);

        var resumed = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(resumed, ClassifyOperationIds.ApplyRun);
        using var doc = ParseResult(resumed);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("appliedCount").GetInt32() + body.GetProperty("alreadyAppliedCount").GetInt32() >= 1);
        var itemResult = body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("transactionId").GetString() == item.TransactionId);
        var recordedAlloc = itemResult.GetProperty("allocationEventId").GetString();
        // Resume must reconcile to the exact external AllocationEventId produced under the frozen key.
        Assert.Equal(externalAlloc, recordedAlloc);
        // No second allocation event for the transaction.
        Assert.Equal(countAfterExternal, await AllocationEventCountAsync(item.TransactionId));
    }

    [Fact]
    public async Task UC003_partial_frontier_resumes_only_remaining_items()
    {
        // Two assignable suggestions under one evaluation/preview.
        var category = await CreateCategoryAsync("Uc003Multi");
        await SaveAndActivateAsync(category, "uc003 multi", broadApply: false);
        var tx1 = await RecordTransactionAsync("uc003 multi");
        // Second distinct description matching same rule via equals — need second rule token.
        // Use exact same description so both match the single equals rule.
        var tx2 = await RecordTransactionAsync("uc003 multi");
        var evalId = await EvaluateSuccessAsync();
        var o1 = await OutcomeGetBodyAsync(evalId, tx1);
        var o2 = await OutcomeGetBodyAsync(evalId, tx2);
        Assert.Equal("suggestion", o1.Kind);
        Assert.Equal("suggestion", o2.Kind);
        var outcomeIds = new[] { o1.OutcomeId, o2.OutcomeId };
        var preview = await PreviewSelectedAsync(evalId, outcomeIds, NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var previewDoc = ParseResult(preview);
        var previewId = previewDoc.RootElement.GetProperty("result_or_error").GetProperty("previewId").GetString()!;
        Assert.Equal(2, previewDoc.RootElement.GetProperty("result_or_error").GetProperty("selectedCount").GetInt32());

        var applyId = "apply-partial-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(previewId, applyId);
        Assert.Equal(2, planned.Count);

        // Complete first ordinal as applied with a real Ledger assignment under frozen key.
        var first = planned.OrderBy(i => i.Ordinal).First();
        var assign = await ledger.AssignCategoryAsync(
            ClassifyContractMapper.ToAssignInput(first),
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc003", "run-01"),
            first.LedgerIdempotencyKey,
            CancellationToken.None);
        Assert.True(assign.IsSuccess, assign.Error?.Code);
        await CompleteItemAsync(
            applyId,
            first.Ordinal,
            ApplyReplayPolicy.ItemStatePlanned,
            ApplyReplayPolicy.ItemStateApplied,
            resultFingerprint: new string('a', 64),
            allocationId: assign.Value!.AllocationEventId,
            priorAllocationId: null,
            error: null);

        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        {
            var items = await runStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
            var frontier = ApplyReplayPolicy.SelectReplayFrontier(
                items, i => i.Ordinal, i => i.TransactionId, i => i.ItemState);
            Assert.Single(frontier);
            Assert.Equal(planned.OrderBy(i => i.Ordinal).Last().TransactionId, frontier[0].TransactionId);
        }

        var countFirst = await AllocationEventCountAsync(first.TransactionId);
        var secondTx = planned.OrderBy(i => i.Ordinal).Last().TransactionId;
        var countSecondBefore = await AllocationEventCountAsync(secondTx);

        var resumed = await ApplyRunAsync(previewId, applyId, NextKey());
        AssertClassifySuccess(resumed, ClassifyOperationIds.ApplyRun);
        using var runDoc = ParseResult(resumed);
        var body = runDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(0, body.GetProperty("unresolvedCount").GetInt32());
        // First item allocation count unchanged; second gains exactly one.
        Assert.Equal(countFirst, await AllocationEventCountAsync(first.TransactionId));
        Assert.Equal(countSecondBefore + 1, await AllocationEventCountAsync(secondTx));
    }

    [Fact]
    public async Task UC003_zero_duplicate_allocations_after_double_resume()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 double-resume");
        var applyId = "apply-dbl-" + Guid.NewGuid().ToString("N")[..8];
        await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        var r1 = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(r1, ClassifyOperationIds.ApplyRun);
        var count = await AllocationEventCountAsync(seeded.TransactionIds[0]);
        Assert.Equal(1, count);

        var r2 = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(r2, ClassifyOperationIds.ApplyRun);
        Assert.Equal(1, await AllocationEventCountAsync(seeded.TransactionIds[0]));
        using var d1 = ParseResult(r1);
        using var d2 = ParseResult(r2);
        Assert.Equal(
            d1.RootElement.GetProperty("result_or_error").GetProperty("items")[0].GetProperty("allocationEventId").GetString(),
            d2.RootElement.GetProperty("result_or_error").GetProperty("items")[0].GetProperty("allocationEventId").GetString());
    }

    [Fact]
    public async Task UC003_frozen_item_idempotency_key_stable_per_apply_and_tx()
    {
        var a = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-1");
        var b = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-1");
        var c = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-2");
        var d = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-2", "tx-1");
        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);

        var seeded = await SeedSuggestionPreviewAsync("uc003 frozen-key");
        var applyId = "apply-key-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);
        Assert.All(planned, i =>
        {
            Assert.Equal(
                ClassifyContractMapper.DeriveItemIdempotencyKey(applyId, i.TransactionId),
                i.LedgerIdempotencyKey);
            Assert.Equal(64, i.LedgerRequestFingerprint.Length);
            Assert.Equal(ApplyReplayPolicy.LedgerOperationAssign, i.LedgerOperationId);
        });
    }

    [Fact]
    public async Task UC003_run_missing_preview_and_guards()
    {
        var missing = await ApplyRunAsync("missing-preview", "apply-x", NextKey());
        AssertClassifyError(missing, ClassifyErrors.PreviewNotFound);

        // Process-boundary preflight rejects a missing required idempotency key before the
        // CLASSIFY handler runs (published envelope schema), so the stable process code is
        // validation.invalid_input rather than CLASSIFY-IDEMPOTENCY-REQUIRED.
        var noKey = await process.RunAsync(
            ["classify", "apply", "run", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"1.0","previewId":"p","applyId":"a"}""",
                idempotencyKey: null),
            CancellationToken.None);
        Assert.NotEqual(0, noKey.ExitCode);
        using (var noKeyDoc = ParseResult(noKey))
        {
            Assert.Equal("error", noKeyDoc.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "validation.invalid_input",
                noKeyDoc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        }
        Assert.StartsWith("tally: ", noKey.Stderr, StringComparison.Ordinal);

        var badVer = await process.RunAsync(
            ["classify", "apply", "run", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"9.9","previewId":"p","applyId":"a"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifyError(badVer, ClassifyErrors.UnsupportedVersion);
    }

    [Fact]
    public async Task UC003_operation_idempotency_replays_run_envelope()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 op-idem");
        var applyId = "apply-op-" + Guid.NewGuid().ToString("N")[..8];
        var key = NextKey();
        var first = await ApplyRunAsync(seeded.PreviewId, applyId, key);
        AssertClassifySuccess(first, ClassifyOperationIds.ApplyRun);
        using var firstDoc = ParseResult(first);
        var firstBody = firstDoc.RootElement.GetProperty("result_or_error");

        var second = await ApplyRunAsync(seeded.PreviewId, applyId, key);
        AssertClassifySuccess(second, ClassifyOperationIds.ApplyRun);
        using var secondDoc = ParseResult(second);
        var secondBody = secondDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(firstBody.GetProperty("applyId").GetString(), secondBody.GetProperty("applyId").GetString());
        Assert.Equal(firstBody.GetProperty("appliedCount").GetInt32(), secondBody.GetProperty("appliedCount").GetInt32());
        Assert.Equal(1, await AllocationEventCountAsync(seeded.TransactionIds[0]));
    }

    [Fact]
    public async Task UC003_preview_idempotent_replay_is_stable_and_read_only()
    {
        var category = await CreateCategoryAsync("Uc003PrevIdem");
        await SaveAndActivateAsync(category, "uc003 prev-idem", broadApply: false);
        var tx = await RecordTransactionAsync("uc003 prev-idem");
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);
        var key = NextKey();
        var before = await SnapshotAllocationsAsync([tx]);

        var first = await PreviewSelectedAsync(evalId, [outcome.OutcomeId], key);
        AssertClassifySuccess(first, ClassifyOperationIds.ApplyPreview);
        using var firstDoc = ParseResult(first);
        var firstBody = firstDoc.RootElement.GetProperty("result_or_error");

        var second = await PreviewSelectedAsync(evalId, [outcome.OutcomeId], key);
        AssertClassifySuccess(second, ClassifyOperationIds.ApplyPreview);
        using var secondDoc = ParseResult(second);
        var secondBody = secondDoc.RootElement.GetProperty("result_or_error");
        Assert.Equal(firstBody.GetProperty("previewId").GetString(), secondBody.GetProperty("previewId").GetString());
        Assert.Equal(firstBody.GetProperty("selectionHash").GetString(), secondBody.GetProperty("selectionHash").GetString());
        Assert.Equal(before, await SnapshotAllocationsAsync([tx]));
    }

    [Fact]
    public async Task UC003_structured_result_covers_every_requested_item()
    {
        var seeded = await SeedSuggestionPreviewAsync("uc003 structure");
        var applyId = "apply-str-" + Guid.NewGuid().ToString("N")[..8];
        var run = await ApplyRunAsync(seeded.PreviewId, applyId, NextKey());
        AssertClassifySuccess(run, ClassifyOperationIds.ApplyRun);
        using var doc = ParseResult(run);
        var body = doc.RootElement.GetProperty("result_or_error");
        var resultTx = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("transactionId").GetString()!)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            seeded.TransactionIds.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            resultTx);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var durable = await runStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
        Assert.Equal(resultTx.Length, durable.Count);
        Assert.All(resultTx, tx => Assert.Contains(durable, row => row.TransactionId == tx));
    }

    // ── Seed / process helpers ───────────────────────────────────────────────

    private sealed record SeededPreview(
        string PreviewId,
        string EvaluationId,
        IReadOnlyList<string> TransactionIds,
        ProcessResult PreviewResult);

    private async Task<SeededPreview> SeedSuggestionPreviewAsync(string description, bool broadApply = false)
    {
        var category = await CreateCategoryAsync("Uc003Cat");
        await SaveAndActivateAsync(category, description, broadApply);
        var tx = await RecordTransactionAsync(description);
        var evalId = await EvaluateSuccessAsync();
        var outcome = await OutcomeGetBodyAsync(evalId, tx);
        Assert.Equal("suggestion", outcome.Kind);
        var preview = await PreviewSelectedAsync(evalId, [outcome.OutcomeId], NextKey());
        AssertClassifySuccess(preview, ClassifyOperationIds.ApplyPreview);
        using var doc = ParseResult(preview);
        var previewId = doc.RootElement.GetProperty("result_or_error").GetProperty("previewId").GetString()!;
        return new SeededPreview(previewId, evalId, [tx], preview);
    }

    private async Task<string> SaveAndActivateAsync(string categoryId, string description, bool broadApply)
    {
        var versionId = await SaveRuleAsync(categoryId, description);
        await ActivateRulesAsync([versionId], [(description, "suggestion", categoryId)], broadApply);
        return versionId;
    }

    private async Task ActivateRulesAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows,
        bool broadApply)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";

        var rep = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var repBody = repDoc.RootElement.GetProperty("result_or_error");
        var validationId = repBody.GetProperty("validationId").GetString()!;
        Assert.True(repBody.GetProperty("activationEligible").GetBoolean(), "rep not eligible: " + rep.Stdout);

        var replay = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(replay, ClassifyOperationIds.RuleValidate);
        using var replayDoc = ParseResult(replay);
        var replayBody = replayDoc.RootElement.GetProperty("result_or_error");
        var replayId = replayBody.GetProperty("validationId").GetString()!;
        Assert.True(replayBody.GetProperty("activationEligible").GetBoolean(), "replay not eligible: " + replay.Stdout);
        Assert.Equal(repBody.GetProperty("outcomesCanonicalHash").GetString(), replayBody.GetProperty("outcomesCanonicalHash").GetString());
        Assert.Equal(
            repBody.GetProperty("reportFingerprint").GetString(),
            replayBody.GetProperty("reportFingerprint").GetString());
        Assert.NotEqual(validationId, replayId);

        var hold = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}},"representativeValidationId":{{JsonSerializer.Serialize(validationId)}},"independentReplayValidationId":{{JsonSerializer.Serialize(replayId)}},"ownerDecisionCountBefore":10,"ownerDecisionCountAfter":2,"explicitBenefitDecision":"approve-broad"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var holdBody = holdDoc.RootElement.GetProperty("result_or_error");
        var receiptId = holdBody.GetProperty("ownerRulebookGateReceiptId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(receiptId), "missing receipt: " + hold.Stdout);

        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT authority_granted, block_code, safety_passed FROM owner_rulebook_gate_receipt WHERE receipt_id = $id;";
            cmd.Parameters.AddWithValue("$id", receiptId!);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "receipt missing");
            Assert.True(
                reader.GetInt64(0) == 1 && reader.IsDBNull(1) && reader.GetInt64(2) == 1,
                $"receipt not granted: auth={reader.GetInt64(0)} block={(reader.IsDBNull(1) ? "null" : reader.GetString(1))} safety={reader.GetInt64(2)} hold={hold.Stdout}");
        }

        var activated = await process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":{{(broadApply ? "true" : "false")}},"reason":"uc003 activate"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
    }

    private async Task<string> SaveRuleAsync(string categoryId, string description, string? ruleId = null)
    {
        var id = ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc003 draft"}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var txId = await RecordTransactionAsync(row.Description);
            created.Add((txId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc003", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<string> EvaluateSuccessAsync()
    {
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"1.0"}""", NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString()!;
    }

    private sealed record OutcomeInfo(string OutcomeId, string Kind, string? SuggestedCategoryId);

    private async Task<OutcomeInfo> OutcomeGetBodyAsync(string evaluationId, string transactionId)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"transactionId\":" + JsonSerializer.Serialize(transactionId) + "}";
        var result = await process.RunAsync(
            ["classify", "outcome", "get", "--input", "-"],
            ClassifyEnvelope(input, idempotencyKey: null),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.OutcomeGet);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        return new OutcomeInfo(
            body.GetProperty("outcomeId").GetString()!,
            body.GetProperty("kind").GetString()!,
            body.TryGetProperty("suggestedCategoryId", out var cat) && cat.ValueKind == JsonValueKind.String
                ? cat.GetString()
                : null);
    }

    private async Task<Dictionary<string, OutcomeInfo>> ListOutcomeIdsByKindAsync(
        string evaluationId,
        IReadOnlyList<string> transactionIds)
    {
        var map = new Dictionary<string, OutcomeInfo>(StringComparer.Ordinal);
        foreach (var tx in transactionIds)
        {
            map[tx] = await OutcomeGetBodyAsync(evaluationId, tx);
        }

        return map;
    }

    private Task<ProcessResult> PreviewSelectedAsync(string evaluationId, IReadOnlyList<string> outcomeIds, string key)
    {
        var ids = "[" + string.Join(",", outcomeIds.Select(id => JsonSerializer.Serialize(id))) + "]";
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"selection\":{\"mode\":\"selected_outcomes\",\"outcomeIds\":" + ids + "}}";
        return process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private Task<ProcessResult> PreviewExactRuleAsync(string evaluationId, string ruleVersionId, string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"selection\":{\"mode\":\"exact_rule\",\"ruleVersionId\":" + JsonSerializer.Serialize(ruleVersionId)
            + "}}";
        return process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private Task<ProcessResult> PreviewCorrectionsAsync(
        string evaluationId,
        string transactionId,
        string outcomeId,
        string currentCategoryId,
        string targetCategoryId,
        string reason,
        string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"evaluationId\":" + JsonSerializer.Serialize(evaluationId)
            + ",\"selection\":{\"mode\":\"explicit_corrections\",\"correctionItems\":[{"
            + "\"transactionId\":" + JsonSerializer.Serialize(transactionId)
            + ",\"outcomeId\":" + JsonSerializer.Serialize(outcomeId)
            + ",\"currentCategoryId\":" + JsonSerializer.Serialize(currentCategoryId)
            + ",\"targetCategoryId\":" + JsonSerializer.Serialize(targetCategoryId)
            + ",\"reason\":" + JsonSerializer.Serialize(reason)
            + "}]}}";
        return process.RunAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private Task<ProcessResult> ApplyRunAsync(string previewId, string applyId, string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"previewId\":" + JsonSerializer.Serialize(previewId)
            + ",\"applyId\":" + JsonSerializer.Serialize(applyId) + "}";
        return process.RunAsync(
            ["classify", "apply", "run", "--input", "-"],
            ClassifyEnvelope(input, key),
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<ClassifyApplyItemRow>> FreezePlannedRunWithoutLedgerAsync(
        string previewId,
        string applyId)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var preview = await previewStore.GetPreviewAsync(connection, null, previewId, CancellationToken.None);
        Assert.NotNull(preview);
        var previewItems = await previewStore.ListItemsAsync(connection, null, previewId, CancellationToken.None);
        Assert.NotEmpty(previewItems);

        var fingerprint = ClassifyContractMapper.ComputeApplyRunRequestFingerprint(
            applyId, preview!, previewItems);
        var planned = previewItems
            .OrderBy(i => i.Ordinal)
            .Select(i => ClassifyContractMapper.ToPlannedApplyItemRow(applyId, i))
            .ToArray();
        var run = ClassifyContractMapper.ToApplyRunRow(
            applyId,
            previewId,
            fingerprint,
            ApplyReplayPolicy.RunLifecycleRunning,
            planned.Length,
            "automation:classify-uc003:run-01",
            ClassifyContractMapper.FormatUtc(DateTimeOffset.UtcNow));

        await store.ExecuteWriteAsync(
            async (conn, tx, ct) =>
            {
                await runStore.InsertRunAsync(conn, tx, run, ct);
                await runStore.InsertItemsAsync(conn, tx, planned, ct);
                return true;
            },
            CancellationToken.None);

        return planned;
    }

    private async Task CompleteItemAsync(
        string applyId,
        int ordinal,
        string expectedPrior,
        string next,
        string? resultFingerprint,
        string? allocationId,
        string? priorAllocationId,
        string? error)
    {
        await store.ExecuteWriteAsync(
            async (conn, tx, ct) =>
            {
                var ok = await runStore.TryCompleteItemAsync(
                    conn, tx, applyId, ordinal, expectedPrior, next,
                    resultFingerprint, allocationId, priorAllocationId, error, ct);
                Assert.True(ok);
                var items = await runStore.ListItemsAsync(conn, tx, applyId, ct);
                var frontier = ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.ItemState));
                await runStore.UpdateRunFrontierAsync(conn, tx, applyId, frontier, ct);
                return true;
            },
            CancellationToken.None);
    }

    private async Task<Dictionary<string, string?>> SnapshotAllocationsAsync(IReadOnlyList<string> transactionIds)
    {
        var ordered = transactionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc003", "run-01"),
            CancellationToken.None,
            transactionIds: ordered);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var id in ordered)
        {
            var item = page.Value!.ClassificationItems?
                .FirstOrDefault(i => string.Equals(i.TransactionId, id, StringComparison.Ordinal));
            map[id] = item?.CurrentAllocationId;
        }

        return map;
    }

    private async Task<long> AllocationEventCountAsync(string transactionId)
    {
        var path = LedgerDatabasePath();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM category_allocation_event WHERE transaction_id = $tx;";
        command.Parameters.AddWithValue("$tx", transactionId);
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<long> ApplyRunCountAsync()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        return await runStore.CountRunsAsync(connection, null, CancellationToken.None);
    }

    private async Task<string> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var result = await process.RunAsync(
            ["ledger", "account", "create", "--input", "-"],
            LedgerEnvelope(
                $$"""{"institutionName":"Uc003 Bank {{unique}}","displayName":"Primary-{{unique}}","accountType":"cheque","maskedIdentifier":"****{{(Math.Abs(unique.GetHashCode()) % 9000 + 1000)}}","currencyCode":"ZAR"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("accountId").GetString()!;
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var full = name + "-" + Guid.NewGuid().ToString("N")[..6];
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            LedgerEnvelope($$"""{"name":{{JsonSerializer.Serialize(full)}}}""", NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task VoidTransactionAsync(string transactionId)
    {
        var result = await process.RunAsync(
            ["ledger", "transaction", "void", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"reason":"uc003-void"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task AssignCategoryAsync(string transactionId, string categoryId, string reason)
    {
        var result = await process.RunAsync(
            ["ledger", "transaction", "category", "assign", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":{{JsonSerializer.Serialize(reason)}}}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task<string> RecordTransactionAsync(string description, string amount = "-12.34")
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(accountId)}},
              "signedAmount":{{JsonSerializer.Serialize(amount)}},
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(description)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc003:" + Guid.NewGuid().ToString("N")[..8])}}
              }
            }
            """;
        var result = await process.RunAsync(
            ["ledger", "transaction", "record", "--input", "-"],
            LedgerEnvelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("transactionId").GetString()!;
    }

    private static void AssertClassifySuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + "\n" + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("result_or_error", out _));
    }

    private static void AssertClassifyError(ProcessResult result, string errorCode)
    {
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            errorCode,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
    }

    private static JsonDocument ParseResult(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout);

    private static string ClassifyEnvelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc003","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc003","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc003","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc003-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];

    private string LedgerDatabasePath()
    {
        var current = File.ReadAllText(Path.Combine(root, "CURRENT")).Trim();
        return Path.Combine(root, "generations", current, "ledger.db");
    }
}
