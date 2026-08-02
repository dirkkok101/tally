using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Apply.Run;

/// <summary>
/// classify.apply.run vertical slice
/// (FR-CLASSIFY-APPLY-EXECUTION / NFR-CLASSIFY-APPLY-RECOVERY / TASK-CLASSIFY-RULEBOOK-APPLY-RUN-SAGA).
/// One owner-only per-run OS lock; all-item apply_preflight before any Ledger mutation;
/// durable frozen per-item intent before each Ledger call outside CLASSIFY transactions;
/// terminal results before advancing; resume only planned/unresolved with frozen keys.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RunClassificationApplyCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationApplyPreviewStore previewStore;
    private readonly ClassificationApplyRunStore runStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly ClassificationApplyLock applyLock;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public RunClassificationApplyCommand(
        ClassifyStateStore stateStore,
        ClassificationApplyPreviewStore previewStore,
        ClassificationApplyRunStore runStore,
        ClassificationEvaluationStore evaluationStore,
        RuleSetStore ruleSetStore,
        ClassificationApplyLock applyLock,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(previewStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(applyLock);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.previewStore = previewStore;
        this.runStore = runStore;
        this.evaluationStore = evaluationStore;
        this.ruleSetStore = ruleSetStore;
        this.applyLock = applyLock;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyApplyRunResult>> HandleAsync(
        ClassifyApplyRunRequest input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Unexpected);
        }

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.PreviewId) || string.IsNullOrWhiteSpace(input.ApplyId))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var previewId = input.PreviewId.Trim();
        var applyId = input.ApplyId.Trim();

        var fingerprintElement = ClassifyContractMapper.ToApplyRunFingerprintElement(
            ClassifyOperationIds.ContractVersion, previewId, applyId);
        var operationFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.ApplyRun,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        var probed = await TryProbeOperationIdempotencyAsync(
            idempotencyKey, operationFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        await using var heldLock = await applyLock.TryAcquireAsync(applyId, cancellationToken);
        if (heldLock is null)
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Conflict);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs));
        var ct = timeout.Token;

        try
        {
            // Existing run identity: conflict on semantic drift; resume or return terminal results.
            ClassifyApplyRunRow? existingRun;
            IReadOnlyList<ClassifyApplyItemRow>? existingItems;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                existingRun = await runStore.GetRunAsync(connection, null, applyId, ct);
                existingItems = existingRun is null
                    ? null
                    : await runStore.ListItemsAsync(connection, null, applyId, ct);
            }

            if (existingRun is not null)
            {
                if (!string.Equals(existingRun.Actor, actorText, StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Conflict);
                }

                // Load preview to recompute semantic fingerprint for conflict detection.
                ClassifyApplyPreviewRow? previewForConflict;
                IReadOnlyList<ClassifyApplyPreviewItemRow> previewItemsForConflict;
                await using (var connection = await stateStore.OpenMigratedAsync(ct))
                {
                    previewForConflict = await previewStore.GetPreviewAsync(connection, null, previewId, ct);
                    previewItemsForConflict = previewForConflict is null
                        ? Array.Empty<ClassifyApplyPreviewItemRow>()
                        : await previewStore.ListItemsAsync(connection, null, previewId, ct);
                }

                if (previewForConflict is null)
                {
                    // Apply identity exists under a different preview path — still conflict if fingerprints differ.
                    if (!string.Equals(existingRun.PreviewId, previewId, StringComparison.Ordinal))
                    {
                        return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Conflict);
                    }

                    return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.PreviewNotFound);
                }

                var expectedFingerprint = ClassifyContractMapper.ComputeApplyRunRequestFingerprint(
                    applyId, previewForConflict, previewItemsForConflict);
                if (!string.Equals(existingRun.RequestFingerprint, expectedFingerprint, StringComparison.Ordinal)
                    || !string.Equals(existingRun.PreviewId, previewId, StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Conflict);
                }

                if (string.Equals(
                        existingRun.LifecycleState,
                        ApplyReplayPolicy.RunLifecycleCompleted,
                        StringComparison.Ordinal)
                    || string.Equals(
                        existingRun.LifecycleState,
                        ApplyReplayPolicy.RunLifecycleFailed,
                        StringComparison.Ordinal))
                {
                    var terminal = ClassifyContractMapper.ToApplyRunResult(
                        applyId, existingRun.PreviewId, existingItems ?? Array.Empty<ClassifyApplyItemRow>());
                    await CommitOperationIdempotencyIfMissingAsync(
                        idempotencyKey, operationFingerprint, terminal, actorText, ct);
                    return CommandResult<ClassifyApplyRunResult>.Success(terminal);
                }

                if (!string.Equals(
                        existingRun.LifecycleState,
                        ApplyReplayPolicy.RunLifecycleRunning,
                        StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Lifecycle);
                }

                // Resume running run: process remaining frontier only.
                var resumeResult = await ProcessItemsAsync(
                    applyId,
                    existingRun.PreviewId,
                    existingItems ?? Array.Empty<ClassifyApplyItemRow>(),
                    actor,
                    actorText,
                    isResume: true,
                    ct);
                if (resumeResult.IsSuccess && resumeResult.Value is not null)
                {
                    await CommitOperationIdempotencyIfMissingAsync(
                        idempotencyKey, operationFingerprint, resumeResult.Value, actorText, ct);
                }

                return resumeResult;
            }

            // ── First start: load preview, all-item preflight, freeze intent ──
            ClassifyApplyPreviewRow preview;
            IReadOnlyList<ClassifyApplyPreviewItemRow> previewItems;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                var loaded = await previewStore.GetPreviewAsync(connection, null, previewId, ct);
                if (loaded is null)
                {
                    return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.PreviewNotFound);
                }

                preview = loaded;
                previewItems = await previewStore.ListItemsAsync(connection, null, previewId, ct);
            }

            if (previewItems.Count == 0 || previewItems.Count != preview.SelectedCount)
            {
                return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Integrity);
            }

            if (!TryParseUtc(preview.ExpiresAt, out var expiresAt)
                || timeProvider.GetUtcNow() >= expiresAt)
            {
                return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Stale);
            }

            var requestFingerprint = ClassifyContractMapper.ComputeApplyRunRequestFingerprint(
                applyId, preview, previewItems);

            // All-item preflight BEFORE any apply_run / apply_item durability and before Ledger mutation.
            var preflightError = await RevalidateAllItemsAsync(preview, previewItems, actor, ct);
            if (preflightError is not null)
            {
                return CommandResult<ClassifyApplyRunResult>.Failure(preflightError);
            }

            var now = timeProvider.GetUtcNow();
            var startedAt = ClassifyContractMapper.FormatUtc(now);
            var plannedItems = previewItems
                .OrderBy(i => i.Ordinal)
                .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                .Select(item => ClassifyContractMapper.ToPlannedApplyItemRow(applyId, item))
                .ToArray();

            var runRow = ClassifyContractMapper.ToApplyRunRow(
                applyId,
                previewId,
                requestFingerprint,
                ApplyReplayPolicy.RunLifecycleRunning,
                plannedItems.Length,
                actorText,
                startedAt);

            await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    // Race: another concurrent lock holder may have inserted (should not with OS lock).
                    var raced = await runStore.GetRunAsync(connection, transaction, applyId, writeCt);
                    if (raced is not null)
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Conflict}: apply run already exists under lock.");
                    }

                    await runStore.InsertRunAsync(connection, transaction, runRow, writeCt);
                    await runStore.InsertItemsAsync(connection, transaction, plannedItems, writeCt);
                    return true;
                },
                ct);

            var processResult = await ProcessItemsAsync(
                applyId,
                previewId,
                plannedItems,
                actor,
                actorText,
                isResume: false,
                ct);

            if (processResult.IsSuccess && processResult.Value is not null)
            {
                await CommitOperationIdempotencyIfMissingAsync(
                    idempotencyKey, operationFingerprint, processResult.Value, actorText, ct);
            }

            return processResult;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Conflict, StringComparison.Ordinal))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Conflict);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("apply", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<string?> RevalidateAllItemsAsync(
        ClassifyApplyPreviewRow preview,
        IReadOnlyList<ClassifyApplyPreviewItemRow> previewItems,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        ClassifyEvaluationRunRow? retainedEvaluation;
        ClassifyActiveRuleSetPointer? activeRuleSet;
        IReadOnlyList<string> activeRuleVersionIds;
        IReadOnlyList<ClassifyRuleLifecycleEventRow> exactRuleEvents = Array.Empty<ClassifyRuleLifecycleEventRow>();
        IReadOnlyList<ClassifyRuleLifecycleEventRow> ruleSetEvents = Array.Empty<ClassifyRuleLifecycleEventRow>();
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            retainedEvaluation = await evaluationStore.GetRunAsync(
                connection, null, preview.EvaluationId, cancellationToken);
            activeRuleSet = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            activeRuleVersionIds = activeRuleSet is null
                ? Array.Empty<string>()
                : await ruleSetStore.ListMemberRuleVersionIdsAsync(
                    connection, null, activeRuleSet.RuleSetVersionId, cancellationToken);

            if (string.Equals(preview.SelectionMode, ClassifyContractMapper.SelectionModeExactRule, StringComparison.Ordinal))
            {
                var exactRuleIds = previewItems
                    .Select(i => i.RuleVersionId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (exactRuleIds.Length == 1)
                {
                    exactRuleEvents = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
                        connection, null, exactRuleIds[0], cancellationToken);
                }

                if (activeRuleSet is not null)
                {
                    ruleSetEvents = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
                        connection, null, activeRuleSet.RuleSetVersionId, cancellationToken);
                }
            }
        }

        if (retainedEvaluation is null
            || activeRuleSet is null
            || !string.Equals(
                ClassifyContractMapper.ToRetainedEvaluationFingerprint(retainedEvaluation).CanonicalHash,
                preview.EvaluationFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(activeRuleSet.RuleSetVersionId, retainedEvaluation.RuleSetVersionId, StringComparison.Ordinal)
            || !string.Equals(retainedEvaluation.NormalizationVersion, NormalizationDescriptor.V1.Version, StringComparison.Ordinal))
        {
            return ClassifyErrors.Stale;
        }

        var activeRules = activeRuleVersionIds.ToHashSet(StringComparer.Ordinal);
        var selectedRuleIds = previewItems
            .Select(i => i.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(preview.SelectionMode, ClassifyContractMapper.SelectionModeExplicitCorrections, StringComparison.Ordinal)
            && (selectedRuleIds.Length == 0 || selectedRuleIds.Any(id => !activeRules.Contains(id))))
        {
            return ClassifyErrors.Stale;
        }

        var broadAuthority = ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(exactRuleEvents)
            || ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(ruleSetEvents);
        var currentRuleAuthorityFingerprint = ClassifyContractMapper.ComputeFrozenRuleAuthorityFingerprint(
            preview.SelectionMode, selectedRuleIds, broadAuthority);
        if (currentRuleAuthorityFingerprint is null
            || !string.Equals(currentRuleAuthorityFingerprint, preview.RuleAuthorityFingerprint, StringComparison.Ordinal)
            || !string.Equals(
                ClassifyContractMapper.ComputeFrozenTargetCategoryFingerprint(previewItems),
                preview.TargetCategoryFingerprint,
                StringComparison.Ordinal))
        {
            return ClassifyErrors.Stale;
        }

        var transactionIds = previewItems
            .Select(i => i.TransactionId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (transactionIds.Length == 0
            || transactionIds.Length > ClassificationProjectionVersions.MaxApplyPreflightIds)
        {
            return ClassifyErrors.SelectionInvalid;
        }

        var preflight = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            preview.LedgerContractVersion,
            actor,
            cancellationToken,
            transactionIds: transactionIds);

        if (!preflight.IsSuccess || preflight.Value is null)
        {
            return ClassifyContractMapper.MapLedgerCategoryListError(preflight.Error);
        }

        var page = preflight.Value;
        if (string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint)
            || !string.Equals(
                page.ProjectionVersion,
                ClassificationProjectionVersions.ClassificationV1,
                StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        // Projection / generation drift vs frozen preview fails closed.
        if (!string.Equals(page.StoreGenerationFingerprint, preview.StoreGenerationFingerprint, StringComparison.Ordinal)
            || !string.Equals(page.LedgerContractVersion, preview.LedgerContractVersion, StringComparison.Ordinal)
            || !string.Equals(page.ProjectionVersion, preview.ProjectionVersion, StringComparison.Ordinal))
        {
            return ClassifyErrors.Stale;
        }

        var currentCategoryLifecycleFingerprint = !string.IsNullOrWhiteSpace(page.CategoryIdentityLifecycleFingerprint)
            ? page.CategoryIdentityLifecycleFingerprint
            : EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                (page.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                    .Select(c => (c.CategoryId, c.LifecycleState)));
        if (!string.Equals(
                currentCategoryLifecycleFingerprint,
                preview.CategoryLifecycleFingerprint,
                StringComparison.Ordinal)
            || !TryParseUtc(preview.CreatedAt, out var previewCreatedAt))
        {
            return ClassifyErrors.Stale;
        }

        foreach (var targetCategoryId in previewItems
                     .Select(i => i.CategoryId)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = await ledger.GetBudgetCategoryAsync(
                targetCategoryId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken,
                includeHistory: true);
            if (!category.IsSuccess
                || category.Value is null
                || category.Value.Status != CategoryStatus.Active
                || category.Value.LifecycleHistory.Any(h =>
                    TryParseUtc(h.OccurredAt, out var occurredAt) && occurredAt > previewCreatedAt))
            {
                return ClassifyErrors.Stale;
            }
        }

        var itemsByTx = (page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var missing = new HashSet<string>(
            page.MissingTransactionIds ?? Array.Empty<string>(),
            StringComparer.Ordinal);

        var activeCategoryIds = (page.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
            .Where(c => string.Equals(c.LifecycleState, "active", StringComparison.Ordinal))
            .Select(c => c.CategoryId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var frozen in previewItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsByTx.TryGetValue(frozen.TransactionId, out var live);
            var isMissing = missing.Contains(frozen.TransactionId) || live is null;
            if (!ClassifyContractMapper.TryMatchFrozenPreflight(
                    frozen, live, isMissing, activeCategoryIds, out var matchError))
            {
                return matchError ?? ClassifyErrors.Stale;
            }
        }

        return null;
    }

    private async Task<CommandResult<ClassifyApplyRunResult>> ProcessItemsAsync(
        string applyId,
        string previewId,
        IReadOnlyList<ClassifyApplyItemRow> seedItems,
        SafeActor actor,
        string actorText,
        bool isResume,
        CancellationToken cancellationToken)
    {
        // Always reload durable items so resume uses frozen columns only.
        IReadOnlyList<ClassifyApplyItemRow> items;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            items = await runStore.ListItemsAsync(connection, null, applyId, cancellationToken);
        }

        if (items.Count == 0)
        {
            items = seedItems;
        }

        var frontier = ApplyReplayPolicy.SelectReplayFrontier(
            items,
            i => i.Ordinal,
            i => i.TransactionId,
            i => i.ItemState);

        foreach (var item in frontier)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ApplyReplayPolicy.MayCallLedger(item.ItemState))
            {
                continue;
            }

            // Ledger call OUTSIDE any CLASSIFY SQLite transaction.
            var ledgerOutcome = await InvokeLedgerAsync(item, actor, cancellationToken);

            var (nextState, _) = ApplyReplayPolicy.MapLedgerOutcome(
                ledgerOutcome.Success,
                ledgerOutcome.ErrorCode,
                ledgerOutcome.CategoryAlreadyMatchesTarget);

            var resultFingerprint = ClassifyContractMapper.ComputeLedgerResultFingerprint(
                nextState,
                ledgerOutcome.AllocationEventId,
                ledgerOutcome.ErrorCode);

            var completed = await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    var ok = await runStore.TryCompleteItemAsync(
                        connection,
                        transaction,
                        applyId,
                        item.Ordinal,
                        item.ItemState,
                        nextState,
                        resultFingerprint,
                        ledgerOutcome.AllocationEventId,
                        item.ExpectedActiveAllocationId,
                        ledgerOutcome.ErrorCode,
                        writeCt);
                    if (!ok)
                    {
                        // Concurrent transition — reload later.
                        return false;
                    }

                    var latest = await runStore.ListItemsAsync(connection, transaction, applyId, writeCt);
                    var frontierCount = ApplyReplayPolicy.ComputeUnresolvedFrontier(
                        latest.Select(i => i.ItemState));
                    await runStore.UpdateRunFrontierAsync(
                        connection, transaction, applyId, frontierCount, writeCt);
                    return true;
                },
                cancellationToken);

            if (!completed)
            {
                // Another writer advanced this item; continue with reloaded frontier.
            }
        }

        // Reload and complete run if frontier empty.
        IReadOnlyList<ClassifyApplyItemRow> finalItems;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            finalItems = await runStore.ListItemsAsync(connection, null, applyId, cancellationToken);
        }

        var remaining = ApplyReplayPolicy.ComputeUnresolvedFrontier(finalItems.Select(i => i.ItemState));
        if (remaining == 0)
        {
            var completedAt = ClassifyContractMapper.FormatUtc(timeProvider.GetUtcNow());
            await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    await runStore.TryCompleteRunAsync(
                        connection,
                        transaction,
                        applyId,
                        ApplyReplayPolicy.RunLifecycleCompleted,
                        completedAt,
                        writeCt);
                    return true;
                },
                cancellationToken);
        }

        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            finalItems = await runStore.ListItemsAsync(connection, null, applyId, cancellationToken);
        }

        var result = ClassifyContractMapper.ToApplyRunResult(applyId, previewId, finalItems);
        _ = isResume;
        _ = actorText;
        return CommandResult<ClassifyApplyRunResult>.Success(result);
    }

    private async Task<LedgerItemOutcome> InvokeLedgerAsync(
        ClassifyApplyItemRow item,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(
                    item.LedgerOperationId,
                    ApplyReplayPolicy.LedgerOperationAssign,
                    StringComparison.Ordinal))
            {
                var input = ClassifyContractMapper.ToAssignInput(item);
                var result = await ledger.AssignCategoryAsync(
                    input,
                    item.ExpectedTransactionRevision is not null
                        ? ActualsContractVersions.Current
                        : ActualsContractVersions.Current,
                    actor,
                    item.LedgerIdempotencyKey,
                    cancellationToken);

                if (result.IsSuccess && result.Value is not null)
                {
                    return new LedgerItemOutcome(true, null, result.Value.AllocationEventId, false);
                }

                var code = result.Error?.Code;
                var already = ApplyReplayPolicy.IsAlreadyAppliedError(code);
                return new LedgerItemOutcome(false, code, null, already);
            }

            if (string.Equals(
                    item.LedgerOperationId,
                    ApplyReplayPolicy.LedgerOperationCorrect,
                    StringComparison.Ordinal))
            {
                var input = ClassifyContractMapper.ToCorrectInput(item);
                var result = await ledger.CorrectCategoryAsync(
                    input,
                    ActualsContractVersions.Current,
                    actor,
                    item.LedgerIdempotencyKey,
                    cancellationToken);

                if (result.IsSuccess && result.Value is not null)
                {
                    return new LedgerItemOutcome(true, null, result.Value.AllocationEventId, false);
                }

                var code = result.Error?.Code;
                var already = ApplyReplayPolicy.IsAlreadyAppliedError(code);
                return new LedgerItemOutcome(false, code, null, already);
            }

            return new LedgerItemOutcome(false, ClassifyErrors.Integrity, null, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Unexpected transport/runtime failure → failed (not applied).
            return new LedgerItemOutcome(false, ClassifyErrors.LedgerUnavailable, null, false);
        }
    }

    private async Task<CommandResult<ClassifyApplyRunResult>?> TryProbeOperationIdempotencyAsync(
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        await using var transaction = stateStore.BeginImmediate(connection);
        try
        {
            var existing = await idempotencyStore.FindAsync(
                connection, transaction, idempotencyKey, cancellationToken);
            var lookup = idempotencyStore.Resolve(
                existing,
                ClassifyOperationIds.ApplyRun,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private async Task CommitOperationIdempotencyIfMissingAsync(
        string idempotencyKey,
        string requestFingerprint,
        ClassifyApplyRunResult result,
        string actorText,
        CancellationToken cancellationToken)
    {
        _ = actorText;
        var createdAt = ClassifyContractMapper.FormatUtc(timeProvider.GetUtcNow());
        await stateStore.ExecuteWriteAsync(
            async (connection, transaction, writeCt) =>
            {
                var existing = await idempotencyStore.FindAsync(
                    connection, transaction, idempotencyKey, writeCt);
                if (existing is not null)
                {
                    return true;
                }

                await idempotencyStore.CommitAsync(
                    connection,
                    transaction,
                    new ClassifyOperationIdempotencyRow(
                        idempotencyKey,
                        ClassifyOperationIds.ApplyRun,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint,
                        SerializeResult(result),
                        createdAt),
                    writeCt);
                return true;
            },
            cancellationToken);
    }

    private static CommandResult<ClassifyApplyRunResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyApplyRunResult);
            return result is null
                ? CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyApplyRunResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyApplyRunResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyApplyRunResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyApplyRunResult);

    private static bool TryParseUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    private sealed record LedgerItemOutcome(
        bool Success,
        string? ErrorCode,
        string? AllocationEventId,
        bool CategoryAlreadyMatchesTarget);
}
