using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
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
/// classify.outcome.get vertical slice
/// (FR-CLASSIFY-OUTCOME-EXPLANATION / FR-CLASSIFY-OUTCOME-INVALIDATION / TASK-CLASSIFY-RULEBOOK-OUTCOME-EXPLANATION).
/// Reads retained CLASSIFY evaluation + match evidence and public Ledger display/lifecycle state only.
/// Never reconstructs missing MatchEvidence, never mutates Ledger or CLASSIFY durable state.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetClassificationOutcomeQuery
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public GetClassificationOutcomeQuery(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyOutcomeGetResult>> HandleAsync(
        ClassifyOutcomeGetRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.EvaluationId)
            || string.IsNullOrWhiteSpace(input.TransactionId))
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var evaluationId = input.EvaluationId.Trim();
        var transactionId = input.TransactionId.Trim();

        ClassifyEvaluationRunRow? run;
        ClassifyOutcomeRow? outcome;
        IReadOnlyList<ClassifyMatchEvidenceRow> evidence;
        string? currentRuleSetVersionId;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            run = await evaluationStore.GetRunAsync(connection, null, evaluationId, cancellationToken);
            if (run is null)
            {
                return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.EvaluationNotFound);
            }

            if (!string.Equals(run.LifecycleState, ClassifyContractMapper.EvaluationLifecycleCompleted, StringComparison.Ordinal))
            {
                return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.Lifecycle);
            }

            var outcomes = await evaluationStore.ListOutcomesAsync(
                connection, null, evaluationId, cancellationToken);
            outcome = outcomes.FirstOrDefault(o =>
                string.Equals(o.TransactionId, transactionId, StringComparison.Ordinal));
            if (outcome is null)
            {
                return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.OutcomeNotFound);
            }

            evidence = await evaluationStore.ListEvidenceForOutcomeAsync(
                connection, null, outcome.OutcomeId, cancellationToken);

            var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            currentRuleSetVersionId = active?.RuleSetVersionId;
        }

        ClassificationOutcomeKind kind;
        try
        {
            kind = ClassifyContractMapper.ParseStoredOutcomeType(outcome.OutcomeType);
        }
        catch (ArgumentOutOfRangeException)
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyContractMapper.EvidenceUnavailable);
        }

        // Retained evidence completeness — never reconstruct from current Ledger/rule state.
        if (!ClassifyContractMapper.TryValidateRetainedEvidence(kind, evidence, out var evidenceError))
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(
                evidenceError ?? ClassifyContractMapper.EvidenceUnavailable);
        }

        var retainedFingerprint = ClassifyContractMapper.ToRetainedEvaluationFingerprint(run);
        if (!TryParseExpiresAt(run.SnapshotExpiresAt, out var retainedExpiresAt))
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(ClassifyErrors.Integrity);
        }

        // Public Ledger reads for display names and current lifecycle/fingerprint state only.
        var current = await ReadCurrentPublicStateAsync(
            actor,
            transactionId,
            outcome.CategoryId,
            run,
            currentRuleSetVersionId,
            cancellationToken);

        if (current.LedgerError is not null)
        {
            return CommandResult<ClassifyOutcomeGetResult>.Failure(current.LedgerError);
        }

        var staleness = ClassificationStalenessPolicy.Evaluate(new ClassificationStalenessPolicy.Input(
            RetainedEvaluation: retainedFingerprint,
            RetainedItemLifecycleFingerprint: outcome.ItemLifecycleFingerprint,
            SuggestedCategoryId: kind == ClassificationOutcomeKind.Suggestion ? outcome.CategoryId : null,
            CurrentStoreGenerationFingerprint: current.StoreGenerationFingerprint,
            CurrentLedgerContractVersion: current.LedgerContractVersion,
            CurrentProjectionVersion: current.ProjectionVersion,
            CurrentCategoryLifecycleFingerprint: current.CategoryLifecycleFingerprint,
            CurrentNormalizationVersion: NormalizationDescriptor.V1.Version,
            CurrentRuleSetVersionId: currentRuleSetVersionId,
            CurrentOrderedItemsFingerprint: current.OrderedItemsFingerprint,
            CurrentItemLifecycleFingerprint: current.ItemLifecycleFingerprint,
            TransactionFoundInLedger: current.TransactionFound,
            SuggestedCategoryLifecycleState: current.SuggestedCategoryLifecycleState,
            NowUtc: timeProvider.GetUtcNow(),
            RetainedSnapshotExpiresAt: retainedExpiresAt));

        var result = ClassifyContractMapper.ToOutcomeGetResult(
            run,
            outcome,
            evidence,
            staleness.IsStale,
            staleness.ChangedDimensions,
            current.SuggestedCategoryDisplayName);

        // Policy surface available for composition/tests: only re-evaluate is permitted when stale/unappliable.
        _ = ClassifyContractMapper.PermittedNextOperationId(kind, staleness);

        return CommandResult<ClassifyOutcomeGetResult>.Success(result);
    }

    private async Task<CurrentPublicState> ReadCurrentPublicStateAsync(
        SafeActor actor,
        string transactionId,
        string? suggestedCategoryId,
        ClassifyEvaluationRunRow retainedRun,
        string? currentRuleSetVersionId,
        CancellationToken cancellationToken)
    {
        // apply_preflight returns the selected transaction's current revision tuple without mutating Ledger.
        var preflight = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            retainedRun.LedgerContractVersion,
            actor,
            cancellationToken,
            transactionIds: [transactionId]);

        if (!preflight.IsSuccess || preflight.Value is null)
        {
            return CurrentPublicState.Failed(
                ClassifyContractMapper.MapLedgerCategoryListError(preflight.Error));
        }

        var page = preflight.Value;
        var item = page.ClassificationItems?
            .FirstOrDefault(i => string.Equals(i.TransactionId, transactionId, StringComparison.Ordinal));
        var transactionFound = item is not null
            || (page.MissingTransactionIds is not null
                && page.MissingTransactionIds.Contains(transactionId, StringComparer.Ordinal) is false
                && page.ClassificationItems is { Count: > 0 });

        // Missing from store → preflight lists missing IDs.
        if (page.MissingTransactionIds is not null
            && page.MissingTransactionIds.Contains(transactionId, StringComparer.Ordinal))
        {
            transactionFound = false;
        }

        string? itemLifecycle = item is null
            ? null
            : ClassificationEvaluationInputLoader.ComputeItemLifecycleFingerprint(item);

        // Full evaluation-purpose projection for store generation / ordered membership / category catalogue.
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
        var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
            (evalPage.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
                .OrderBy(i => i.Ordinal)
                .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                .Select(i => (
                    i.Ordinal,
                    i.TransactionId,
                    ClassificationEvaluationInputLoader.ComputeItemLifecycleFingerprint(i))));

        var categoryLifecycleFingerprint = !string.IsNullOrWhiteSpace(evalPage.CategoryIdentityLifecycleFingerprint)
            ? evalPage.CategoryIdentityLifecycleFingerprint
            : EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                (evalPage.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                    .Select(c => (c.CategoryId, c.LifecycleState)));

        string? displayName = null;
        string? suggestedLifecycle = null;
        if (!string.IsNullOrWhiteSpace(suggestedCategoryId))
        {
            // List without status filter so rename (active) and archive are both visible.
            var listed = await ledger.ListClassificationCategoriesAsync(
                CategoryContractVersions.Current,
                actor,
                cancellationToken,
                status: null);
            if (!listed.IsSuccess || listed.Value is null)
            {
                return CurrentPublicState.Failed(
                    ClassifyContractMapper.MapLedgerCategoryListError(listed.Error));
            }

            var match = listed.Value.Items
                .FirstOrDefault(c => string.Equals(c.CategoryId, suggestedCategoryId, StringComparison.Ordinal));
            if (match is not null)
            {
                displayName = match.Name;
                suggestedLifecycle = match.Status == CategoryStatus.Active ? "active" : "archived";
            }
            else
            {
                suggestedLifecycle = null;
            }
        }

        return new CurrentPublicState(
            LedgerError: null,
            StoreGenerationFingerprint: evalPage.StoreGenerationFingerprint ?? page.StoreGenerationFingerprint,
            LedgerContractVersion: evalPage.LedgerContractVersion,
            ProjectionVersion: evalPage.ProjectionVersion ?? page.ProjectionVersion,
            CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
            OrderedItemsFingerprint: orderedItemsFingerprint,
            ItemLifecycleFingerprint: itemLifecycle,
            TransactionFound: transactionFound,
            SuggestedCategoryDisplayName: displayName,
            SuggestedCategoryLifecycleState: suggestedLifecycle);
    }

    private static bool TryParseExpiresAt(string expiresAt, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    private sealed record CurrentPublicState(
        string? LedgerError,
        string? StoreGenerationFingerprint,
        string? LedgerContractVersion,
        string? ProjectionVersion,
        string? CategoryLifecycleFingerprint,
        string? OrderedItemsFingerprint,
        string? ItemLifecycleFingerprint,
        bool TransactionFound,
        string? SuggestedCategoryDisplayName,
        string? SuggestedCategoryLifecycleState)
    {
        public static CurrentPublicState Failed(string error) =>
            new(error, null, null, null, null, null, null, false, null, null);
    }
}
