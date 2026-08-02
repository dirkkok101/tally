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

/// <summary>
/// Public classify.rule.validate input. Optional owner-gate finalization fields apply only on the
/// hold-out run: production reloads stored representative + independent-replay + this hold-out
/// validation, derives and persists a trusted aggregate receipt, and returns receipt identity.
/// Never accepts a caller-supplied authority boolean or receipt body.
/// </summary>
public sealed record ClassifyRuleValidateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] IReadOnlyList<string> CandidateIds,
    [property: JsonRequired] string CorpusSource,
    string? RepresentativeValidationId = null,
    string? IndependentReplayValidationId = null,
    int? OwnerDecisionCountBefore = null,
    int? OwnerDecisionCountAfter = null,
    double? OwnerMinutesBefore = null,
    double? OwnerMinutesAfter = null,
    string? ExplicitBenefitDecision = null);

/// <summary>
/// Public classify.rule.activate input. Requires a trusted persisted owner-rulebook gate receipt ID.
/// Never accepts a caller-supplied receipt body or authority boolean.
/// </summary>
public sealed record ClassifyRuleActivateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string ValidationId,
    [property: JsonRequired] string OwnerRulebookGateReceiptId,
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

// ── Operator ergonomics additive contracts (PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1) ──
// Contract-only shapes for outcome.list, rule.list, rule-set.active.get, corpus.build,
// and unresolved.report. Descriptors/handlers are owned by later beads; inventory remains C12.

/// <summary>Closed stale filter for classify.outcome.list (DM-CLASSIFY-OUTCOME-PAGE).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyOutcomeStaleFilter>))]
public enum ClassifyOutcomeStaleFilter
{
    [JsonStringEnumMemberName("any")]
    Any,

    [JsonStringEnumMemberName("fresh")]
    Fresh,

    [JsonStringEnumMemberName("stale")]
    Stale
}

/// <summary>Closed rule lifecycle filter for classify.rule.list (DM-CLASSIFY-RULE-DISCOVERY).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyRuleLifecycleFilter>))]
public enum ClassifyRuleLifecycleFilter
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("retired")]
    Retired,

    [JsonStringEnumMemberName("superseded")]
    Superseded
}

/// <summary>Closed provenance enum for rule discovery items (DM-CLASSIFY-RULE-DISCOVERY).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyRuleProvenanceKind>))]
public enum ClassifyRuleProvenanceKind
{
    [JsonStringEnumMemberName("owner_authored")]
    OwnerAuthored,

    [JsonStringEnumMemberName("feedback_derived")]
    FeedbackDerived
}

/// <summary>Closed category lifecycle enum for discovery surfaces (no free-text).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyCategoryLifecycleState>))]
public enum ClassifyCategoryLifecycleState
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("archived")]
    Archived
}

/// <summary>
/// classify.outcome.list request (DM-CLASSIFY-OUTCOME-PAGE / FR-CLASSIFY-OUTCOME-DISCOVERY).
/// pageSize is 1..500; continuation is opaque and snapshot-bound.
/// </summary>
public sealed record ClassifyOutcomeListRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] int PageSize,
    ClassifyOutcomeKind? OutcomeKind = null,
    string? SuggestedCategoryId = null,
    string? ContributingRuleVersionId = null,
    ClassifyOutcomeStaleFilter? StaleState = null,
    string? TransactionId = null,
    string? Continuation = null);

/// <summary>
/// classify.rule.list request (DM-CLASSIFY-RULE-DISCOVERY / FR-CLASSIFY-RULEBOOK-DISCOVERY).
/// pageSize is 1..500; continuation is opaque and high-water bound.
/// </summary>
public sealed record ClassifyRuleListRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] int PageSize,
    string? LogicalRuleId = null,
    ClassifyRuleLifecycleFilter? Lifecycle = null,
    string? CategoryId = null,
    bool? ActiveMembership = null,
    string? Continuation = null);

/// <summary>
/// classify.rule-set.active.get request — no caller-supplied authority flag.
/// </summary>
public sealed record ClassifyRuleSetActiveGetRequest(
    [property: JsonRequired] string ContractVersion);

/// <summary>
/// One explicit owner label for classify.corpus.build
/// (DM-CLASSIFY-PRIVATE-CORPUS-BUILD / FR-CLASSIFY-PRIVATE-CORPUS-BUILDER).
/// expectedCategoryId is required when the outcome kind requires a category.
/// </summary>
public sealed record ClassifyCorpusBuildLabel(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] ClassifyOutcomeKind ExpectedOutcome,
    string? ExpectedCategoryId = null);

/// <summary>
/// One projection member required to bind a corpus row. Owner supplies a fresh complete
/// classification_v1 evaluation projection; never invents labels or rows.
/// </summary>
public sealed record ClassifyCorpusBuildProjectionItem(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string SourceDescription,
    string? AmountDirection,
    [property: JsonRequired] long AmountAbsoluteMinor,
    [property: JsonRequired] string ItemLifecycleFingerprint);

/// <summary>
/// Complete fresh classification_v1 projection envelope for corpus.build.
/// Bound into the request for exact matching; never returned on the public receipt.
/// </summary>
public sealed record ClassifyCorpusBuildProjectionEnvelope(
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string ProjectionVersion,
    [property: JsonRequired] string StoreGenerationFingerprint,
    [property: JsonRequired] string SnapshotId,
    [property: JsonRequired] string SnapshotExpiresAt,
    [property: JsonRequired] string CategoryLifecycleFingerprint,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] IReadOnlyList<ClassifyCorpusBuildProjectionItem> Items);

/// <summary>
/// classify.corpus.build request. Idempotent; labels 1..10000; absolute outputPath required.
/// Aggregate receipt never echoes path or row data.
/// </summary>
public sealed record ClassifyCorpusBuildRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string OutputPath,
    [property: JsonRequired] ClassifyCorpusBuildProjectionEnvelope Projection,
    [property: JsonRequired] IReadOnlyList<ClassifyCorpusBuildLabel> Labels);

/// <summary>
/// classify.unresolved.report request (DM-CLASSIFY-UNRESOLVED-REPORT).
/// topN 1..500; minimumCount 2..500; optional account/direction filters.
/// </summary>
public sealed record ClassifyUnresolvedReportRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] int TopN,
    [property: JsonRequired] int MinimumCount,
    string? AccountId = null,
    ClassificationAmountDirectionValue? AmountDirection = null);
