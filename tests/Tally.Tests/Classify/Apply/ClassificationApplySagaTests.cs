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

namespace Tally.Tests.Classify.Apply;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-APPLY-RUN-SAGA / bd-2ffc — apply contract: preflight, assign, correct, replay, conflict.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationApplySagaTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-apply-saga-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "apply-saga", "run-01");
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

    // ── Pure policy ─────────────────────────────────────────────────────────

    [Fact]
    public void Policy_terminal_states_are_closed_set()
    {
        Assert.True(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStateApplied));
        Assert.True(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStateAlreadyApplied));
        Assert.True(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStateRejected));
        Assert.True(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStateFailed));
        Assert.False(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStatePlanned));
        Assert.False(ApplyReplayPolicy.IsTerminalItemState(ApplyReplayPolicy.ItemStateUnresolved));
    }

    [Fact]
    public void Policy_only_planned_and_unresolved_may_call_ledger()
    {
        Assert.True(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStatePlanned));
        Assert.True(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateUnresolved));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateApplied));
        Assert.False(ApplyReplayPolicy.MayCallLedger(ApplyReplayPolicy.ItemStateRejected));
    }

    [Fact]
    public void Policy_frontier_orders_by_ordinal_then_transaction()
    {
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
    }

    [Fact]
    public void Policy_unresolved_frontier_counts_non_terminal()
    {
        Assert.Equal(2, ApplyReplayPolicy.ComputeUnresolvedFrontier(
        [
            ApplyReplayPolicy.ItemStatePlanned,
            ApplyReplayPolicy.ItemStateApplied,
            ApplyReplayPolicy.ItemStateUnresolved
        ]));
        Assert.Equal(0, ApplyReplayPolicy.ComputeUnresolvedFrontier(
        [
            ApplyReplayPolicy.ItemStateApplied,
            ApplyReplayPolicy.ItemStateRejected
        ]));
    }

    [Fact]
    public void Policy_map_success_to_applied()
    {
        var (state, kind) = ApplyReplayPolicy.MapLedgerOutcome(true, null, false);
        Assert.Equal(ApplyReplayPolicy.ItemStateApplied, state);
        Assert.Equal(ClassifyApplyItemResultKind.Applied, kind);
    }

    [Fact]
    public void Policy_map_stale_precondition_to_rejected()
    {
        var (state, kind) = ApplyReplayPolicy.MapLedgerOutcome(
            false, CategoryMutationPreconditionCodes.StalePrecondition, false);
        Assert.Equal(ApplyReplayPolicy.ItemStateRejected, state);
        Assert.Equal(ClassifyApplyItemResultKind.Rejected, kind);
    }

    [Fact]
    public void Policy_map_cardinality_to_rejected()
    {
        var (state, kind) = ApplyReplayPolicy.MapLedgerOutcome(
            false, "LEDGER-CATEGORY-ALLOCATION-CARDINALITY", false);
        Assert.Equal(ApplyReplayPolicy.ItemStateRejected, state);
        Assert.Equal(ClassifyApplyItemResultKind.Rejected, kind);
    }

    [Fact]
    public void Policy_map_unchanged_with_match_to_already_applied()
    {
        var (state, kind) = ApplyReplayPolicy.MapLedgerOutcome(
            false, "LEDGER-CATEGORY-ALLOCATION-UNCHANGED", true);
        Assert.Equal(ApplyReplayPolicy.ItemStateAlreadyApplied, state);
        Assert.Equal(ClassifyApplyItemResultKind.AlreadyApplied, kind);
    }

    [Fact]
    public void Policy_map_unknown_code_to_failed()
    {
        var (state, kind) = ApplyReplayPolicy.MapLedgerOutcome(false, "SOMETHING-WEIRD", false);
        Assert.Equal(ApplyReplayPolicy.ItemStateFailed, state);
        Assert.Equal(ClassifyApplyItemResultKind.Failed, kind);
    }

    [Fact]
    public void Policy_valid_transitions()
    {
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition("planned", "applied"));
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition("planned", "unresolved"));
        Assert.True(ApplyReplayPolicy.IsValidItemStateTransition("unresolved", "applied"));
        Assert.False(ApplyReplayPolicy.IsValidItemStateTransition("applied", "planned"));
        Assert.False(ApplyReplayPolicy.IsValidItemStateTransition("rejected", "applied"));
    }

    [Fact]
    public void Mapper_item_idempotency_key_stable_per_apply_and_tx()
    {
        var a = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-1");
        var b = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-1");
        var c = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-1", "tx-2");
        var d = ClassifyContractMapper.DeriveItemIdempotencyKey("apply-2", "tx-1");
        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void Mapper_ledger_operation_resolution()
    {
        Assert.Equal(
            ApplyReplayPolicy.LedgerOperationAssign,
            ApplyReplayPolicy.ResolveLedgerOperationId("assign"));
        Assert.Equal(
            ApplyReplayPolicy.LedgerOperationCorrect,
            ApplyReplayPolicy.ResolveLedgerOperationId("correct"));
        Assert.Null(ApplyReplayPolicy.ResolveLedgerOperationId("broad"));
    }

    [Fact]
    public void Mapper_run_lifecycle_after_frontier()
    {
        Assert.Equal(ApplyReplayPolicy.RunLifecycleCompleted, ApplyReplayPolicy.ResolveRunLifecycleAfterItems(0));
        Assert.Equal(ApplyReplayPolicy.RunLifecycleRunning, ApplyReplayPolicy.ResolveRunLifecycleAfterItems(1));
    }

    // ── Guard rails ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_requires_actor()
    {
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, "p", "a"),
            null, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Run_requires_idempotency_key()
    {
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, "p", "a"),
            actor, null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Run_rejects_unsupported_version()
    {
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest("9.9", "p", "a"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Run_rejects_missing_preview()
    {
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, "missing-preview", "apply-x"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.PreviewNotFound, result.ErrorCode);
    }

    // ── Happy path assign ───────────────────────────────────────────────────

    [Fact]
    public async Task Assign_applies_selected_suggestion_exactly_once()
    {
        var seeded = await SeedPreviewAsync("saga shop");
        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);

        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.PreviewId,
                "apply-" + Guid.NewGuid().ToString("N")[..12]),
            actor, NextKey(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.AppliedCount >= 1);
        Assert.Equal(0, result.Value.UnresolvedCount);
        Assert.Equal(result.Value.Items.Count, result.Value.AppliedCount + result.Value.AlreadyAppliedCount
            + result.Value.RejectedCount + result.Value.FailedCount + result.Value.UnresolvedCount);
        Assert.All(result.Value.Items, i => Assert.Equal(ClassifyApplyItemResultKind.Applied, i.Kind));
        Assert.All(result.Value.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.AllocationEventId)));

        var after = await SnapshotAllocationsAsync(seeded.TransactionIds);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.NotEqual(before[tx], after[tx]);
            Assert.False(string.IsNullOrWhiteSpace(after[tx]));
        }
    }

    [Fact]
    public async Task Assign_identical_replay_returns_prior_terminal_no_second_allocation()
    {
        var seeded = await SeedPreviewAsync("replay shop");
        var applyId = "apply-replay-" + Guid.NewGuid().ToString("N")[..8];
        var first = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var allocsAfterFirst = await SnapshotAllocationsAsync(seeded.TransactionIds);

        var second = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.ApplyId, second.Value!.ApplyId);
        Assert.Equal(first.Value.AppliedCount, second.Value.AppliedCount);
        Assert.Equal(
            first.Value.Items.Select(i => i.AllocationEventId).ToArray(),
            second.Value.Items.Select(i => i.AllocationEventId).ToArray());

        var allocsAfterSecond = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.Equal(allocsAfterFirst, allocsAfterSecond);
    }

    [Fact]
    public async Task Assign_conflicting_apply_identity_with_different_preview_is_stable_conflict()
    {
        var seededA = await SeedPreviewAsync("conflict-a");
        var seededB = await SeedPreviewAsync("conflict-b");
        var applyId = "apply-conflict-" + Guid.NewGuid().ToString("N")[..8];

        var first = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seededA.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seededB.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Conflict, second.ErrorCode);
    }

    [Fact]
    public async Task Assign_stale_preflight_rejects_whole_selection_before_mutation()
    {
        var seeded = await SeedPreviewAsync("stale-preflight");
        // Mutate ledger so frozen revisions no longer match.
        await VoidTransactionAsync(seeded.TransactionIds[0]);

        var before = await SnapshotAllocationsAsync(seeded.TransactionIds);
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.PreviewId,
                "apply-stale-" + Guid.NewGuid().ToString("N")[..8]),
            actor, NextKey(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(
            result.ErrorCode is ClassifyErrors.Stale or ClassifyErrors.SelectionInvalid
                or ClassifyErrors.LedgerUnavailable,
            result.ErrorCode);

        // No apply_run should have been created on first-start preflight failure.
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0, await services.RunStore.CountRunsAsync(connection, null, CancellationToken.None));
        var after = await SnapshotAllocationsAsync(seeded.TransactionIds);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Correct_applies_explicit_correction_with_reason()
    {
        var catA = await CreateCategoryAsync("CorrA");
        var catB = await CreateCategoryAsync("CorrB");
        var versionId = await SaveDraftAsync(catA.CategoryId, "corr-saga");
        await ActivateWithGateAsync(versionId, catA.CategoryId, "corr-saga", false);
        var tx = await RecordAsync("corr-saga");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        await AssignCategoryAsync(tx.TransactionId, catA.CategoryId, "seed");

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var outcomes = await services.EvaluationStore.ListOutcomesAsync(
            connection, null, evaluated.Value!.EvaluationId, CancellationToken.None);
        var outcome = outcomes.Single(o => o.TransactionId == tx.TransactionId);

        var preview = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.ExplicitCorrections,
                    CorrectionItems:
                    [
                        new ClassifyExplicitCorrectionItem(
                            tx.TransactionId, outcome.OutcomeId, catA.CategoryId, catB.CategoryId, "owner fix")
                    ])),
            actor, NextKey(), CancellationToken.None);
        Assert.True(preview.IsSuccess, preview.ErrorCode);

        var before = await SnapshotAllocationsAsync([tx.TransactionId]);
        var run = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(
                ClassifyOperationIds.ContractVersion,
                preview.Value!.PreviewId,
                "apply-corr-" + Guid.NewGuid().ToString("N")[..8]),
            actor, NextKey(), CancellationToken.None);
        Assert.True(run.IsSuccess, run.ErrorCode);
        Assert.Equal(1, run.Value!.AppliedCount);
        Assert.Equal(ClassifyApplyItemResultKind.Applied, run.Value.Items[0].Kind);

        var after = await SnapshotAllocationsAsync([tx.TransactionId]);
        Assert.NotEqual(before[tx.TransactionId], after[tx.TransactionId]);
    }

    [Fact]
    public async Task Run_persists_frozen_intent_before_results()
    {
        var seeded = await SeedPreviewAsync("intent shop");
        var applyId = "apply-intent-" + Guid.NewGuid().ToString("N")[..8];
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var items = await services.RunStore.ListItemsAsync(connection, null, applyId, CancellationToken.None);
        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.Equal(64, i.LedgerRequestFingerprint.Length);
            Assert.False(string.IsNullOrWhiteSpace(i.LedgerIdempotencyKey));
            Assert.Equal(ApplyReplayPolicy.LedgerOperationAssign, i.LedgerOperationId);
            Assert.True(ApplyReplayPolicy.IsTerminalItemState(i.ItemState));
            Assert.Equal(
                ClassifyContractMapper.DeriveItemIdempotencyKey(applyId, i.TransactionId),
                i.LedgerIdempotencyKey);
        });
    }

    [Fact]
    public async Task Run_operation_idempotency_replays_envelope()
    {
        var seeded = await SeedPreviewAsync("op-idem");
        var applyId = "apply-op-" + Guid.NewGuid().ToString("N")[..8];
        var key = NextKey();
        var request = new ClassifyApplyRunRequest(
            ClassifyOperationIds.ContractVersion, seeded.PreviewId, applyId);
        var first = await services.Run.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await services.Run.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.ApplyId, second.Value!.ApplyId);
        Assert.Equal(first.Value.AppliedCount, second.Value.AppliedCount);
    }

    [Fact]
    public async Task Run_cancellation_propagates()
    {
        var seeded = await SeedPreviewAsync("cancel-run");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.PreviewId,
                "apply-cancel-" + Guid.NewGuid().ToString("N")[..8]),
            actor, NextKey(), cts.Token);
        Assert.True(
            result.ErrorCode is ClassifyErrors.Unexpected or ClassifyErrors.ResourceLimit,
            result.ErrorCode);
    }

    [Fact]
    public async Task Run_result_counts_partition_exactly()
    {
        var seeded = await SeedPreviewAsync("counts shop");
        var result = await services.Run.HandleAsync(
            new ClassifyApplyRunRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.PreviewId,
                "apply-counts-" + Guid.NewGuid().ToString("N")[..8]),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var v = result.Value!;
        Assert.Equal(
            v.Items.Count,
            v.AppliedCount + v.AlreadyAppliedCount + v.RejectedCount + v.FailedCount + v.UnresolvedCount);
    }

    [Fact]
    public async Task Lock_is_exclusive_per_apply_id()
    {
        var first = await services.ApplyLock.TryAcquireAsync("lock-id-1", CancellationToken.None);
        Assert.NotNull(first);
        var second = await services.ApplyLock.TryAcquireAsync("lock-id-1", CancellationToken.None);
        Assert.Null(second);
        await first!.DisposeAsync();
        var third = await services.ApplyLock.TryAcquireAsync("lock-id-1", CancellationToken.None);
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    [Fact]
    public async Task Different_apply_ids_take_independent_locks()
    {
        var a = await services.ApplyLock.TryAcquireAsync("lock-a", CancellationToken.None);
        var b = await services.ApplyLock.TryAcquireAsync("lock-b", CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
        await a!.DisposeAsync();
        await b!.DisposeAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record SeededPreview(
        string PreviewId,
        string EvaluationId,
        IReadOnlyList<string> TransactionIds);

    private async Task<SeededPreview> SeedPreviewAsync(string description)
    {
        var category = await CreateCategoryAsync("Cat");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description, false);
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
        return new SeededPreview(preview.Value!.PreviewId, evaluated.Value.EvaluationId, [tx.TransactionId]);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description, bool broadApply)
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
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
                broadApply,
                "saga activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description, string? ruleId = null)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
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
                "saga draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task<string> WriteBoundCorpusAsync(
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
        return path;
    }

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

    private async Task AssignCategoryAsync(string transactionId, string categoryId, string reason) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, reason),
            NextKey(),
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task VoidTransactionAsync(string transactionId)
    {
        var descriptor = registry.Find("ledger.transaction.void");
        if (descriptor is null) return;
        var request = new RequestEnvelope(
            "1.0", actor,
            JsonSerializer.SerializeToElement(
                new VoidTransactionInput(transactionId, "saga-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Saga Bank " + unique, "S-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "saga:" + Guid.NewGuid().ToString("N")[..8], null, null)),
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

    private string NextKey() => $"apply-saga-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
