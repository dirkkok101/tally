using System.Text.Json.Serialization;
using Tally.Contracts.Classify.Rules;

namespace Tally.Contracts.Classify.Operations;

/// <summary>Apply selection mode for preview (DM-CLASSIFY-OPERATION-CONTRACTS).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyApplySelectionMode>))]
public enum ClassifyApplySelectionMode
{
    [JsonStringEnumMemberName("selected_outcomes")]
    SelectedOutcomes,

    [JsonStringEnumMemberName("exact_rule")]
    ExactRule,

    [JsonStringEnumMemberName("explicit_corrections")]
    ExplicitCorrections
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyStatusSubjectType>))]
public enum ClassifyStatusSubjectType
{
    [JsonStringEnumMemberName("rule")]
    Rule,

    [JsonStringEnumMemberName("validation")]
    Validation,

    [JsonStringEnumMemberName("evaluation")]
    Evaluation,

    [JsonStringEnumMemberName("preview")]
    Preview,

    [JsonStringEnumMemberName("apply")]
    Apply,

    [JsonStringEnumMemberName("feedback")]
    Feedback,

    [JsonStringEnumMemberName("abandonment")]
    Abandonment,

    [JsonStringEnumMemberName("cleanup")]
    Cleanup
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyFeedbackDecision>))]
public enum ClassifyFeedbackDecision
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [JsonStringEnumMemberName("corrected")]
    Corrected
}

/// <summary>Explicit correction item — never selected by broad exact-rule mode.</summary>
public sealed record ClassifyExplicitCorrectionItem(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] string CurrentCategoryId,
    [property: JsonRequired] string TargetCategoryId,
    [property: JsonRequired] string Reason);

/// <summary>
/// Selection union for apply preview. Exactly one mode is active; mixed modes are rejected by validation.
/// </summary>
public sealed record ClassifyApplySelection(
    [property: JsonRequired] ClassifyApplySelectionMode Mode,
    IReadOnlyList<string>? OutcomeIds = null,
    string? RuleVersionId = null,
    IReadOnlyList<ClassifyExplicitCorrectionItem>? CorrectionItems = null);

public sealed record ClassifyEvaluateRequest(
    [property: JsonRequired] string ContractVersion);

public sealed record ClassifyOutcomeGetRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string TransactionId);

public sealed record ClassifyApplyPreviewRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] ClassifyApplySelection Selection);

public sealed record ClassifyApplyRunRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string ApplyId);

public sealed record ClassifyRuleSaveRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleId,
    string? PriorVersionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] IReadOnlyList<ClassificationRuleConditionInput> Conditions,
    [property: JsonRequired] string Reason);

public sealed record ClassifyRuleValidateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] IReadOnlyList<string> CandidateIds,
    [property: JsonRequired] string CorpusSource);

public sealed record ClassifyRuleActivateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string ValidationId,
    [property: JsonRequired] bool BroadApplyAllowed,
    [property: JsonRequired] string Reason);

public sealed record ClassifyRuleRetireRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] string Reason);

public sealed record ClassifyFeedbackRecordRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] ClassifyFeedbackDecision Decision,
    IReadOnlyList<string>? LedgerAllocationRefs,
    [property: JsonRequired] string Reason);

public sealed record ClassifyStatusRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId);

public sealed record ClassifyAbandonRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] string Reason);

public sealed record ClassifyCleanupRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PolicyVersion);
