using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Features.Classify.Contract;

namespace Tally.Features.Classify.Rules.Save;

/// <summary>
/// Boundary validation for classify.rule.save (TASK-CLASSIFY-RULEBOOK-RULE-DRAFT-SAVE).
/// Shape and presence only — closed field/predicate grammar is validated by
/// <see cref="Domain.Classify.Rules.ClassificationRuleVocabulary"/> in the command.
/// </summary>
public static class SaveClassificationRuleValidator
{
    public static bool TryValidate(
        ClassifyRuleSaveRequest? request,
        out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RuleId))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.CategoryId))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.NormalizationVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (!ClassifyContractMapper.IsSupportedNormalizationVersion(request.NormalizationVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (request.Conditions is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!ClassifyContractMapper.TryNormalizeReason(request.Reason, out _))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (request.PriorVersionId is not null && string.IsNullOrWhiteSpace(request.PriorVersionId))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        return true;
    }
}
