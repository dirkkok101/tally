using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Domain.Budget.Periods;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;

namespace Tally.Features.Budget.Plans.ListRevisions;

/// <summary>
/// List Budget Plan Revisions for an explicit Budget Period (FR-BUDGET-PLAN-HISTORY).
/// Returns Draft, Active, and Superseded summaries ordered by createdAt ascending with
/// revisionId ascending as the tie-breaker. No Ledger, actuals, mutation, or idempotency.
/// NoBudgetPlan is an empty successful list; no-active is any list with no Active row.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ListBudgetPlanRevisionsQuery
{
    /// <summary>Default page size when the caller omits Limit (personal-scale history).</summary>
    public const int DefaultLimit = 100;

    /// <summary>Hard upper bound on Limit — one-over returns <see cref="BudgetErrors.ResourceLimit"/>.</summary>
    public const int MaxLimit = 100;

    private readonly BudgetStateStore store;
    private readonly TimeProvider timeProvider;

    public ListBudgetPlanRevisionsQuery(
        BudgetStateStore store,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ListBudgetPlanRevisionsResult>> HandleAsync(
        ListBudgetPlanRevisionsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (!BudgetContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ListBudgetPlanRevisionsResult>.Failure(BudgetErrors.UnsupportedVersion);
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
            return CommandResult<ListBudgetPlanRevisionsResult>.Failure(
                periodError ?? BudgetErrors.InvalidPeriod);
        }

        if (!TryResolveLimit(input.Limit, out var limit, out var limitError))
        {
            return CommandResult<ListBudgetPlanRevisionsResult>.Failure(limitError!);
        }

        string? statusFilter = null;
        if (input.Status is { } status)
        {
            statusFilter = BudgetRowMapper.FormatStatus(status);
        }

        var periodDetail = new BudgetPeriodDetail(
            period.Year,
            period.Month,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            period.FormatEndExclusive(),
            periodState);

        await using var connection = await store.OpenMigratedAsync(cancellationToken);

        var plan = await store.GetPlanByPeriodAsync(
            connection,
            null,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            cancellationToken);

        if (plan is null)
        {
            // Explicit No Budget Plan: success with empty items — not NotFound, empty plan, or zero plan.
            return CommandResult<ListBudgetPlanRevisionsResult>.Success(
                new ListBudgetPlanRevisionsResult([], NextCursor: null));
        }

        // Fetch one extra row to decide whether a next page exists without silent truncation.
        var rows = await store.ListRevisionSummariesAsync(
            connection,
            null,
            plan.PlanId,
            statusFilter,
            limit + 1,
            cancellationToken);

        string? nextCursor = null;
        IReadOnlyList<BudgetPlanRevisionSummaryRow> page = rows;
        if (rows.Count > limit)
        {
            page = rows.Take(limit).ToArray();
            nextCursor = rows[limit].RevisionId;
        }

        var items = page
            .Select(row => new BudgetPlanRevisionSummary(
                plan.PlanId,
                row.RevisionId,
                row.RevisionNumber,
                row.Status,
                periodDetail,
                row.CreatedAtUtc,
                row.PlannedTotalMinorUnits,
                row.EntryCount))
            .ToArray();

        return CommandResult<ListBudgetPlanRevisionsResult>.Success(
            new ListBudgetPlanRevisionsResult(items, nextCursor));
    }

    private static bool TryResolveLimit(int? requested, out int limit, out string? error)
    {
        error = null;
        if (requested is null)
        {
            limit = DefaultLimit;
            return true;
        }

        if (requested.Value is < 1 or > MaxLimit)
        {
            limit = 0;
            error = BudgetErrors.ResourceLimit;
            return false;
        }

        limit = requested.Value;
        return true;
    }

}
