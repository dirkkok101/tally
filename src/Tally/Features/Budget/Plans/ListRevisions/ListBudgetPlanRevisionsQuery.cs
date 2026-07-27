using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
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
        var rows = await ListRevisionSummariesAsync(
            connection,
            plan.PlanId,
            statusFilter,
            limit + 1,
            cancellationToken);

        string? nextCursor = null;
        IReadOnlyList<RevisionSummaryRow> page = rows;
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

    private static async Task<IReadOnlyList<RevisionSummaryRow>> ListRevisionSummariesAsync(
        SqliteConnection connection,
        string planId,
        string? statusFilter,
        int fetchLimit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                r.revision_id,
                r.revision_number,
                r.status,
                r.created_at_utc,
                COALESCE(SUM(e.planned_minor_units), 0) AS planned_total,
                COUNT(e.category_id) AS entry_count
            FROM budget_plan_revision r
            LEFT JOIN budget_plan_entry e ON e.revision_id = r.revision_id
            WHERE r.plan_id = $plan_id
              AND ($status IS NULL OR r.status = $status)
            GROUP BY
                r.revision_id,
                r.revision_number,
                r.status,
                r.created_at_utc
            ORDER BY r.created_at_utc ASC, r.revision_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$plan_id", planId);
        command.Parameters.AddWithValue("$status", (object?)statusFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", fetchLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<RevisionSummaryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RevisionSummaryRow(
                reader.GetString(0),
                reader.GetInt32(1),
                BudgetRowMapper.ParseStatus(reader.GetString(2)),
                reader.GetString(3),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private sealed record RevisionSummaryRow(
        string RevisionId,
        int RevisionNumber,
        BudgetRevisionStatus Status,
        string CreatedAtUtc,
        long PlannedTotalMinorUnits,
        int EntryCount);
}
