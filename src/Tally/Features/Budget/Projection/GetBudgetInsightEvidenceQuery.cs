using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Position;
using Tally.Features.Budget.Categories;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Integration.Ledger;

namespace Tally.Features.Budget.Projection;

/// <summary>
/// BUDGET-owned coherent INSIGHTS evidence producer
/// (FR-BUDGET-INSIGHTS-PROJECTION / DD-BUDGET-INSIGHTS-READ-PROJECTION /
/// DD-INSIGHTS-COHERENT-PUBLIC-EVIDENCE / DD-BUDGET-EXACT-POSITION-CALCULATION).
/// Resolves BoundRevision, NoBudgetPlan, or NoActiveBudgetPlanRevision before composition;
/// invokes the released LEDGER actuals query once for every valid period state; invokes
/// <see cref="BudgetPositionCalculator"/> only for BoundRevision over that same member set;
/// persists no report, recommendation, or consumer state.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetBudgetInsightEvidenceQuery
{
    /// <summary>Default memberLimit when omitted (matches capability MaxLimit).</summary>
    public const int DefaultMemberLimit = BudgetReadProjectionModule.InsightsEvidenceDefaultMemberLimit;

    /// <summary>Hard upper bound — one-over returns <see cref="BudgetErrors.ResourceLimit"/>.</summary>
    public const int MaxMemberLimit = BudgetReadProjectionModule.InsightsEvidenceMaxMemberLimit;

    private readonly BudgetStateStore store;
    private readonly LedgerContractClient ledger;
    private readonly BudgetCategoryEvidenceResolver categoryEvidence;
    private readonly TimeProvider timeProvider;

    public GetBudgetInsightEvidenceQuery(
        BudgetStateStore store,
        LedgerContractClient ledger,
        BudgetCategoryEvidenceResolver? categoryEvidence = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ledger);
        this.store = store;
        this.ledger = ledger;
        this.categoryEvidence = categoryEvidence ?? new BudgetCategoryEvidenceResolver(ledger);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<GetBudgetInsightEvidenceResult>> HandleAsync(
        GetBudgetInsightEvidenceInput input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.UnsupportedVersion);
        }

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.ActorRequired);
        }

        if (!BudgetPeriodResolver.Resolve(
                input.BudgetPeriod?.Year ?? 0,
                input.BudgetPeriod?.Month ?? 0,
                input.BudgetPeriod?.CurrencyCode,
                timeProvider,
                out var period,
                out var periodState,
                out var periodError))
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                periodError ?? BudgetErrors.InvalidPeriod);
        }

        if (!TryResolveMemberLimit(input.MemberLimit, out var memberLimit, out var limitError))
        {
            // Resource failure before any financial Ledger output.
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(limitError!);
        }

        // ── Resolve planState before any public LEDGER call ──────────────────
        var planBind = await BindPlanStateAsync(input.RevisionId, period, cancellationToken);
        if (planBind.ErrorCode is not null)
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(planBind.ErrorCode);
        }

        Domain.Budget.Plans.BudgetPlanRevision? domainRevision = null;
        if (planBind.PlanState == BudgetInsightPlanState.BoundRevision)
        {
            domainRevision = planBind.DomainRevision
                ?? throw new InvalidOperationException("BoundRevision requires a domain revision.");
        }

        // ── One complete public LEDGER actuals snapshot for every valid state ─
        // Category evidence is resolved once after actuals (bd-b5fl) — never a second
        // catalogue read via GetBudgetPlanRevisionQuery (DD-BUDGET-INSIGHTS-READ-PROJECTION).
        cancellationToken.ThrowIfCancellationRequested();

        var actualsResult = await ledger.QueryBudgetActualsAsync(
            period,
            ActualsContractVersions.Current,
            actor,
            cancellationToken);

        if (!actualsResult.IsSuccess || actualsResult.Value is null)
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                BudgetContractMapper.MapLedgerCompositionError(actualsResult.Error));
        }

        var actuals = actualsResult.Value;
        var ledgerSnapshot = BudgetContractMapper.TryMapLedgerSnapshot(actuals, out var snapshotError);
        if (ledgerSnapshot is null)
        {
            // Incomplete provenance cannot prove one binding.
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                snapshotError is not null
                && string.Equals(snapshotError, BudgetErrors.Integrity, StringComparison.Ordinal)
                    ? BudgetErrors.SourceStateChanged
                    : snapshotError ?? BudgetErrors.SourceStateChanged);
        }

        if (!BudgetContractMapper.TryMapActualMembers(
                actuals,
                out var members,
                out var expectedTotal,
                out var memberError))
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                memberError ?? BudgetErrors.Integrity);
        }

        // Complete set must fit the validated limit — never silent truncation.
        if (members.Count > memberLimit)
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.ResourceLimit);
        }

        // Every valid state returns complete members once; checked sum equals LEDGER total.
        try
        {
            var membershipSum = BudgetInsightEvidenceBinding.CheckedMemberSum(members);
            if (membershipSum != expectedTotal)
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.Integrity);
            }
        }
        catch (OverflowException)
        {
            return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.Integrity);
        }

        BudgetPosition? position = null;
        string? calculationSchemaVersion = null;
        BudgetPlanRevisionDetail? revisionDetail = null;
        string? categoryContractVersion = null;
        IReadOnlyList<CategoryLifecycleEvidence> boundCategoryEvidence = [];

        if (planBind.PlanState == BudgetInsightPlanState.BoundRevision)
        {
            // Single catalogue resolution for plan entries + actual members (no second list).
            var requiredIds = new List<string>();
            foreach (var entry in domainRevision!.Entries)
            {
                requiredIds.Add(entry.CategoryId);
            }

            foreach (var member in members)
            {
                if (member.CategoryId is not null)
                {
                    requiredIds.Add(member.CategoryId);
                }
            }

            var categoryResult = await categoryEvidence.ResolveAsync(
                requiredIds,
                BudgetCategoryEvidenceResolver.Mode.ResolveKnown,
                actor,
                cancellationToken);

            if (categoryResult.ErrorCode is not null)
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(categoryResult.ErrorCode);
            }

            categoryContractVersion = categoryResult.CategoryContractVersion;
            boundCategoryEvidence = categoryResult.Evidence;
            var periodDetail = BudgetContractMapper.ToPeriodDetail(period, periodState);
            revisionDetail = ToRevisionDetail(domainRevision, periodDetail, boundCategoryEvidence);

            try
            {
                position = BudgetPositionCalculator.CalculatePosition(
                    domainRevision,
                    periodDetail,
                    ledgerSnapshot,
                    members,
                    boundCategoryEvidence,
                    expectedTotal);

                calculationSchemaVersion = position.CalculationSchemaVersion;

                // Position provenance must match the member-set LEDGER binding exactly.
                if (!string.Equals(position.Ledger.SnapshotId, ledgerSnapshot.SnapshotId, StringComparison.Ordinal)
                    || !string.Equals(
                        position.Ledger.StoreGenerationFingerprint,
                        ledgerSnapshot.StoreGenerationFingerprint,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        position.Ledger.ContractVersion,
                        ledgerSnapshot.ContractVersion,
                        StringComparison.Ordinal))
                {
                    return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                        BudgetErrors.SourceStateChanged);
                }

                if (position.Totals.ActualMinorUnits != expectedTotal)
                {
                    return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.Integrity);
                }
            }
            catch (InvalidOperationException ex) when (BudgetContractMapper.IsPositionIntegrityFailure(ex))
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.Integrity);
            }
            catch (OverflowException)
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(BudgetErrors.Integrity);
            }
        }
        // else: NoBudgetPlan / NoActiveBudgetPlanRevision — never invoke the calculator;
        // revisionDetail and position remain null; calculationSchemaVersion remains null.

        var bindingFingerprint = BudgetInsightEvidenceBinding.ComputeBindingFingerprint(
            planBind.PlanState,
            revisionDetail?.RevisionId,
            calculationSchemaVersion,
            ledgerSnapshot,
            expectedTotal,
            members,
            categoryContractVersion,
            boundCategoryEvidence);

        var evidence = new BudgetInsightEvidence(
            PlanState: planBind.PlanState,
            Revision: revisionDetail,
            Position: position,
            ActualMembers: members,
            BudgetActualTotalMinorUnits: expectedTotal,
            Ledger: ledgerSnapshot,
            CalculationSchemaVersion: calculationSchemaVersion,
            CategoryContractVersion: categoryContractVersion,
            BindingFingerprint: bindingFingerprint);

        return CommandResult<GetBudgetInsightEvidenceResult>.Success(
            new GetBudgetInsightEvidenceResult(evidence));
    }

    private static BudgetPlanRevisionDetail ToRevisionDetail(
        Domain.Budget.Plans.BudgetPlanRevision domain,
        BudgetPeriodDetail periodDetail,
        IReadOnlyList<CategoryLifecycleEvidence> evidence)
    {
        var byId = evidence.ToDictionary(e => e.CategoryId, StringComparer.Ordinal);
        var entryDetails = domain.Entries
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .Select(e =>
            {
                byId.TryGetValue(e.CategoryId, out var row);
                return new BudgetPlanEntryDetail(
                    e.CategoryId,
                    e.PlannedMinorUnits,
                    row?.CurrentDisplayName,
                    row?.Lifecycle);
            })
            .ToArray();

        // Plan-entry evidence only on the revision DTO (subset of full known set).
        var planEvidence = domain.Entries
            .Select(e => byId.TryGetValue(e.CategoryId, out var row) ? row : null)
            .Where(e => e is not null)
            .Cast<CategoryLifecycleEvidence>()
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .ToArray();

        return new BudgetPlanRevisionDetail(
            domain.PlanId,
            domain.RevisionId,
            domain.RevisionNumber,
            domain.Status,
            periodDetail,
            domain.ActorKind,
            domain.ActorLabel,
            domain.ActorRunId,
            domain.Reason,
            Domain.Budget.Plans.BudgetPlanRevision.FormatUtc(domain.CreatedAtUtc),
            domain.CategoryContractVersion,
            domain.PayloadHash,
            domain.ActivatedAtUtc is null
                ? null
                : Domain.Budget.Plans.BudgetPlanRevision.FormatUtc(domain.ActivatedAtUtc.Value),
            domain.SupersededAtUtc is null
                ? null
                : Domain.Budget.Plans.BudgetPlanRevision.FormatUtc(domain.SupersededAtUtc.Value),
            domain.SupersededByRevisionId,
            entryDetails,
            domain.PlannedTotalMinorUnits(),
            planEvidence);
    }

    private static bool TryResolveMemberLimit(int? requested, out int limit, out string? error)
    {
        error = null;
        if (requested is null)
        {
            limit = DefaultMemberLimit;
            return true;
        }

        if (requested.Value is < 1 or > MaxMemberLimit)
        {
            limit = 0;
            error = BudgetErrors.ResourceLimit;
            return false;
        }

        limit = requested.Value;
        return true;
    }

    private async Task<PlanBindResult> BindPlanStateAsync(
        string? revisionId,
        BudgetPeriod period,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenMigratedAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(revisionId))
        {
            var id = revisionId.Trim();
            var loaded = await store.GetRevisionAsync(connection, null, id, cancellationToken);
            if (loaded is null)
            {
                return PlanBindResult.Fail(BudgetErrors.RevisionNotFound);
            }

            var plan = await store.GetPlanAsync(connection, null, loaded.PlanId, cancellationToken);
            if (plan is null)
            {
                return PlanBindResult.Fail(BudgetErrors.Integrity);
            }

            if (!string.Equals(plan.CurrencyCode, period.CurrencyCode, StringComparison.Ordinal)
                || !string.Equals(plan.PeriodStart, period.FormatStartInclusive(), StringComparison.Ordinal)
                || !string.Equals(plan.PeriodEndExclusive, period.FormatEndExclusive(), StringComparison.Ordinal))
            {
                return PlanBindResult.Fail(BudgetErrors.RevisionPeriodMismatch);
            }

            var entries = await store.GetEntriesAsync(connection, null, id, cancellationToken);
            var domain = BudgetContractMapper.ToDomainRevision(loaded, entries);
            return PlanBindResult.Bound(domain);
        }

        var byPeriod = await store.GetPlanByPeriodAsync(
            connection,
            null,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            cancellationToken);

        if (byPeriod is null)
        {
            // Explicit No Budget Plan — success planState; never NotFound / zero plan.
            return PlanBindResult.Absent(BudgetInsightPlanState.NoBudgetPlan);
        }

        if (string.IsNullOrWhiteSpace(byPeriod.ActiveRevisionId))
        {
            // Plan exists without Active pointer — success planState; never NotFound.
            return PlanBindResult.Absent(BudgetInsightPlanState.NoActiveBudgetPlanRevision);
        }

        var active = await store.GetRevisionAsync(
            connection, null, byPeriod.ActiveRevisionId, cancellationToken);
        if (active is null)
        {
            return PlanBindResult.Fail(BudgetErrors.Integrity);
        }

        var activeEntries = await store.GetEntriesAsync(
            connection, null, byPeriod.ActiveRevisionId, cancellationToken);
        return PlanBindResult.Bound(BudgetContractMapper.ToDomainRevision(active, activeEntries));
    }

    private sealed record PlanBindResult(
        BudgetInsightPlanState PlanState,
        Domain.Budget.Plans.BudgetPlanRevision? DomainRevision,
        string? ErrorCode)
    {
        public static PlanBindResult Bound(Domain.Budget.Plans.BudgetPlanRevision revision) =>
            new(BudgetInsightPlanState.BoundRevision, revision, null);

        public static PlanBindResult Absent(BudgetInsightPlanState planState) =>
            new(planState, null, null);

        public static PlanBindResult Fail(string errorCode) =>
            new(BudgetInsightPlanState.NoBudgetPlan, null, errorCode);
    }
}
