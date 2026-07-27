using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Position;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.GetRevision;
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
    private readonly GetBudgetPlanRevisionQuery revisionQuery;
    private readonly TimeProvider timeProvider;

    public GetBudgetInsightEvidenceQuery(
        BudgetStateStore store,
        LedgerContractClient ledger,
        GetBudgetPlanRevisionQuery revisionQuery,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(revisionQuery);
        this.store = store;
        this.ledger = ledger;
        this.revisionQuery = revisionQuery;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Convenience constructor that composes <see cref="GetBudgetPlanRevisionQuery"/> from the same stores.
    /// </summary>
    public GetBudgetInsightEvidenceQuery(
        BudgetStateStore store,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
        : this(
            store,
            ledger,
            new GetBudgetPlanRevisionQuery(store, ledger, timeProvider),
            timeProvider)
    {
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

        BudgetPlanRevisionDetail? revisionDetail = null;
        Domain.Budget.Plans.BudgetPlanRevision? domainRevision = null;

        if (planBind.PlanState == BudgetInsightPlanState.BoundRevision)
        {
            domainRevision = planBind.DomainRevision
                ?? throw new InvalidOperationException("BoundRevision requires a domain revision.");

            // Shared owner revision DTO — exact plan detail parity with budget.plan.revision.get.
            var revisionResult = await revisionQuery.HandleAsync(
                new GetBudgetPlanRevisionInput(
                    BudgetOperationIds.ContractVersion,
                    domainRevision.RevisionId),
                actor,
                cancellationToken);

            if (!revisionResult.IsSuccess || revisionResult.Value is null)
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(
                    revisionResult.ErrorCode ?? BudgetErrors.Unexpected);
            }

            revisionDetail = revisionResult.Value;
        }

        // ── One complete public LEDGER actuals snapshot for every valid state ─
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

        if (planBind.PlanState == BudgetInsightPlanState.BoundRevision)
        {
            // Category evidence + pure calculator only for BoundRevision over the same members.
            var categoryResult = await ResolveKnownCategoriesAsync(
                domainRevision!,
                members,
                actor,
                cancellationToken);

            if (categoryResult.ErrorCode is not null)
            {
                return CommandResult<GetBudgetInsightEvidenceResult>.Failure(categoryResult.ErrorCode);
            }

            var periodDetail = BudgetContractMapper.ToPeriodDetail(period, periodState);

            try
            {
                position = BudgetPositionCalculator.CalculatePosition(
                    domainRevision!,
                    periodDetail,
                    ledgerSnapshot,
                    members,
                    categoryResult.Evidence,
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
            members);

        var evidence = new BudgetInsightEvidence(
            PlanState: planBind.PlanState,
            Revision: revisionDetail,
            Position: position,
            ActualMembers: members,
            BudgetActualTotalMinorUnits: expectedTotal,
            Ledger: ledgerSnapshot,
            CalculationSchemaVersion: calculationSchemaVersion,
            BindingFingerprint: bindingFingerprint);

        return CommandResult<GetBudgetInsightEvidenceResult>.Success(
            new GetBudgetInsightEvidenceResult(evidence));
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

    private async Task<CategoryResolutionResult> ResolveKnownCategoriesAsync(
        Domain.Budget.Plans.BudgetPlanRevision domainRevision,
        IReadOnlyList<BudgetActualMember> members,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        var listed = await ledger.ListBudgetCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken);

        if (!listed.IsSuccess || listed.Value is null)
        {
            return CategoryResolutionResult.Fail(
                BudgetContractMapper.MapLedgerCompositionError(listed.Error));
        }

        var knownById = BudgetContractMapper.MapCategoryListEvidence(listed.Value)
            .ToDictionary(e => e.CategoryId, StringComparer.Ordinal);

        var requiredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in domainRevision.Entries)
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

        foreach (var categoryId in requiredIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (knownById.ContainsKey(categoryId))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var got = await ledger.GetBudgetCategoryAsync(
                categoryId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken);

            if (!got.IsSuccess || got.Value is null)
            {
                var code = BudgetContractMapper.MapLedgerCompositionError(got.Error);
                if (string.Equals(code, BudgetErrors.LedgerUnavailable, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.SourceStateChanged, StringComparison.Ordinal))
                {
                    if (got.Error is not null
                        && (string.Equals(got.Error.Category, "not_found", StringComparison.Ordinal)
                            || got.Error.Code.Contains("NOT-FOUND", StringComparison.OrdinalIgnoreCase)
                            || got.Error.Code.Contains("not_found", StringComparison.OrdinalIgnoreCase)))
                    {
                        return CategoryResolutionResult.Fail(BudgetErrors.Integrity);
                    }
                }

                if (string.Equals(code, BudgetErrors.LedgerIncompatible, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.Integrity, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.SourceStateChanged, StringComparison.Ordinal))
                {
                    return CategoryResolutionResult.Fail(code);
                }

                return CategoryResolutionResult.Fail(BudgetErrors.Integrity);
            }

            var evidence = BudgetContractMapper.MapCategoryDetailEvidence(got.Value);
            if (evidence.Lifecycle == CategoryLifecycleStatus.Unknown)
            {
                return CategoryResolutionResult.Fail(BudgetErrors.Integrity);
            }

            knownById[categoryId] = evidence;
        }

        return CategoryResolutionResult.Ok(
            knownById.Values.OrderBy(e => e.CategoryId, StringComparer.Ordinal).ToArray());
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

    private sealed record CategoryResolutionResult(
        string? ErrorCode,
        IReadOnlyList<CategoryLifecycleEvidence> Evidence)
    {
        public static CategoryResolutionResult Ok(IReadOnlyList<CategoryLifecycleEvidence> evidence) =>
            new(null, evidence);

        public static CategoryResolutionResult Fail(string errorCode) =>
            new(errorCode, []);
    }
}
