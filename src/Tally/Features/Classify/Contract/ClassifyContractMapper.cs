using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure domain-to-contract mapping root for CLASSIFY (DD-CLASSIFY-APPLICATION-ARCHITECTURE).
/// No I/O, no TimeProvider, no Ledger access — only pure transforms and contract predicates.
/// </summary>
public static partial class ClassifyContractMapper
{
    public static bool IsSupportedContractVersion(string? version) =>
        string.Equals(version, ClassifyOperationIds.ContractVersion, StringComparison.Ordinal);

    /// <summary>
    /// Selection union must activate exactly one mode; corrections always require complete items.
    /// </summary>
    public static bool TryValidateApplySelection(ClassifyApplySelection? selection, out string? errorCode)
    {
        errorCode = null;
        if (selection is null)
        {
            errorCode = ClassifyErrors.SelectionInvalid;
            return false;
        }

        var hasOutcomes = selection.OutcomeIds is { Count: > 0 };
        var hasRule = !string.IsNullOrWhiteSpace(selection.RuleVersionId);
        var hasCorrections = selection.CorrectionItems is { Count: > 0 };
        var modeCount = (hasOutcomes ? 1 : 0) + (hasRule ? 1 : 0) + (hasCorrections ? 1 : 0);
        if (modeCount != 1)
        {
            errorCode = ClassifyErrors.SelectionInvalid;
            return false;
        }

        return selection.Mode switch
        {
            ClassifyApplySelectionMode.SelectedOutcomes when hasOutcomes && !hasRule && !hasCorrections => true,
            ClassifyApplySelectionMode.ExactRule when hasRule && !hasOutcomes && !hasCorrections => true,
            ClassifyApplySelectionMode.ExplicitCorrections when hasCorrections && !hasOutcomes && !hasRule
                && selection.CorrectionItems!.All(IsCompleteCorrection) => true,
            _ => Fail(out errorCode)
        };

        static bool Fail(out string? code)
        {
            code = ClassifyErrors.SelectionInvalid;
            return false;
        }
    }

    public static bool IsCompleteCorrection(ClassifyExplicitCorrectionItem item) =>
        !string.IsNullOrWhiteSpace(item.TransactionId)
        && !string.IsNullOrWhiteSpace(item.OutcomeId)
        && !string.IsNullOrWhiteSpace(item.CurrentCategoryId)
        && !string.IsNullOrWhiteSpace(item.TargetCategoryId)
        && !string.IsNullOrWhiteSpace(item.Reason);

    /// <summary>Order rule conditions by ordinal then field key for deterministic wire hashes.</summary>
    public static IReadOnlyList<ClassificationRuleConditionInput> OrderConditions(
        IReadOnlyList<ClassificationRuleConditionInput> conditions) =>
        conditions
            .OrderBy(condition => condition.Ordinal)
            .ThenBy(condition => condition.FieldKey.ToString(), StringComparer.Ordinal)
            .ToArray();

    /// <summary>Inclusive-max one-over-limit check for a single applicable dimension.</summary>
    public static bool IsWithinInclusiveLimit(long value, long maxInclusive) =>
        maxInclusive == OperationLimits.NotApplicable || (value >= 0 && value <= maxInclusive);

    public static bool ExceedsAnyLimit(
        OperationLimits limits,
        long transactionCount = 0,
        long ruleCount = 0,
        long evidenceRowCount = 0,
        long corpusRowCount = 0,
        long memoryBytes = 0,
        long processingTimeMs = 0) =>
        !limits.AcceptsTransactionCount(transactionCount)
        || !limits.AcceptsRuleCount(ruleCount)
        || !limits.AcceptsEvidenceRowCount(evidenceRowCount)
        || !limits.AcceptsCorpusRowCount(corpusRowCount)
        || !limits.AcceptsMemoryBytes(memoryBytes)
        || !limits.AcceptsProcessingTimeMs(processingTimeMs);
}
