using System.Globalization;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Unresolved;
using Tally.Domain.Ledger;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure mapping for classify.unresolved.report
/// (DM-CLASSIFY-UNRESOLVED-REPORT / FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT / bd-3ciw).
/// Owner-visible groups and metadata only — never transaction IDs, raw descriptions, or paths.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string EvaluationLifecycleAbandoned = "abandoned";

    /// <summary>
    /// Map pure policy groups to the public wire shape using released classification amount directions.
    /// </summary>
    public static ClassifyUnresolvedReportResult ToUnresolvedReportResult(
        string evaluationId,
        string evaluationFingerprint,
        string projectionFingerprint,
        string categoryLifecycleFingerprint,
        string ruleSetFingerprint,
        UnresolvedPatternGroupingPolicy.Success grouped)
    {
        ArgumentNullException.ThrowIfNull(grouped);
        var groups = new ClassifyUnresolvedPatternGroup[grouped.Groups.Count];
        for (var i = 0; i < grouped.Groups.Count; i++)
        {
            var g = grouped.Groups[i];
            groups[i] = new ClassifyUnresolvedPatternGroup(
                g.Rank,
                g.NormalizedDescription,
                g.AccountId,
                MapAmountDirectionToWire(g.AmountDirection),
                g.TransactionCount,
                g.CheckedSignedAmountMinorTotal,
                g.CheckedAbsoluteAmountMinorTotal,
                g.GroupFingerprint);
        }

        return new ClassifyUnresolvedReportResult(
            ClassifyOperationIds.ContractVersion,
            evaluationId,
            evaluationFingerprint,
            projectionFingerprint,
            categoryLifecycleFingerprint,
            ruleSetFingerprint,
            grouped.NormalizationVersion,
            grouped.NoSuggestionOutcomeCount,
            grouped.JoinedRowCount,
            grouped.CandidateRowCount,
            grouped.BelowMinimumRowCount,
            grouped.DistinctGroupCount,
            grouped.ReturnedGroupCount,
            grouped.OmittedGroupCount,
            grouped.BoundedRequestTopN,
            grouped.BoundedRequestMinimumCount,
            grouped.ReportFingerprint,
            groups);
    }

    /// <summary>
    /// Fresh projection fingerprint over contract versions, generation, snapshot, catalogue,
    /// and ordered item lifecycle identity — never raw descriptions or amounts alone.
    /// </summary>
    public static string ComputeUnresolvedProjectionFingerprint(
        string ledgerContractVersion,
        string projectionVersion,
        string storeGenerationFingerprint,
        string snapshotId,
        string categoryLifecycleFingerprint,
        string orderedItemsFingerprint) =>
        CanonicalClassificationHasher.HashParts(
            ledgerContractVersion,
            projectionVersion,
            storeGenerationFingerprint,
            snapshotId,
            categoryLifecycleFingerprint,
            orderedItemsFingerprint);

    /// <summary>
    /// Map classification_v1 amount direction to policy/wire closed vocabulary (expense/income/zero).
    /// </summary>
    public static string FormatUnresolvedAmountDirection(ClassificationAmountDirection direction) =>
        direction switch
        {
            ClassificationAmountDirection.Expense => UnresolvedPatternGroupingPolicy.AmountDirections.Expense,
            ClassificationAmountDirection.Income => UnresolvedPatternGroupingPolicy.AmountDirections.Income,
            ClassificationAmountDirection.Zero => UnresolvedPatternGroupingPolicy.AmountDirections.Zero,
            _ => UnresolvedPatternGroupingPolicy.AmountDirections.Zero
        };

    public static ClassificationAmountDirection MapAmountDirectionToWire(string direction) =>
        direction switch
        {
            UnresolvedPatternGroupingPolicy.AmountDirections.Expense => ClassificationAmountDirection.Expense,
            UnresolvedPatternGroupingPolicy.AmountDirections.Income => ClassificationAmountDirection.Income,
            UnresolvedPatternGroupingPolicy.AmountDirections.Zero => ClassificationAmountDirection.Zero,
            // Accept closed rule vocabulary aliases if present in pure policy output.
            "outflow" => ClassificationAmountDirection.Expense,
            "inflow" => ClassificationAmountDirection.Income,
            _ => ClassificationAmountDirection.Zero
        };

    /// <summary>
    /// Parse signed public amount to checked minor units for unresolved aggregation.
    /// </summary>
    public static bool TryMapSignedAmountMinor(
        ClassificationProjectionItem item,
        out long signedMinor,
        out string? errorCode)
    {
        signedMinor = 0;
        errorCode = null;
        if (!Money.TryParse(item.SignedAmount, out var money, out _))
        {
            errorCode = ClassifyErrors.LedgerIncompatible;
            return false;
        }

        signedMinor = money.MinorUnits;
        return true;
    }

    public static string FormatUtcInvariant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
