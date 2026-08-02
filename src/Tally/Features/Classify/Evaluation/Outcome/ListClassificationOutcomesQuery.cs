using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Discovery;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Evaluation.Outcome;

/// <summary>
/// classify.outcome.list vertical slice
/// (FR-CLASSIFY-OUTCOME-DISCOVERY / FR-CLASSIFY-OUTCOME-INVALIDATION / FR-CLASSIFY-ELIGIBLE-PROJECTION /
/// DD-CLASSIFY-PAGINATED-DISCOVERY / bd-vg33).
/// Validates retained lifecycle, active rule membership, category lifecycle, Ledger generation/projection,
/// cursor bindings, and partition accounting before constructing any page. Never mutates CLASSIFY or Ledger.
/// Never calls outcome.get; never persists a second index or private payload.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ListClassificationOutcomesQuery
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationOutcomeDiscoveryStore discoveryStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ListClassificationOutcomesQuery(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationOutcomeDiscoveryStore discoveryStore,
        ClassificationRuleStore ruleStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(discoveryStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.discoveryStore = discoveryStore;
        this.ruleStore = ruleStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyOutcomeListResult>> HandleAsync(
        ClassifyOutcomeListRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyOperatorErgonomicsContracts.TryValidate(input, out var validationError)
            || validationError is not null)
        {
            // TryValidate covers version + evaluationId + pageSize.
            return CommandResult<ClassifyOutcomeListResult>.Failure(
                validationError ?? ClassifyErrors.InvalidInput);
        }

        var evaluationId = input.EvaluationId.Trim();
        var pageSize = input.PageSize;
        var outcomeType = input.OutcomeKind is null
            ? null
            : ClassifyContractMapper.FormatStoredOutcomeType(input.OutcomeKind.Value);
        var suggestedCategoryId = string.IsNullOrWhiteSpace(input.SuggestedCategoryId)
            ? null
            : input.SuggestedCategoryId.Trim();
        var contributingRuleVersionId = string.IsNullOrWhiteSpace(input.ContributingRuleVersionId)
            ? null
            : input.ContributingRuleVersionId.Trim();
        var transactionId = string.IsNullOrWhiteSpace(input.TransactionId)
            ? null
            : input.TransactionId.Trim();
        var staleFilter = input.StaleState ?? ClassifyOutcomeStaleFilter.Any;

        ClassifyEvaluationRunRow? run;
        IReadOnlyList<ClassifyOutcomeRow> allOutcomes;
        IReadOnlyList<ClassifyOutcomeRow> staticFiltered;
        string? currentRuleSetVersionId;
        int overallCount;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            run = await evaluationStore.GetRunAsync(connection, null, evaluationId, cancellationToken);
            if (run is null)
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.EvaluationNotFound);
            }

            if (!string.Equals(run.LifecycleState, ClassifyContractMapper.EvaluationLifecycleCompleted, StringComparison.Ordinal))
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Lifecycle);
            }

            overallCount = await discoveryStore.CountOutcomesForEvaluationAsync(
                connection, null, evaluationId, cancellationToken);
            if (overallCount != run.InputCount
                || run.SuggestionCount + run.NoSuggestionCount + run.ConflictCount + run.StaleCount != run.InputCount)
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Integrity);
            }

            allOutcomes = await evaluationStore.ListOutcomesAsync(
                connection, null, evaluationId, cancellationToken);
            if (allOutcomes.Count != overallCount)
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Integrity);
            }

            staticFiltered = await discoveryStore.ListFilteredOutcomesAsync(
                connection,
                null,
                evaluationId,
                outcomeType,
                suggestedCategoryId,
                contributingRuleVersionId,
                transactionId,
                cancellationToken);

            var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            if (active is null)
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.ActiveRuleSetNotFound);
            }

            currentRuleSetVersionId = active.RuleSetVersionId;
        }

        if (!TryParseExpiresAt(run.SnapshotExpiresAt, out var retainedExpiresAt)
            || !TryParseUtc(run.CreatedAt, out var evaluationCreatedAt))
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Integrity);
        }

        var now = timeProvider.GetUtcNow();
        if (now >= retainedExpiresAt)
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Stale);
        }

        var publicState = await ReadCurrentPublicStateAsync(
            actor,
            run,
            allOutcomes,
            evaluationCreatedAt,
            cancellationToken);
        if (publicState.LedgerError is not null)
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(publicState.LedgerError);
        }

        // Generation / projection compatibility before any page construction.
        if (!string.Equals(publicState.ProjectionVersion, run.ProjectionVersion, StringComparison.Ordinal)
            || !string.Equals(publicState.LedgerContractVersion, run.LedgerContractVersion, StringComparison.Ordinal))
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.LedgerIncompatible);
        }

        if (!string.Equals(
                publicState.StoreGenerationFingerprint,
                run.StoreGenerationFingerprint,
                StringComparison.Ordinal))
        {
            return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Stale);
        }

        var retainedFingerprint = ClassifyContractMapper.ToRetainedEvaluationFingerprint(run);
        var evaluationFingerprint = retainedFingerprint.CanonicalHash;
        var resultFingerprint = ClassificationOutcomeDiscoveryStore.ComputeResultFingerprint(allOutcomes);
        // Cursor/snapshot binding uses current durable active rule-set authority (not retained
        // evaluation membership alone). An authority change invalidates continuations.
        var ruleSetFingerprint = ClassifyContractMapper.RuleSetFingerprint(currentRuleSetVersionId!);
        var categoryLifecycleFingerprint = publicState.CategoryLifecycleFingerprint!;
        var ledgerGeneration = publicState.StoreGenerationFingerprint!;

        // Cursor validation before mapping rows.
        ClassifyCursorCodec.OutcomeKeysetPosition? resume = null;
        if (!string.IsNullOrWhiteSpace(input.Continuation))
        {
            var filterFp = ClassifyContractMapper.OutcomeListFilterFingerprint(
                evaluationId,
                input.OutcomeKind,
                suggestedCategoryId,
                contributingRuleVersionId,
                input.StaleState,
                transactionId);
            var binding = new ClassifyCursorCodec.OutcomeSnapshotBinding(
                EvaluationId: evaluationId,
                FilterFingerprint: filterFp,
                PageSize: pageSize,
                EvaluationFingerprint: evaluationFingerprint,
                ResultFingerprint: resultFingerprint,
                RuleSetFingerprint: ruleSetFingerprint,
                CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
                LedgerGeneration: ledgerGeneration,
                ExpiresAtUtc: retainedExpiresAt);

            if (!ClassifyCursorCodec.TryDecodeOutcome(
                    input.Continuation,
                    binding,
                    now,
                    out resume,
                    out var cursorError))
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(
                    cursorError ?? ClassifyErrors.CursorInvalid);
            }
        }

        // Load evidence for static-filtered candidates (staleness needs item fingerprints only;
        // evidence is required for mapped page items).
        IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> evidenceByOutcome;
        Dictionary<string, ClassifyRuleVersionRow> immutableRules;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            evidenceByOutcome = await discoveryStore.ListEvidenceForOutcomesAsync(
                connection,
                null,
                staticFiltered.Select(o => o.OutcomeId).ToArray(),
                cancellationToken);

            immutableRules = new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal);
            foreach (var evidence in evidenceByOutcome.Values)
            {
                foreach (var ruleId in ClassifyContractMapper.ToContributingRuleVersionIds(evidence))
                {
                    if (immutableRules.ContainsKey(ruleId))
                    {
                        continue;
                    }

                    var version = await ruleStore.GetRuleVersionAsync(connection, null, ruleId, cancellationToken);
                    if (version is null)
                    {
                        return CommandResult<ClassifyOutcomeListResult>.Failure(
                            ClassifyContractMapper.EvidenceUnavailable);
                    }

                    immutableRules[ruleId] = version;
                }
            }
        }

        // Compute staleness for every static-filtered outcome; fail closed on archived suggestion category.
        var enriched = new List<EnrichedOutcome>(staticFiltered.Count);
        foreach (var outcome in staticFiltered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassificationOutcomeKind kind;
            try
            {
                kind = ClassifyContractMapper.ParseStoredOutcomeType(outcome.OutcomeType);
            }
            catch (ArgumentOutOfRangeException)
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(
                    ClassifyContractMapper.EvidenceUnavailable);
            }

            publicState.ItemLifecycleByTransaction.TryGetValue(outcome.TransactionId, out var currentItemLife);
            var transactionFound = publicState.ItemLifecycleByTransaction.ContainsKey(outcome.TransactionId);

            string? suggestedLifecycle = null;
            var reactivated = false;
            string? displayName = null;
            if (kind == ClassificationOutcomeKind.Suggestion
                && !string.IsNullOrWhiteSpace(outcome.CategoryId))
            {
                if (!publicState.CategoryById.TryGetValue(outcome.CategoryId, out var catInfo))
                {
                    // Missing identity — fail closed as archived-category / lifecycle gap.
                    return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Stale);
                }

                displayName = catInfo.DisplayName;
                suggestedLifecycle = catInfo.LifecycleState;
                reactivated = catInfo.ReactivatedAfterEvaluation;
                if (!string.Equals(suggestedLifecycle, "active", StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyOutcomeListResult>.Failure(ClassifyErrors.Stale);
                }
            }

            var staleness = ClassificationStalenessPolicy.Evaluate(new ClassificationStalenessPolicy.Input(
                RetainedEvaluation: retainedFingerprint,
                RetainedItemLifecycleFingerprint: outcome.ItemLifecycleFingerprint,
                SuggestedCategoryId: kind == ClassificationOutcomeKind.Suggestion ? outcome.CategoryId : null,
                CurrentStoreGenerationFingerprint: publicState.StoreGenerationFingerprint,
                CurrentLedgerContractVersion: publicState.LedgerContractVersion,
                CurrentProjectionVersion: publicState.ProjectionVersion,
                CurrentCategoryLifecycleFingerprint: publicState.CategoryLifecycleFingerprint,
                CurrentNormalizationVersion: NormalizationDescriptor.V1.Version,
                CurrentRuleSetVersionId: currentRuleSetVersionId,
                CurrentOrderedItemsFingerprint: publicState.OrderedItemsFingerprint,
                CurrentItemLifecycleFingerprint: currentItemLife,
                TransactionFoundInLedger: transactionFound,
                SuggestedCategoryLifecycleState: suggestedLifecycle,
                SuggestedCategoryReactivatedAfterEvaluation: reactivated,
                NowUtc: now,
                RetainedSnapshotExpiresAt: retainedExpiresAt));

            // Stale-state AND filter.
            var isStale = staleness.IsStale || kind == ClassificationOutcomeKind.Stale;
            if (staleFilter == ClassifyOutcomeStaleFilter.Fresh && isStale)
            {
                continue;
            }

            if (staleFilter == ClassifyOutcomeStaleFilter.Stale && !isStale)
            {
                continue;
            }

            if (!evidenceByOutcome.TryGetValue(outcome.OutcomeId, out var evidence))
            {
                evidence = Array.Empty<ClassifyMatchEvidenceRow>();
            }

            if (!ClassifyContractMapper.TryMapOutcomeListItem(
                    outcome,
                    evidence,
                    isStale,
                    staleness.ChangedDimensions,
                    displayName,
                    immutableRules,
                    out var item,
                    out var mapError))
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(
                    mapError ?? ClassifyContractMapper.EvidenceUnavailable);
            }

            enriched.Add(new EnrichedOutcome(outcome, item));
        }

        // Deterministic order (already ordinal/tx from SQL; re-assert after stale filter).
        enriched.Sort(static (a, b) =>
        {
            var cmp = a.Outcome.Ordinal.CompareTo(b.Outcome.Ordinal);
            return cmp != 0
                ? cmp
                : string.CompareOrdinal(a.Outcome.TransactionId, b.Outcome.TransactionId);
        });

        var filteredCount = enriched.Count;

        // Apply keyset resume (after last ordinal/transactionId).
        IEnumerable<EnrichedOutcome> window = enriched;
        if (resume is not null)
        {
            window = enriched.Where(e =>
                e.Outcome.Ordinal > resume.LastOrdinal
                || (e.Outcome.Ordinal == resume.LastOrdinal
                    && string.CompareOrdinal(e.Outcome.TransactionId, resume.LastTransactionId) > 0));
        }

        var pageMaterialized = window.ToArray();
        var pageRows = pageMaterialized.Take(pageSize).ToArray();
        var items = pageRows.Select(r => r.Item).ToArray();
        var hasMore = pageMaterialized.Length > pageSize;

        string? continuation = null;
        if (hasMore && pageRows.Length > 0)
        {
            var last = pageRows[^1].Outcome;
            var filterFp = ClassifyContractMapper.OutcomeListFilterFingerprint(
                evaluationId,
                input.OutcomeKind,
                suggestedCategoryId,
                contributingRuleVersionId,
                input.StaleState,
                transactionId);
            var binding = new ClassifyCursorCodec.OutcomeSnapshotBinding(
                EvaluationId: evaluationId,
                FilterFingerprint: filterFp,
                PageSize: pageSize,
                EvaluationFingerprint: evaluationFingerprint,
                ResultFingerprint: resultFingerprint,
                RuleSetFingerprint: ruleSetFingerprint,
                CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
                LedgerGeneration: ledgerGeneration,
                ExpiresAtUtc: retainedExpiresAt);
            if (!ClassifyCursorCodec.TryEncodeOutcome(
                    binding,
                    new ClassifyCursorCodec.OutcomeKeysetPosition(last.Ordinal, last.TransactionId),
                    out continuation,
                    out var encodeError))
            {
                return CommandResult<ClassifyOutcomeListResult>.Failure(
                    encodeError ?? ClassifyErrors.CursorInvalid);
            }
        }

        var result = ClassifyContractMapper.ToOutcomeListResult(
            evaluationId,
            evaluationFingerprint,
            resultFingerprint,
            ruleSetFingerprint,
            categoryLifecycleFingerprint,
            ledgerGeneration,
            overallCount,
            filteredCount,
            items,
            continuation);

        return CommandResult<ClassifyOutcomeListResult>.Success(result);
    }

    private async Task<CurrentPublicState> ReadCurrentPublicStateAsync(
        SafeActor actor,
        ClassifyEvaluationRunRow retainedRun,
        IReadOnlyList<ClassifyOutcomeRow> allOutcomes,
        DateTimeOffset evaluationCreatedAt,
        CancellationToken cancellationToken)
    {
        var evaluationProjection = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            retainedRun.LedgerContractVersion,
            actor,
            cancellationToken);
        if (!evaluationProjection.IsSuccess || evaluationProjection.Value is null)
        {
            return CurrentPublicState.Failed(
                ClassifyContractMapper.MapLedgerCategoryListError(evaluationProjection.Error));
        }

        var evalPage = evaluationProjection.Value;
        if (string.IsNullOrWhiteSpace(evalPage.ProjectionVersion)
            || string.IsNullOrWhiteSpace(evalPage.StoreGenerationFingerprint)
            || string.IsNullOrWhiteSpace(evalPage.LedgerContractVersion))
        {
            return CurrentPublicState.Failed(ClassifyErrors.LedgerIncompatible);
        }

        var items = evalPage.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
        var itemLifecycleByTx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            itemLifecycleByTx[item.TransactionId] =
                ClassificationEvaluationInputLoader.ComputeItemLifecycleFingerprint(item);
        }

        var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
            items
                .OrderBy(i => i.Ordinal)
                .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                .Select(i => (
                    i.Ordinal,
                    i.TransactionId,
                    itemLifecycleByTx[i.TransactionId])));

        var categoryLifecycleFingerprint = !string.IsNullOrWhiteSpace(evalPage.CategoryIdentityLifecycleFingerprint)
            ? evalPage.CategoryIdentityLifecycleFingerprint
            : EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                (evalPage.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                    .Select(c => (c.CategoryId, c.LifecycleState)));

        // Category display + lifecycle for every distinct suggested category on retained outcomes.
        var categoryIds = allOutcomes
            .Where(o => !string.IsNullOrWhiteSpace(o.CategoryId))
            .Select(o => o.CategoryId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var categoryById = new Dictionary<string, CategoryInfo>(StringComparer.Ordinal);
        foreach (var categoryId in categoryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await ledger.GetBudgetCategoryAsync(
                categoryId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken,
                includeHistory: true);
            if (!detail.IsSuccess || detail.Value is null)
            {
                // Absent — leave out; caller fails closed for suggestions.
                continue;
            }

            var lifecycle = detail.Value.Status == CategoryStatus.Active ? "active" : "archived";
            var reactivated = detail.Value.LifecycleHistory
                .Any(h => h.Action == CategoryLifecycleAction.Reactivate
                    && TryParseUtc(h.OccurredAt, out var occurred)
                    && occurred > evaluationCreatedAt);
            categoryById[categoryId] = new CategoryInfo(detail.Value.Name, lifecycle, reactivated);
        }

        // Also seed active catalogue identities (display names) for rename-fresh cases.
        foreach (var cat in evalPage.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
        {
            if (!categoryById.ContainsKey(cat.CategoryId))
            {
                categoryById[cat.CategoryId] = new CategoryInfo(
                    cat.DisplayName ?? cat.CategoryId,
                    string.Equals(cat.LifecycleState, "active", StringComparison.Ordinal) ? "active" : "archived",
                    ReactivatedAfterEvaluation: false);
            }
        }

        return new CurrentPublicState(
            LedgerError: null,
            StoreGenerationFingerprint: evalPage.StoreGenerationFingerprint,
            LedgerContractVersion: evalPage.LedgerContractVersion,
            ProjectionVersion: evalPage.ProjectionVersion,
            CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
            OrderedItemsFingerprint: orderedItemsFingerprint,
            ItemLifecycleByTransaction: itemLifecycleByTx,
            CategoryById: categoryById);
    }

    private static bool TryParseExpiresAt(string expiresAt, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    private static bool TryParseUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    private sealed record EnrichedOutcome(ClassifyOutcomeRow Outcome, ClassifyOutcomeListItem Item);

    private sealed record CategoryInfo(string DisplayName, string LifecycleState, bool ReactivatedAfterEvaluation);

    private sealed record CurrentPublicState(
        string? LedgerError,
        string? StoreGenerationFingerprint,
        string? LedgerContractVersion,
        string? ProjectionVersion,
        string? CategoryLifecycleFingerprint,
        string? OrderedItemsFingerprint,
        IReadOnlyDictionary<string, string> ItemLifecycleByTransaction,
        IReadOnlyDictionary<string, CategoryInfo> CategoryById)
    {
        public static CurrentPublicState Failed(string error) =>
            new(
                error,
                null,
                null,
                null,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, CategoryInfo>(StringComparer.Ordinal));
    }
}
