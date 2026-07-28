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

namespace Tally.Features.Budget.Plans.CreateDraft;

/// <summary>
/// Create Draft vertical slice (FR-BUDGET-PLAN-DRAFT / TASK-BUDGET-DRAFT-CREATION).
/// Validates attribution, period eligibility, unique active category entries, and exact amounts
/// before appending one immutable Draft revision through <see cref="BudgetMutationExecutor"/>.
/// Never changes <c>active_revision_id</c> and never rewrites prior revision content.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CreateBudgetDraftCommand
{
    private readonly BudgetMutationExecutor executor;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public CreateBudgetDraftCommand(
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

    public async Task<CommandResult<CreateDraftBudgetPlanResult>> HandleAsync(
        CreateDraftBudgetPlanInput input,
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
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.IdempotencyRequired);
        }

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.UnsupportedVersion);
        }

        if (!BudgetPlanLifecycle.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.InvalidInput);
        }

        if (!BudgetPeriodResolver.Resolve(
                input.Period?.Year ?? 0,
                input.Period?.Month ?? 0,
                input.Period?.CurrencyCode,
                timeProvider,
                out var period,
                out var periodState,
                out var periodError))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(
                periodError ?? BudgetErrors.InvalidPeriod);
        }

        if (periodState == BudgetPeriodState.Closed)
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.InvalidPeriod);
        }

        if (input.Entries is null)
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.InvalidInput);
        }

        if (!TryNormalizeEntries(input.Entries, out var domainEntries, out var entryError))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(entryError!);
        }

        if (!BudgetPlanRevision.TrySumPlannedMinorUnits(domainEntries, out var plannedTotal))
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.InvalidAmount);
        }

        var categoryValidation = await ValidateCategoriesAsync(domainEntries, actor, cancellationToken);
        if (categoryValidation.ErrorCode is not null)
        {
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(categoryValidation.ErrorCode);
        }

        var categoryContractVersion = categoryValidation.CategoryContractVersion
            ?? CategoryContractVersions.Current;
        var categoryEvidence = categoryValidation.Evidence;
        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();

        var requestHash = BudgetMutationCanonicalizer.HashDraftRequest(new BudgetDraftLogicalRequest(
            input.ContractVersion,
            BudgetOperationIds.DraftCreate,
            actorKind,
            actorLabel,
            actorRunId,
            reason,
            period.Year,
            period.Month,
            period.CurrencyCode,
            domainEntries.Select(e => new BudgetCanonicalEntry(e.CategoryId, e.PlannedMinorUnits)).ToArray()));

        var identity = new BudgetMutationIdentity(
            idempotencyKey,
            input.ContractVersion,
            BudgetOperationIds.DraftCreate,
            requestHash);

        var createdAt = timeProvider.GetUtcNow();
        var createdAtUtc = BudgetPlanRevision.FormatUtc(createdAt);
        var payloadHash = BudgetPlanRevision.ComputePayloadHash(categoryContractVersion, domainEntries);
        var store = executor.StateStore;

        try
        {
            var execution = await executor.ExecuteAsync(
                identity,
                async (connection, transaction, ct) =>
                {
                    var planRow = await store.GetPlanByPeriodAsync(
                        connection,
                        transaction,
                        period.CurrencyCode,
                        period.FormatStartInclusive(),
                        ct);

                    string planId;
                    string? priorActiveRevisionId;
                    if (planRow is null)
                    {
                        planId = BudgetIdentity.New(createdAt).ToString();
                        priorActiveRevisionId = null;
                        await store.InsertPlanAsync(
                            connection,
                            transaction,
                            new BudgetPlanRow(
                                planId,
                                period.FormatStartInclusive(),
                                period.FormatEndExclusive(),
                                period.CurrencyCode,
                                ActiveRevisionId: null,
                                createdAtUtc),
                            ct);
                    }
                    else
                    {
                        planId = planRow.PlanId;
                        priorActiveRevisionId = planRow.ActiveRevisionId;
                    }

                    var revisionNumber = await store.NextRevisionNumberAsync(connection, transaction, planId, ct);
                    var revisionId = BudgetIdentity.New(createdAt).ToString();
                    var eventId = BudgetIdentity.New(createdAt).ToString();
                    var eventSequence = await store.NextEventSequenceAsync(connection, transaction, planId, ct);

                    var revisionRow = new BudgetPlanRevisionRow(
                        revisionId,
                        planId,
                        revisionNumber,
                        BudgetRevisionStatus.Draft,
                        actorKind,
                        actorLabel,
                        actorRunId,
                        reason,
                        createdAtUtc,
                        categoryContractVersion,
                        payloadHash,
                        ActivatedAtUtc: null,
                        SupersededAtUtc: null,
                        SupersededByRevisionId: null);

                    var entryRows = domainEntries
                        .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
                        .Select(e => new BudgetPlanEntryRow(revisionId, e.CategoryId, e.PlannedMinorUnits))
                        .ToArray();

                    var draftEvent = new BudgetLifecycleEventRow(
                        eventId,
                        planId,
                        revisionId,
                        "DraftCreated",
                        actorKind,
                        actorLabel,
                        actorRunId,
                        reason,
                        createdAtUtc,
                        PriorStatus: null,
                        ResultingStatus: BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Draft),
                        ReplacementRevisionId: null,
                        eventSequence);

                    await store.InsertDraftRevisionAsync(
                        connection, transaction, revisionRow, entryRows, draftEvent, ct);

                    // Re-read plan to prove active pointer was not mutated by draft insert.
                    var planAfter = await store.GetPlanAsync(connection, transaction, planId, ct)
                        ?? throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Plan disappeared during draft creation.");
                    if (!string.Equals(planAfter.ActiveRevisionId, priorActiveRevisionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{BudgetErrors.Integrity}: Draft creation must not change active_revision_id.");
                    }

                    return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
                        planId,
                        revisionId,
                        priorActiveRevisionId,
                        [eventId],
                        createdAtUtc,
                        createdAtUtc));
                },
                cancellationToken);

            return MapExecutionResult(
                execution,
                period,
                periodState,
                plannedTotal,
                categoryEvidence);
        }
        catch (InvalidOperationException ex) when (BudgetContractMapper.IsPositionIntegrityFailure(ex))
        {
            // Detected integrity breach fails closed as BUDGET-INTEGRITY (exit 8), never host.unexpected.
            return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.Integrity);
        }
    }

    private async Task<CategoryValidationResult> ValidateCategoriesAsync(
        IReadOnlyList<BudgetPlanEntry> entries,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return CategoryValidationResult.Ok(CategoryContractVersions.Current, []);
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
        string? citedVersion = null;

        foreach (var entry in entries.OrderBy(e => e.CategoryId, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(entry.CategoryId, out var summary))
            {
                // Not in the full catalogue — confirm via get for a precise unknown/inactive code.
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

            citedVersion ??= summary.LedgerContractVersion;
            evidence.Add(new CategoryLifecycleEvidence(
                summary.CategoryId,
                summary.Name,
                CategoryLifecycleStatus.Active,
                summary.LedgerContractVersion));
        }

        return CategoryValidationResult.Ok(
            citedVersion ?? CategoryContractVersions.Current,
            evidence);
    }

    private static CommandResult<CreateDraftBudgetPlanResult> MapExecutionResult(
        BudgetMutationExecutionResult execution,
        BudgetPeriod period,
        BudgetPeriodState periodState,
        long plannedTotal,
        IReadOnlyList<CategoryLifecycleEvidence> categoryEvidence)
    {
        switch (execution.Disposition)
        {
            case BudgetMutationDisposition.Conflict:
                return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.IdempotencyConflict);
            case BudgetMutationDisposition.Rejected:
                return CommandResult<CreateDraftBudgetPlanResult>.Failure(
                    execution.ErrorCode ?? BudgetErrors.Unexpected);
            case BudgetMutationDisposition.Committed:
            case BudgetMutationDisposition.Replayed:
                break;
            default:
                return CommandResult<CreateDraftBudgetPlanResult>.Failure(BudgetErrors.Unexpected);
        }

        var snapshot = execution.Snapshot
            ?? throw new InvalidOperationException("Successful draft mutation must produce a snapshot.");
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

        // Recompute checked total from durable rows — must match the pre-mutation total.
        long durableTotal = 0;
        foreach (var entry in entryDetails)
        {
            durableTotal = checked(durableTotal + entry.PlannedMinorUnits);
        }

        if (durableTotal != plannedTotal)
        {
            // Replay implies a byte-identical request by hash, so a mismatch on either
            // disposition means the durable rows no longer reconcile — store corruption.
            throw new InvalidOperationException(
                $"{BudgetErrors.Integrity}: Durable draft total does not match the validated request total.");
        }

        var periodDetail = new BudgetPeriodDetail(
            period.Year,
            period.Month,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            period.FormatEndExclusive(),
            periodState);

        var detail = new BudgetPlanRevisionDetail(
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
            durableTotal,
            categoryEvidence
                .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
                .ToArray());

        return CommandResult<CreateDraftBudgetPlanResult>.Success(new CreateDraftBudgetPlanResult(detail));
    }

    private static bool TryNormalizeEntries(
        IReadOnlyList<BudgetPlanEntryInput> inputs,
        out IReadOnlyList<BudgetPlanEntry> entries,
        out string? error)
    {
        entries = [];
        error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<BudgetPlanEntry>(inputs.Count);

        foreach (var input in inputs)
        {
            if (input is null
                || string.IsNullOrWhiteSpace(input.CategoryId)
                || !BudgetIdentity.TryParse(input.CategoryId.Trim(), out var categoryId, out _))
            {
                // Display-name-only or malformed identifiers are rejected before mutation.
                error = BudgetErrors.InvalidInput;
                return false;
            }

            if (input.PlannedMinorUnits < 0)
            {
                error = BudgetErrors.InvalidAmount;
                return false;
            }

            var id = categoryId.ToString();
            if (!seen.Add(id))
            {
                error = BudgetErrors.InvalidInput;
                return false;
            }

            normalized.Add(new BudgetPlanEntry(id, input.PlannedMinorUnits));
        }

        entries = normalized
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private sealed record CategoryValidationResult(
        string? ErrorCode,
        string? CategoryContractVersion,
        IReadOnlyList<CategoryLifecycleEvidence> Evidence)
    {
        public static CategoryValidationResult Ok(
            string categoryContractVersion,
            IReadOnlyList<CategoryLifecycleEvidence> evidence) =>
            new(null, categoryContractVersion, evidence);

        public static CategoryValidationResult Fail(string errorCode) =>
            new(errorCode, null, []);
    }
}
