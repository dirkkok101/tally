using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Budget;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Integration.Ledger;

namespace Tally.Features.Budget.Plans.Activate;

/// <summary>
/// Activate Plan Revision vertical slice (FR-BUDGET-PLAN-ACTIVATION / TASK-BUDGET-ACTIVATION-LIFECYCLE).
/// Loads the exact Draft and trusted period, revalidates active categories through the public Ledger client,
/// then atomically activates (and supersedes any prior Active) through <see cref="BudgetMutationExecutor"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ActivateBudgetPlanRevisionCommand
{
    private readonly BudgetMutationExecutor executor;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ActivateBudgetPlanRevisionCommand(
        BudgetMutationExecutor executor,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(ledger);
        this.executor = executor;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ActivateBudgetPlanRevisionResult>> HandleAsync(
        ActivateBudgetPlanRevisionInput input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.IdempotencyRequired);
        }

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.UnsupportedVersion);
        }

        if (!BudgetPlanLifecycle.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.InvalidInput);
        }

        if (string.IsNullOrWhiteSpace(input.RevisionId))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.InvalidInput);
        }

        var revisionId = input.RevisionId.Trim();
        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var store = executor.StateStore;

        // Load exact Draft + plan first; category revalidation must not precede that load
        // (TASK-BUDGET-ACTIVATION-LIFECYCLE failure criteria).
        BudgetPlanRevisionRow? preRevision;
        BudgetPlanRow? prePlan;
        await using (var readConnection = await store.OpenMigratedAsync(cancellationToken))
        {
            preRevision = await store.GetRevisionAsync(readConnection, null, revisionId, cancellationToken);
            if (preRevision is null)
            {
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.RevisionNotFound);
            }

            prePlan = await store.GetPlanAsync(readConnection, null, preRevision.PlanId, cancellationToken);
            if (prePlan is null)
            {
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.PlanNotFound);
            }
        }

        if (!TryResolvePlanPeriod(prePlan, timeProvider, out var period, out var periodState, out var periodError))
        {
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(
                periodError ?? BudgetErrors.InvalidPeriod);
        }

        // Lifecycle eligibility (Draft/open period) is enforced inside the mutation so
        // completed activations can still replay after the revision becomes Active/Superseded
        // or the period later closes. Category revalidation applies only to first-time Draft activation.
        IReadOnlyList<CategoryLifecycleEvidence> categoryEvidence = [];
        var mayActivateNow = BudgetPlanLifecycle.ValidateActivationEligibility(preRevision.Status, periodState) is null;
        if (mayActivateNow)
        {
            // Category contract cited on the draft must still match the released category contract.
            if (!string.Equals(
                    preRevision.CategoryContractVersion,
                    CategoryContractVersions.Current,
                    StringComparison.Ordinal))
            {
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.LedgerIncompatible);
            }

            IReadOnlyList<BudgetPlanEntryRow> preEntries;
            await using (var entryConnection = await store.OpenMigratedAsync(cancellationToken))
            {
                preEntries = await store.GetEntriesAsync(entryConnection, null, revisionId, cancellationToken);
            }

            var categoryValidation = await ValidateCategoriesAsync(preEntries, actor, cancellationToken);
            if (categoryValidation.ErrorCode is not null)
            {
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(categoryValidation.ErrorCode);
            }

            categoryEvidence = categoryValidation.Evidence;
        }

        var requestHash = BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
            input.ContractVersion,
            BudgetOperationIds.RevisionActivate,
            actorKind,
            actorLabel,
            actorRunId,
            reason,
            revisionId));

        var identity = new BudgetMutationIdentity(
            idempotencyKey,
            input.ContractVersion,
            BudgetOperationIds.RevisionActivate,
            requestHash);

        var activatedAt = timeProvider.GetUtcNow();
        var activatedAtUtc = BudgetPlanRevision.FormatUtc(activatedAt);

        try
        {
            var execution = await executor.ExecuteAsync(
                identity,
                async (connection, transaction, ct) =>
                {
                    var revision = await store.GetRevisionAsync(connection, transaction, revisionId, ct);
                    if (revision is null)
                    {
                        return BudgetMutationWorkResult.Failure(BudgetErrors.RevisionNotFound);
                    }

                    var plan = await store.GetPlanAsync(connection, transaction, revision.PlanId, ct);
                    if (plan is null)
                    {
                        return BudgetMutationWorkResult.Failure(BudgetErrors.PlanNotFound);
                    }

                    if (!TryResolvePlanPeriod(plan, timeProvider, out _, out var livePeriodState, out var livePeriodError))
                    {
                        return BudgetMutationWorkResult.Failure(livePeriodError ?? BudgetErrors.InvalidPeriod);
                    }

                    var liveEligibility = BudgetPlanLifecycle.ValidateActivationEligibility(
                        revision.Status,
                        livePeriodState);
                    if (liveEligibility is not null)
                    {
                        return BudgetMutationWorkResult.Failure(liveEligibility);
                    }

                    if (!string.Equals(
                            revision.CategoryContractVersion,
                            CategoryContractVersions.Current,
                            StringComparison.Ordinal))
                    {
                        return BudgetMutationWorkResult.Failure(BudgetErrors.LedgerIncompatible);
                    }

                    var priorActiveRevisionId = plan.ActiveRevisionId;
                    // Cannot activate a draft that is already the plan's active pointer (should be non-Draft).
                    if (string.Equals(priorActiveRevisionId, revisionId, StringComparison.Ordinal))
                    {
                        return BudgetMutationWorkResult.Failure(BudgetErrors.Conflict);
                    }

                    var activateEventId = BudgetIdentity.New(activatedAt).ToString();
                    string? supersedeEventId = BudgetPlanLifecycle.RequiresSupersession(priorActiveRevisionId)
                        ? BudgetIdentity.New(activatedAt).ToString()
                        : null;

                    await store.ActivateRevisionAsync(
                        connection,
                        transaction,
                        plan.PlanId,
                        revisionId,
                        activatedAtUtc,
                        reason,
                        actorKind,
                        actorLabel,
                        actorRunId,
                        activateEventId,
                        supersedeEventId,
                        ct);

                    // Post-condition: exactly one Active on this plan and pointer matches.
                    var planAfter = await store.GetPlanAsync(connection, transaction, plan.PlanId, ct)
                        ?? throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Plan disappeared during activation.");
                    if (!string.Equals(planAfter.ActiveRevisionId, revisionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Activation must set active_revision_id to the activated revision.");
                    }

                    var activated = await store.GetRevisionAsync(connection, transaction, revisionId, ct)
                        ?? throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Activated revision disappeared during activation.");
                    if (activated.Status != BudgetRevisionStatus.Active)
                    {
                        throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Activated revision must be Active after mutation.");
                    }

                    if (priorActiveRevisionId is not null)
                    {
                        var prior = await store.GetRevisionAsync(connection, transaction, priorActiveRevisionId, ct)
                            ?? throw new InvalidOperationException(
                                $"{BudgetErrors.Integrity}: Prior Active revision disappeared during supersession.");
                        if (prior.Status != BudgetRevisionStatus.Superseded)
                        {
                            throw new InvalidOperationException(
                                $"{BudgetErrors.Integrity}: Prior Active revision must be Superseded after replacement.");
                        }
                    }

                    var eventIds = BudgetPlanLifecycle.OrderedActivationEventIds(supersedeEventId, activateEventId);

                    return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
                        plan.PlanId,
                        revisionId,
                        priorActiveRevisionId,
                        eventIds,
                        activatedAtUtc,
                        activatedAtUtc));
                },
                cancellationToken);

            return await MapExecutionResultAsync(
                execution,
                period,
                periodState,
                categoryEvidence,
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (BudgetContractMapper.IsPositionIntegrityFailure(ex))
        {
            // Detected integrity breach fails closed as BUDGET-INTEGRITY (exit 8), never host.unexpected.
            return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.Integrity);
        }
    }

    private async Task<CommandResult<ActivateBudgetPlanRevisionResult>> MapExecutionResultAsync(
        BudgetMutationExecutionResult execution,
        BudgetPeriod period,
        BudgetPeriodState periodState,
        IReadOnlyList<CategoryLifecycleEvidence> categoryEvidence,
        CancellationToken cancellationToken)
    {
        switch (execution.Disposition)
        {
            case BudgetMutationDisposition.Conflict:
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.IdempotencyConflict);
            case BudgetMutationDisposition.Rejected:
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(
                    execution.ErrorCode ?? BudgetErrors.Unexpected);
            case BudgetMutationDisposition.Committed:
            case BudgetMutationDisposition.Replayed:
                break;
            default:
                return CommandResult<ActivateBudgetPlanRevisionResult>.Failure(BudgetErrors.Unexpected);
        }

        var snapshot = execution.Snapshot
            ?? throw new InvalidOperationException("Successful activation mutation must produce a snapshot.");
        var revision = snapshot.Revision;
        var evidenceById = categoryEvidence.ToDictionary(e => e.CategoryId, StringComparer.Ordinal);

        var entryDetails = snapshot.Entries
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .Select(e =>
            {
                evidenceById.TryGetValue(e.CategoryId, out var evidence);
                return new BudgetPlanEntryDetail(
                    e.CategoryId,
                    e.PlannedMinorUnits,
                    evidence?.CurrentDisplayName,
                    evidence?.Lifecycle);
            })
            .ToArray();

        long plannedTotal = 0;
        foreach (var entry in entryDetails)
        {
            plannedTotal = checked(plannedTotal + entry.PlannedMinorUnits);
        }

        var periodDetail = new BudgetPeriodDetail(
            period.Year,
            period.Month,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            period.FormatEndExclusive(),
            periodState);

        var activated = new BudgetPlanRevisionDetail(
            snapshot.PlanId,
            snapshot.ResultRevisionId,
            revision.RevisionNumber,
            revision.Status,
            periodDetail,
            revision.ActorKind,
            revision.ActorLabel,
            revision.ActorRunId,
            revision.Reason,
            revision.CreatedAtUtc,
            revision.CategoryContractVersion,
            revision.PayloadHash,
            revision.ActivatedAtUtc,
            revision.SupersededAtUtc,
            revision.SupersededByRevisionId,
            entryDetails,
            plannedTotal,
            categoryEvidence
                .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
                .ToArray());

        BudgetPlanRevisionSummary? superseded = null;
        if (!string.IsNullOrWhiteSpace(snapshot.PriorActiveRevisionId))
        {
            superseded = await LoadSupersededSummaryAsync(
                snapshot.PriorActiveRevisionId,
                periodDetail,
                cancellationToken);
        }

        return CommandResult<ActivateBudgetPlanRevisionResult>.Success(
            new ActivateBudgetPlanRevisionResult(activated, superseded));
    }

    private async Task<BudgetPlanRevisionSummary> LoadSupersededSummaryAsync(
        string priorRevisionId,
        BudgetPeriodDetail periodDetail,
        CancellationToken cancellationToken)
    {
        var store = executor.StateStore;
        await using var connection = await store.OpenMigratedAsync(cancellationToken);
        var prior = await store.GetRevisionAsync(connection, null, priorRevisionId, cancellationToken)
            // A cited prior Active revision must remain readable; a missing row is store corruption,
            // not the no-prior-revision case.
            ?? throw new InvalidOperationException(
                $"{BudgetErrors.Integrity}: Prior Active revision cited by activation was not found.");

        var entries = await store.GetEntriesAsync(connection, null, priorRevisionId, cancellationToken);
        long total = 0;
        foreach (var entry in entries)
        {
            total = checked(total + entry.PlannedMinorUnits);
        }

        return new BudgetPlanRevisionSummary(
            prior.PlanId,
            prior.RevisionId,
            prior.RevisionNumber,
            prior.Status,
            periodDetail,
            prior.CreatedAtUtc,
            total,
            entries.Count);
    }

    private async Task<CategoryValidationResult> ValidateCategoriesAsync(
        IReadOnlyList<BudgetPlanEntryRow> entries,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return CategoryValidationResult.Ok([]);
        }

        var listed = await ledger.ListBudgetCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken);

        if (!listed.IsSuccess || listed.Value is null)
        {
            return CategoryValidationResult.Fail(BudgetContractMapper.MapLedgerCompositionError(listed.Error));
        }

        if (!string.Equals(
                listed.Value.LedgerContractVersion,
                CategoryContractVersions.Current,
                StringComparison.Ordinal))
        {
            return CategoryValidationResult.Fail(BudgetErrors.LedgerIncompatible);
        }

        var byId = listed.Value.Items.ToDictionary(
            item => item.CategoryId,
            StringComparer.Ordinal);

        var evidence = new List<CategoryLifecycleEvidence>(entries.Count);

        foreach (var entry in entries.OrderBy(e => e.CategoryId, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(entry.CategoryId, out var summary))
            {
                var got = await ledger.GetBudgetCategoryAsync(
                    entry.CategoryId,
                    CategoryContractVersions.Current,
                    actor,
                    cancellationToken);

                if (!got.IsSuccess || got.Value is null)
                {
                    return CategoryValidationResult.Fail(BudgetContractMapper.MapMissingCategory(got.Error));
                }

                if (got.Value.Status != CategoryStatus.Active)
                {
                    return CategoryValidationResult.Fail(BudgetErrors.CategoryInactive);
                }

                if (!string.Equals(
                        got.Value.LedgerContractVersion,
                        CategoryContractVersions.Current,
                        StringComparison.Ordinal))
                {
                    return CategoryValidationResult.Fail(BudgetErrors.LedgerIncompatible);
                }

                summary = new CategorySummary(
                    got.Value.CategoryId,
                    got.Value.Name,
                    got.Value.Status,
                    got.Value.ParentCategoryId,
                    got.Value.Depth,
                    got.Value.AncestryIds,
                    got.Value.LedgerContractVersion);
            }

            if (summary.Status != CategoryStatus.Active)
            {
                return CategoryValidationResult.Fail(BudgetErrors.CategoryInactive);
            }

            if (!string.Equals(
                    summary.LedgerContractVersion,
                    CategoryContractVersions.Current,
                    StringComparison.Ordinal))
            {
                return CategoryValidationResult.Fail(BudgetErrors.LedgerIncompatible);
            }

            evidence.Add(new CategoryLifecycleEvidence(
                summary.CategoryId,
                summary.Name,
                CategoryLifecycleStatus.Active,
                summary.LedgerContractVersion));
        }

        return CategoryValidationResult.Ok(evidence);
    }

    private static bool TryResolvePlanPeriod(
        BudgetPlanRow plan,
        TimeProvider timeProvider,
        out BudgetPeriod period,
        out BudgetPeriodState periodState,
        out string? error)
    {
        period = default;
        periodState = default;
        error = null;

        if (!DateOnly.TryParseExact(
                plan.PeriodStart,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start))
        {
            error = BudgetErrors.Integrity;
            return false;
        }

        return BudgetPeriodResolver.Resolve(
            start.Year,
            start.Month,
            plan.CurrencyCode,
            timeProvider,
            out period,
            out periodState,
            out error);
    }

    private sealed record CategoryValidationResult(
        string? ErrorCode,
        IReadOnlyList<CategoryLifecycleEvidence> Evidence)
    {
        public static CategoryValidationResult Ok(IReadOnlyList<CategoryLifecycleEvidence> evidence) =>
            new(null, evidence);

        public static CategoryValidationResult Fail(string errorCode) =>
            new(errorCode, []);
    }
}
