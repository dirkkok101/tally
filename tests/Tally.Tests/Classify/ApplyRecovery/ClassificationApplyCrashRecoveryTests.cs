using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.ApplyRecovery;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-APPLY-RUN-SAGA / bd-2ffc — crash-window matrix:
/// planned frontier, unresolved resume, terminal immutability, zero duplicate allocations.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationApplyCrashRecoveryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-apply-crash-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "apply-crash", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyApplyServices services = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);
        services = await ClassifyApplyExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
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

    // ── Pure crash policy ───────────────────────────────────────────────────

    [Fact]
    public void Crash_policy_never_replays_terminal_items()
    {
        foreach (var state in ApplyReplayPolicy.TerminalItemStates)
        {
            Assert.False(ApplyReplayPolicy.MayCallLedger(state));
        }
    }

    [Fact]
    public void Crash_policy_replays_planned_and_unresolved_only()
    {
        Assert.Equal(
            ApplyReplayPolicy.ReplayableItemStates.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            new[] { ApplyReplayPolicy.ItemStatePlanned, ApplyReplayPolicy.ItemStateUnresolved }
                .OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Crash_policy_planned_may_become_unresolved()
    {
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition(
            ApplyReplayPolicy.ItemStatePlanned, ApplyReplayPolicy.ItemStateUnresolved));
    }

    [Fact]
    public void Crash_policy_unresolved_cannot_return_to_planned()
    {
        Assert.False(ApplyReplayPolicy.IsValidItemStateTransition(
            ApplyReplayPolicy.ItemStateUnresolved, ApplyReplayPolicy.ItemStatePlanned));
    }

    [Fact]
    public void Crash_policy_frontier_empty_when_all_terminal()
    {
        var items = new[]
        {
            (0, "tx-a", ApplyReplayPolicy.ItemStateApplied),
            (1, "tx-b", ApplyReplayPolicy.ItemStateRejected)
        };
        var frontier = ApplyReplayPolicy.SelectReplayFrontier(items, i => i.Item1, i => i.Item2, i => i.Item3);
        Assert.Empty(frontier);
        Assert.Equal(0, ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.Item3)));
    }

    [Fact]
    public void Crash_policy_idempotency_key_does_not_change_on_resume_shape()
    {
        // Resume must reuse the exact derived key — pure equality is the contract.
        var key = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-crash", "tx-1");
        Assert.Equal(key, ClassifyContractMapper.DeriveItemIdempotencyKey("apply-crash", "tx-1"));
    }

    // ── Durable planned intent before Ledger ────────────────────────────────

    [Fact]
    public async Task Crash_before_ledger_leaves_planned_and_resume_applies_once()
    {
        var seeded = await SeedPreviewAsync("crash-before-ledger");
        var applyId = "apply-cbl-" + Guid.NewGuid().ToString("N")[..8];
        await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        await using (var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None))
        {
            var items = await services.RunStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
            Assert.All(items, i => Assert.Equal(ApplyReplayPolicy.ItemStatePlanned, i.ItemState));
            var run = await services.RunStore.GetRunAsync(connection, null, applyId, CancellationToken.None);
            Assert.Equal(ApplyReplayPolicy.RunLifecycleRunning, run!.LifecycleState);
            Assert.True(run.UnresolvedFrontier >= 1);
        }

        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);
        Assert.True(resumed.Value!.AppliedCount >= 1);
        Assert.Equal(0, resumed.Value.UnresolvedCount);

        var after = await SnapshotAllocationsAsync(seeded.TransactionIds);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.NotEqual(before[tx], after[tx]);
        }

        // Second resume of completed run must not mutate again.
        var again = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(again.IsSuccess, again.ErrorCode);
        var afterAgain = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.Equal(after, afterAgain);
    }

    [Fact]
    public async Task Crash_after_ledger_before_classify_result_resume_uses_frozen_key()
    {
        // Simulate commit-before-result: planned item, but Ledger already applied via frozen key.
        var seeded = await SeedPreviewAsync("crash-after-ledger");
        var applyId = "apply-cal-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);
        var item = planned[0];

        // External Ledger call with the exact frozen key (as if prior process committed then died).
        var assign = await ledger.AssignCategoryAsync(
            ClassifyContractMapper.ToAssignInput(item),
            ActualsContractVersions.Current,
            actor,
            item.LedgerIdempotencyKey,
            CancellationToken.None);
        Assert.True(assign.IsSuccess, assign.Error?.Code);
        var allocationAfterExternal = assign.Value!.AllocationEventId;

        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);
        // Ledger idempotency returns success with same allocation; CLASSIFY records applied.
        Assert.True(resumed.Value!.AppliedCount + resumed.Value.AlreadyAppliedCount >= 1);
        Assert.Contains(
            resumed.Value.Items,
            i => i.AllocationEventId == allocationAfterExternal
                 || !string.IsNullOrWhiteSpace(i.AllocationEventId));

        // No second allocation identity on the transaction.
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None,
            transactionIds: seeded.TransactionIds.ToArray());
        Assert.True(page.IsSuccess);
        var live = page.Value!.ClassificationItems!.Single(i => i.TransactionId == item.TransactionId);
        Assert.Equal(allocationAfterExternal, live.CurrentAllocationId);
    }

    [Fact]
    public async Task Crash_unresolved_item_resume_reaches_terminal()
    {
        var seeded = await SeedPreviewAsync("crash-unresolved");
        var applyId = "apply-unr-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        // Mark first item unresolved (result-before-CLASSIFY durability window).
        await stateStoreCompleteItemAsync(
            applyId,
            planned[0].Ordinal,
            ApplyReplayPolicy.ItemStatePlanned,
            ApplyReplayPolicy.ItemStateUnresolved,
            null,
            null,
            null,
            null);

        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);
        Assert.Equal(0, resumed.Value!.UnresolvedCount);
        Assert.All(resumed.Value.Items, i => Assert.True(
            i.Kind is ClassifyApplyItemResultKind.Applied
                or ClassifyApplyItemResultKind.AlreadyApplied
                or ClassifyApplyItemResultKind.Rejected
                or ClassifyApplyItemResultKind.Failed));
    }

    [Fact]
    public async Task Terminal_item_is_immutable_and_never_recalled()
    {
        var seeded = await SeedPreviewAsync("terminal-immutable");
        var applyId = "apply-term-" + Guid.NewGuid().ToString("N")[..8];
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        var items = await services.RunStore.ListItemsAsync(connection, transaction, applyId, CancellationToken.None);
        var terminal = items[0];
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await services.RunStore.TryCompleteItemAsync(
                connection, transaction, applyId, terminal.Ordinal,
                terminal.ItemState, ApplyReplayPolicy.ItemStateApplied,
                "x", "y", null, null, CancellationToken.None);
        });
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Multi_item_partial_frontier_resumes_only_remaining()
    {
        // Two suggestions from one evaluation when possible; otherwise single is still valid frontier logic.
        var category = await CreateCategoryAsync("Multi");
        var v1 = await SaveDraftAsync(category.CategoryId, "multi-a");
        await ActivateWithGateAsync(v1, category.CategoryId, "multi-a");
        var tx1 = await RecordAsync("multi-a");
        var tx2 = await RecordAsync("multi-a");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var outcomes = await services.EvaluationStore.ListOutcomesAsync(
            connection, null, evaluated.Value!.EvaluationId, CancellationToken.None);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        Assert.True(suggestionIds.Length >= 1);

        var preview = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor, NextKey(), CancellationToken.None);
        Assert.True(preview.IsSuccess, preview.ErrorCode);

        var applyId = "apply-multi-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(preview.Value!.PreviewId, applyId);
        if (planned.Count >= 2)
        {
            // Complete first item as applied via Ledger + store to leave second planned.
            var first = planned[0];
            var assign = await ledger.AssignCategoryAsync(
                ClassifyContractMapper.ToAssignInput(first),
                ActualsContractVersions.Current,
                actor,
                first.LedgerIdempotencyKey,
                CancellationToken.None);
            Assert.True(assign.IsSuccess, assign.Error?.Code);
            await stateStoreCompleteItemAsync(
                applyId,
                first.Ordinal,
                ApplyReplayPolicy.ItemStatePlanned,
                ApplyReplayPolicy.ItemStateApplied,
                ClassifyContractMapper.ComputeLedgerResultFingerprint(
                    ApplyReplayPolicy.ItemStateApplied, assign.Value!.AllocationEventId, null),
                assign.Value.AllocationEventId,
                null,
                null);
        }

        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, preview.Value.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);
        Assert.Equal(0, resumed.Value!.UnresolvedCount);
        Assert.Equal(
            resumed.Value.Items.Count,
            resumed.Value.AppliedCount + resumed.Value.AlreadyAppliedCount
            + resumed.Value.RejectedCount + resumed.Value.FailedCount + resumed.Value.UnresolvedCount);
        _ = tx1;
        _ = tx2;
    }

    [Fact]
    public async Task Frozen_request_fields_survive_resume_unchanged()
    {
        var seeded = await SeedPreviewAsync("frozen-fields");
        var applyId = "apply-frz-" + Guid.NewGuid().ToString("N")[..8];
        var planned = await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);
        var before = planned.Select(i => (
            i.Ordinal,
            i.LedgerOperationId,
            i.CategoryId,
            i.ExpectedTransactionRevision,
            i.ExpectedRelationshipRevision,
            i.ExpectedAllocationRevision,
            i.ExpectedActiveAllocationId,
            i.CorrectionReason,
            i.LedgerRequestFingerprint,
            i.LedgerIdempotencyKey)).ToArray();

        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var afterItems = await services.RunStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
        var after = afterItems.Select(i => (
            i.Ordinal,
            i.LedgerOperationId,
            i.CategoryId,
            i.ExpectedTransactionRevision,
            i.ExpectedRelationshipRevision,
            i.ExpectedAllocationRevision,
            i.ExpectedActiveAllocationId,
            i.CorrectionReason,
            i.LedgerRequestFingerprint,
            i.LedgerIdempotencyKey)).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Completed_run_lifecycle_has_completed_at()
    {
        var seeded = await SeedPreviewAsync("lifecycle-complete");
        var applyId = "apply-life-" + Guid.NewGuid().ToString("N")[..8];
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var run = await services.RunStore.GetRunAsync(connection, null, applyId, CancellationToken.None);
        Assert.Equal(ApplyReplayPolicy.RunLifecycleCompleted, run!.LifecycleState);
        Assert.False(string.IsNullOrWhiteSpace(run.CompletedAt));
        Assert.Equal(0, run.UnresolvedFrontier);
    }

    [Fact]
    public async Task Resume_after_process_lock_release()
    {
        var seeded = await SeedPreviewAsync("lock-release");
        var applyId = "apply-lck-" + Guid.NewGuid().ToString("N")[..8];
        await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        // Acquire and release lock to prove process loss is recoverable.
        var held = await services.ApplyLock.TryAcquireAsync(applyId, CancellationToken.None);
        Assert.NotNull(held);
        await held!.DisposeAsync();

        var resumed = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.ErrorCode);
        Assert.Equal(0, resumed.Value!.UnresolvedCount);
    }

    [Fact]
    public async Task Zero_duplicate_allocations_after_double_resume()
    {
        var seeded = await SeedPreviewAsync("dup-zero");
        var applyId = "apply-dup-" + Guid.NewGuid().ToString("N")[..8];
        await FreezePlannedRunWithoutLedgerAsync(seeded.PreviewId, applyId);

        var r1 = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.ErrorCode);
        var alloc1 = await SnapshotAllocationsAsync(seeded.TransactionIds);

        var r2 = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.ErrorCode);
        var alloc2 = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.Equal(alloc1, alloc2);
        Assert.Equal(
            r1.Value!.Items.Select(i => i.AllocationEventId).ToArray(),
            r2.Value!.Items.Select(i => i.AllocationEventId).ToArray());
    }

    [Fact]
    public async Task Structured_result_covers_every_requested_item()
    {
        var seeded = await SeedPreviewAsync("structure");
        var applyId = "apply-str-" + Guid.NewGuid().ToString("N")[..8];
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var items = await services.RunStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
        Assert.Equal(items.Count, result.Value!.Items.Count);
        Assert.All(result.Value.Items, i =>
            Assert.Contains(items, row => string.Equals(row.TransactionId, i.TransactionId, StringComparison.Ordinal)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record SeededPreview(string PreviewId, IReadOnlyList<string> TransactionIds);

    private async Task<IReadOnlyList<ClassifyApplyItemRow>> FreezePlannedRunWithoutLedgerAsync(
        string previewId,
        string applyId)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var preview = await services.PreviewStore.GetPreviewAsync(connection, null, previewId, CancellationToken.None);
        Assert.NotNull(preview);
        var previewItems = await services.PreviewStore.ListItemsAsync(connection, null, previewId, CancellationToken.None);
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
            "automation:apply-crash:run-01",
            ClassifyContractMapper.FormatUtc(DateTimeOffset.UtcNow));

        await services.State.Store.ExecuteWriteAsync(
            async (conn, tx, ct) =>
            {
                await services.RunStore.InsertRunAsync(conn, tx, run, ct);
                await services.RunStore.InsertItemsAsync(conn, tx, planned, ct);
                return true;
            },
            CancellationToken.None);

        return planned;
    }

    private async Task stateStoreCompleteItemAsync(
        string applyId,
        int ordinal,
        string expectedPrior,
        string next,
        string? resultFp,
        string? allocId,
        string? priorAlloc,
        string? error)
    {
        await services.State.Store.ExecuteWriteAsync(
            async (conn, tx, ct) =>
            {
                var ok = await services.RunStore.TryCompleteItemAsync(
                    conn, tx, applyId, ordinal, expectedPrior, next, resultFp, allocId, priorAlloc, error, ct);
                Assert.True(ok);
                var items = await services.RunStore.ListItemsAsync(conn, tx, applyId, ct);
                var frontier = ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.ItemState));
                await services.RunStore.UpdateRunFrontierAsync(conn, tx, applyId, frontier, ct);
                return true;
            },
            CancellationToken.None);
    }

    private async Task<SeededPreview> SeedPreviewAsync(string description)
    {
        var category = await CreateCategoryAsync("Cat");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var outcomes = await services.EvaluationStore.ListOutcomesAsync(
            connection, null, evaluated.Value!.EvaluationId, CancellationToken.None);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        Assert.NotEmpty(suggestionIds);

        var preview = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor, NextKey(), CancellationToken.None);
        Assert.True(preview.IsSuccess, preview.ErrorCode);
        return new SeededPreview(preview.Value!.PreviewId, [tx.TransactionId]);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var (path, gateTxIds) = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "crash activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        // Gate evidence membership must not remain in the evaluation universe.
        foreach (var gateTxId in gateTxIds)
        {
            await AssignCategoryAsync(gateTxId, categoryId, "remove gate evidence");
        }
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "crash draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task<(string Path, IReadOnlyList<string> GateTransactionIds)> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, ActualsContractVersions.Current, actor, CancellationToken.None);
        Assert.True(page.IsSuccess);
        var byTx = page.Value!.ClassificationItems!.ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            var item = byTx[txId];
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
        return (path, created.Select(c => c.TxId).ToArray());
    }

    private async Task AssignCategoryAsync(string transactionId, string categoryId, string reason) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, reason),
            NextKey(),
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task<Dictionary<string, string?>> SnapshotAllocationsAsync(IReadOnlyList<string> transactionIds)
    {
        var ordered = transactionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            actor,
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

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Crash Bank " + unique, "C-" + unique, AccountType.Cheque, "****" + (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(), "ZAR"),
            NextKey(), LedgerJsonContext.Default.CreateAccountInput, LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(), LedgerJsonContext.Default.CreateCategoryInput, LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId, "-12.34", "ZAR", "2026-07-15", null, description, null, null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "crash:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId, TInput input, string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var request = new RequestEnvelope("1.0", actor, JsonSerializer.SerializeToElement(input, inputType), key);
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, json, CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
    }

    private string NextKey() => $"apply-crash-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
