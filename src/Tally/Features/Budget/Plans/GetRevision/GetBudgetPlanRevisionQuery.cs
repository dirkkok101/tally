using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Common;
using Tally.Domain.Budget.Periods;
using Tally.Features.Budget.Categories;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Integration.Ledger;

namespace Tally.Features.Budget.Plans.GetRevision;

/// <summary>
/// Get Budget Plan Revision by stable revision identifier (FR-BUDGET-PLAN-HISTORY).
/// Returns exact immutable payload rows, checked total, period lifecycle, attribution,
/// and activation/supersession provenance. Current category display name and lifecycle
/// are supplemental Ledger evidence and never rewrite stored IDs, amounts, or payload hash.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetBudgetPlanRevisionQuery
{
    private readonly BudgetStateStore store;
    private readonly BudgetCategoryEvidenceResolver categoryEvidence;
    private readonly TimeProvider timeProvider;

    public GetBudgetPlanRevisionQuery(
        BudgetStateStore store,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
        : this(store, new BudgetCategoryEvidenceResolver(ledger), timeProvider)
    {
    }

    public GetBudgetPlanRevisionQuery(
        BudgetStateStore store,
        BudgetCategoryEvidenceResolver categoryEvidence,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(categoryEvidence);
        this.store = store;
        this.categoryEvidence = categoryEvidence;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<BudgetPlanRevisionDetail>> HandleAsync(
        GetBudgetPlanRevisionInput input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.RevisionId))
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.InvalidInput);
        }

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            // Ledger enrichment requires a SafeActor envelope; missing actor fails closed.
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.ActorRequired);
        }

        var revisionId = input.RevisionId.Trim();

        await using var connection = await store.OpenMigratedAsync(cancellationToken);
        var revision = await store.GetRevisionAsync(connection, null, revisionId, cancellationToken);
        if (revision is null)
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.RevisionNotFound);
        }

        var plan = await store.GetPlanAsync(connection, null, revision.PlanId, cancellationToken);
        if (plan is null)
        {
            // Plan identity must resolve for a durable revision; do not invent period boundaries.
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.Integrity);
        }

        var entries = await store.GetEntriesAsync(connection, null, revisionId, cancellationToken);

        if (!TryBuildPeriodDetail(plan, out var periodDetail, out var periodError))
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(periodError ?? BudgetErrors.Integrity);
        }

        long plannedTotal = 0;
        try
        {
            foreach (var entry in entries)
            {
                plannedTotal = checked(plannedTotal + entry.PlannedMinorUnits);
            }
        }
        catch (OverflowException)
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(BudgetErrors.Integrity);
        }

        var enrichment = await EnrichCategoriesAsync(entries, actor, cancellationToken);
        if (enrichment.ErrorCode is not null)
        {
            return CommandResult<BudgetPlanRevisionDetail>.Failure(enrichment.ErrorCode);
        }

        var evidenceById = enrichment.Evidence.ToDictionary(e => e.CategoryId, StringComparer.Ordinal);
        var entryDetails = entries
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

        var detail = new BudgetPlanRevisionDetail(
            plan.PlanId,
            revision.RevisionId,
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
            enrichment.Evidence
                .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
                .ToArray());

        return CommandResult<BudgetPlanRevisionDetail>.Success(detail);
    }

    private bool TryBuildPeriodDetail(
        BudgetPlanRow plan,
        out BudgetPeriodDetail periodDetail,
        out string? error)
    {
        periodDetail = null!;
        error = null;

        if (!DateOnly.TryParseExact(
                plan.PeriodStart,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startInclusive))
        {
            error = BudgetErrors.Integrity;
            return false;
        }

        if (!BudgetPeriodResolver.Resolve(
                startInclusive.Year,
                startInclusive.Month,
                plan.CurrencyCode,
                timeProvider,
                out var period,
                out var periodState,
                out var periodError))
        {
            error = periodError ?? BudgetErrors.Integrity;
            return false;
        }

        // Stored half-open bounds remain authoritative for identity; state is host-computed.
        if (!string.Equals(period.FormatStartInclusive(), plan.PeriodStart, StringComparison.Ordinal)
            || !string.Equals(period.FormatEndExclusive(), plan.PeriodEndExclusive, StringComparison.Ordinal))
        {
            error = BudgetErrors.Integrity;
            return false;
        }

        periodDetail = new BudgetPeriodDetail(
            period.Year,
            period.Month,
            period.CurrencyCode,
            plan.PeriodStart,
            plan.PeriodEndExclusive,
            periodState);
        return true;
    }

    private async Task<CategoryEnrichmentResult> EnrichCategoriesAsync(
        IReadOnlyList<BudgetPlanEntryRow> entries,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        var resolved = await categoryEvidence.ResolveAsync(
            entries.Select(e => e.CategoryId).ToArray(),
            BudgetCategoryEvidenceResolver.Mode.EnrichSupplemental,
            actor,
            cancellationToken);
        return resolved.ErrorCode is null
            ? CategoryEnrichmentResult.Ok(resolved.Evidence)
            : CategoryEnrichmentResult.Fail(resolved.ErrorCode);
    }

    private sealed record CategoryEnrichmentResult(
        string? ErrorCode,
        IReadOnlyList<CategoryLifecycleEvidence> Evidence)
    {
        public static CategoryEnrichmentResult Ok(IReadOnlyList<CategoryLifecycleEvidence> evidence) =>
            new(null, evidence);

        public static CategoryEnrichmentResult Fail(string errorCode) =>
            new(errorCode, []);
    }
}
