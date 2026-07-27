using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Periods;
using Tally.Domain.Budget.Plans;
using System.Globalization;
using Tally.Domain.Budget;
using Tally.Infrastructure.Budget.Storage;

namespace Tally.Features.Budget.Contract;

/// <summary>
/// Pure position DTO mapping for budget.position.get (DM-BUDGET-POSITION-PROJECTION).
/// No I/O, no TimeProvider, no Ledger executor calls.
/// </summary>
public static partial class BudgetContractMapper
{
    /// <summary>
    /// Maps durable revision + entry rows into the domain revision shape expected by the calculator.
    /// </summary>
    public static BudgetPlanRevision ToDomainRevision(
        BudgetPlanRevisionRow revision,
        IReadOnlyList<BudgetPlanEntryRow> entries)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(entries);

        var domainEntries = entries
            .OrderBy(e => e.CategoryId, StringComparer.Ordinal)
            .Select(e => new BudgetPlanEntry(e.CategoryId, e.PlannedMinorUnits))
            .ToArray();

        return new BudgetPlanRevision(
            revision.RevisionId,
            revision.PlanId,
            revision.RevisionNumber,
            revision.Status,
            revision.ActorKind,
            revision.ActorLabel,
            revision.ActorRunId,
            revision.Reason,
            ParseUtc(revision.CreatedAtUtc),
            revision.CategoryContractVersion,
            revision.PayloadHash,
            ParseUtcOrNull(revision.ActivatedAtUtc),
            ParseUtcOrNull(revision.SupersededAtUtc),
            revision.SupersededByRevisionId,
            domainEntries);
    }

    public static BudgetPeriodDetail ToPeriodDetail(BudgetPeriod period, BudgetPeriodState state)
    {
        return new BudgetPeriodDetail(
            period.Year,
            period.Month,
            period.CurrencyCode,
            period.FormatStartInclusive(),
            period.FormatEndExclusive(),
            state);
    }

    /// <summary>
    /// Maps a complete LEDGER actuals page set into Budget Actual members and the cited total.
    /// </summary>
    /// <returns>
    /// On success <paramref name="errorCode"/> is null; on parse/reconciliation failure
    /// members are empty and <paramref name="errorCode"/> is a stable BUDGET code.
    /// </returns>
    public static bool TryMapActualMembers(
        ActualsQueryResult actuals,
        out IReadOnlyList<BudgetActualMember> members,
        out long expectedBudgetActualTotalMinorUnits,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(actuals);
        members = [];
        expectedBudgetActualTotalMinorUnits = 0;
        errorCode = null;

        if (!BudgetMoney.TryParse(actuals.Totals.BudgetActual, out var totalMoney, out _))
        {
            errorCode = BudgetErrors.Integrity;
            return false;
        }

        expectedBudgetActualTotalMinorUnits = totalMoney.MinorUnits;
        var mapped = new List<BudgetActualMember>(actuals.Items.Count);
        long membershipSum = 0;

        try
        {
            foreach (var item in actuals.Items)
            {
                if (!BudgetMoney.TryParse(item.Contribution.BudgetActual, out var contribution, out _))
                {
                    errorCode = BudgetErrors.Integrity;
                    members = [];
                    expectedBudgetActualTotalMinorUnits = 0;
                    return false;
                }

                membershipSum = checked(membershipSum + contribution.MinorUnits);
                mapped.Add(new BudgetActualMember(
                    item.Ordinal,
                    item.TransactionId,
                    item.EffectiveDate,
                    item.CategoryId,
                    contribution.MinorUnits));
            }
        }
        catch (OverflowException)
        {
            errorCode = BudgetErrors.Integrity;
            members = [];
            expectedBudgetActualTotalMinorUnits = 0;
            return false;
        }

        if (membershipSum != expectedBudgetActualTotalMinorUnits)
        {
            errorCode = BudgetErrors.Integrity;
            members = [];
            expectedBudgetActualTotalMinorUnits = 0;
            return false;
        }

        members = mapped;
        return true;
    }

    public static LedgerSnapshotEvidence? TryMapLedgerSnapshot(ActualsQueryResult actuals, out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(actuals);
        errorCode = null;

        if (string.IsNullOrWhiteSpace(actuals.SnapshotId)
            || string.IsNullOrWhiteSpace(actuals.ExpiresAt)
            || string.IsNullOrWhiteSpace(actuals.LedgerContractVersion)
            || string.IsNullOrWhiteSpace(actuals.StoreGenerationFingerprint))
        {
            errorCode = BudgetErrors.Integrity;
            return null;
        }

        return new LedgerSnapshotEvidence(
            actuals.LedgerContractVersion,
            actuals.SnapshotId,
            actuals.ExpiresAt,
            actuals.StoreGenerationFingerprint);
    }

    /// <summary>
    /// Maps released category list items to lifecycle evidence (Active / Archived only).
    /// Unknown statuses are omitted — callers resolve individually or fail closed.
    /// </summary>
    public static IReadOnlyList<CategoryLifecycleEvidence> MapCategoryListEvidence(CategoryListResult listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var evidence = new List<CategoryLifecycleEvidence>(listed.Items.Count);
        foreach (var item in listed.Items.OrderBy(i => i.CategoryId, StringComparer.Ordinal))
        {
            var lifecycle = MapLifecycle(item.Status);
            if (lifecycle == CategoryLifecycleStatus.Unknown)
            {
                continue;
            }

            evidence.Add(new CategoryLifecycleEvidence(
                item.CategoryId,
                item.Name,
                lifecycle,
                item.LedgerContractVersion));
        }

        return evidence;
    }

    public static CategoryLifecycleEvidence MapCategoryDetailEvidence(CategoryDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new CategoryLifecycleEvidence(
            detail.CategoryId,
            detail.Name,
            MapLifecycle(detail.Status),
            detail.LedgerContractVersion);
    }

    public static CategoryLifecycleStatus MapLifecycle(CategoryStatus status) => status switch
    {
        CategoryStatus.Active => CategoryLifecycleStatus.Active,
        CategoryStatus.Archived => CategoryLifecycleStatus.Archived,
        _ => CategoryLifecycleStatus.Unknown
    };

    public static GetBudgetPositionResult ToPositionResult(
        BudgetPosition? position,
        bool hasActiveBudgetPlanRevision) =>
        new(position, hasActiveBudgetPlanRevision);

    /// <summary>
    /// Maps a public LEDGER composition failure into a stable BUDGET domain error code.
    /// Snapshot expiry / generation races become <see cref="BudgetErrors.SourceStateChanged"/>;
    /// compatibility stays <see cref="BudgetErrors.LedgerIncompatible"/>; other failures are unavailable.
    /// </summary>
    public static string MapLedgerCompositionError(ProcessError? error)
    {
        if (error is null)
        {
            return BudgetErrors.LedgerUnavailable;
        }

        if (string.Equals(error.Code, BudgetErrors.LedgerIncompatible, StringComparison.Ordinal)
            || string.Equals(error.Category, "compatibility", StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.ContractMismatch, StringComparison.Ordinal))
        {
            return BudgetErrors.LedgerIncompatible;
        }

        if (string.Equals(error.Code, BudgetErrors.Integrity, StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.Invariant, StringComparison.Ordinal))
        {
            return BudgetErrors.Integrity;
        }

        if (string.Equals(error.Code, ActualsErrors.SnapshotExpired, StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.GenerationMismatch, StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.HierarchyMismatch, StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.CursorFilterMismatch, StringComparison.Ordinal)
            || string.Equals(error.Code, ActualsErrors.SnapshotNotFound, StringComparison.Ordinal))
        {
            return BudgetErrors.SourceStateChanged;
        }

        return BudgetErrors.LedgerUnavailable;
    }

    /// <summary>
    /// True when an exception is the calculator's integrity failure (overflow, unknown category, etc.).
    /// </summary>
    public static bool IsPositionIntegrityFailure(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.StartsWith(BudgetErrors.Integrity, StringComparison.Ordinal);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? ParseUtcOrNull(string? value) =>
        value is null ? null : ParseUtc(value);
}
