using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Position;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Integration.Ledger;

namespace Tally.Features.Budget.Position.Get;

/// <summary>
/// Compose exact Budget Positions through public LEDGER contracts
/// (FR-BUDGET-POSITION-QUERY / FR-BUDGET-LEDGER-COMPOSITION / DD-BUDGET-EXACT-POSITION-CALCULATION).
/// Binds one immutable revision before any Ledger call, materializes one complete actuals snapshot,
/// calculates once via <see cref="BudgetPositionCalculator"/>, and persists no derived position state.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetBudgetPositionQuery
{
    private readonly BudgetStateStore store;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public GetBudgetPositionQuery(
        BudgetStateStore store,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ledger);
        this.store = store;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<GetBudgetPositionResult>> HandleAsync(
        GetBudgetPositionInput input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.UnsupportedVersion);
        }

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.ActorRequired);
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
            return CommandResult<GetBudgetPositionResult>.Failure(
                periodError ?? BudgetErrors.InvalidPeriod);
        }

        // ── Bind immutable revision before any public Ledger call ────────────
        BudgetPlanRow? plan;
        BudgetPlanRevisionRow revision;
        IReadOnlyList<BudgetPlanEntryRow> entries;

        {
            await using var connection = await store.OpenMigratedAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(input.RevisionId))
            {
                var revisionId = input.RevisionId.Trim();
                var loaded = await store.GetRevisionAsync(connection, null, revisionId, cancellationToken);
                if (loaded is null)
                {
                    return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.RevisionNotFound);
                }

                plan = await store.GetPlanAsync(connection, null, loaded.PlanId, cancellationToken);
                if (plan is null)
                {
                    return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
                }

                // Explicit revision must belong to the supplied period and ZAR before Ledger.
                if (!string.Equals(plan.CurrencyCode, period.CurrencyCode, StringComparison.Ordinal)
                    || !string.Equals(plan.PeriodStart, period.FormatStartInclusive(), StringComparison.Ordinal)
                    || !string.Equals(plan.PeriodEndExclusive, period.FormatEndExclusive(), StringComparison.Ordinal))
                {
                    return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.RevisionPeriodMismatch);
                }

                revision = loaded;
                entries = await store.GetEntriesAsync(connection, null, revisionId, cancellationToken);
            }
            else
            {
                plan = await store.GetPlanByPeriodAsync(
                    connection,
                    null,
                    period.CurrencyCode,
                    period.FormatStartInclusive(),
                    cancellationToken);

                if (plan is null)
                {
                    // Explicit No Budget Plan — success with null position; never fabricate a zero plan.
                    // Distinct from NoActiveBudgetPlanRevision (plan exists, pointer absent).
                    return CommandResult<GetBudgetPositionResult>.Success(
                        BudgetContractMapper.ToPositionResult(
                            position: null,
                            hasActiveBudgetPlanRevision: false));
                }

                if (string.IsNullOrWhiteSpace(plan.ActiveRevisionId))
                {
                    // Plan exists with only Drafts (or cleared pointer) — fail before Ledger.
                    return CommandResult<GetBudgetPositionResult>.Failure(
                        BudgetErrors.NoActiveBudgetPlanRevision);
                }

                var active = await store.GetRevisionAsync(
                    connection, null, plan.ActiveRevisionId, cancellationToken);
                if (active is null)
                {
                    return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
                }

                revision = active;
                entries = await store.GetEntriesAsync(
                    connection, null, plan.ActiveRevisionId, cancellationToken);
            }
        }
        // Connection closed after bind — no further BUDGET writes; position is not retained.

        var domainRevision = BudgetContractMapper.ToDomainRevision(revision, entries);
        var periodDetail = BudgetContractMapper.ToPeriodDetail(period, periodState);
        var hasActive = !string.IsNullOrWhiteSpace(plan.ActiveRevisionId);

        // ── Public LEDGER category + actuals evidence (one complete snapshot) ─
        cancellationToken.ThrowIfCancellationRequested();

        var listed = await ledger.ListBudgetCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken);

        if (!listed.IsSuccess || listed.Value is null)
        {
            return CommandResult<GetBudgetPositionResult>.Failure(
                BudgetContractMapper.MapLedgerCompositionError(listed.Error));
        }

        var knownById = BudgetContractMapper.MapCategoryListEvidence(listed.Value)
            .ToDictionary(e => e.CategoryId, StringComparer.Ordinal);

        var actualsResult = await ledger.QueryBudgetActualsAsync(
            period,
            ActualsContractVersions.Current,
            actor,
            cancellationToken);

        if (!actualsResult.IsSuccess || actualsResult.Value is null)
        {
            return CommandResult<GetBudgetPositionResult>.Failure(
                BudgetContractMapper.MapLedgerCompositionError(actualsResult.Error));
        }

        var actuals = actualsResult.Value;
        var ledgerSnapshot = BudgetContractMapper.TryMapLedgerSnapshot(actuals, out var snapshotError);
        if (ledgerSnapshot is null)
        {
            return CommandResult<GetBudgetPositionResult>.Failure(
                snapshotError ?? BudgetErrors.Integrity);
        }

        if (!BudgetContractMapper.TryMapActualMembers(
                actuals,
                out var members,
                out var expectedTotal,
                out var memberError))
        {
            return CommandResult<GetBudgetPositionResult>.Failure(
                memberError ?? BudgetErrors.Integrity);
        }

        // Resolve every non-null category id referenced by plan or actuals.
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
                // Unknown category identity is integrity — not a softer not-found success.
                if (string.Equals(code, BudgetErrors.LedgerUnavailable, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.SourceStateChanged, StringComparison.Ordinal))
                {
                    // Distinguish missing category (not found) from transport failures.
                    if (got.Error is not null
                        && (string.Equals(got.Error.Category, "not_found", StringComparison.Ordinal)
                            || got.Error.Code.Contains("NOT-FOUND", StringComparison.OrdinalIgnoreCase)
                            || got.Error.Code.Contains("not_found", StringComparison.OrdinalIgnoreCase)))
                    {
                        return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
                    }
                }

                if (string.Equals(code, BudgetErrors.LedgerIncompatible, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.Integrity, StringComparison.Ordinal)
                    || string.Equals(code, BudgetErrors.SourceStateChanged, StringComparison.Ordinal))
                {
                    return CommandResult<GetBudgetPositionResult>.Failure(code);
                }

                // Missing or unresolvable category evidence fails closed as integrity.
                return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
            }

            var evidence = BudgetContractMapper.MapCategoryDetailEvidence(got.Value);
            if (evidence.Lifecycle == CategoryLifecycleStatus.Unknown)
            {
                return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
            }

            knownById[categoryId] = evidence;
        }

        var knownCategories = knownById.Values
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .ToArray();

        // ── Pure calculation once over the complete membership ────────────────
        try
        {
            var position = BudgetPositionCalculator.CalculatePosition(
                domainRevision,
                periodDetail,
                ledgerSnapshot,
                members,
                knownCategories,
                expectedTotal);

            // Ordered category positions already ascending by calculator; Uncategorized is separate.
            return CommandResult<GetBudgetPositionResult>.Success(
                BudgetContractMapper.ToPositionResult(position, hasActive));
        }
        catch (InvalidOperationException ex) when (BudgetContractMapper.IsPositionIntegrityFailure(ex))
        {
            // Overflow, unknown category, ordinal gaps, total mismatch — no partial position.
            return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
        }
        catch (OverflowException)
        {
            return CommandResult<GetBudgetPositionResult>.Failure(BudgetErrors.Integrity);
        }
    }
}
