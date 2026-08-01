using System.Text.Json.Serialization;

namespace Tally.Contracts.Classify.Operations;

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyOutcomeKind>))]
public enum ClassifyOutcomeKind
{
    [JsonStringEnumMemberName("suggestion")]
    Suggestion,

    [JsonStringEnumMemberName("no_suggestion")]
    NoSuggestion,

    [JsonStringEnumMemberName("conflict")]
    Conflict,

    [JsonStringEnumMemberName("stale")]
    Stale
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyApplyItemResultKind>))]
public enum ClassifyApplyItemResultKind
{
    [JsonStringEnumMemberName("applied")]
    Applied,

    [JsonStringEnumMemberName("already_applied")]
    AlreadyApplied,

    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("unresolved")]
    Unresolved
}

public sealed record ClassifyEvaluateResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string RuleSetVersionId,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] string ProjectionFingerprint,
    [property: JsonRequired] int TotalCount,
    [property: JsonRequired] int SuggestionCount,
    [property: JsonRequired] int NoSuggestionCount,
    [property: JsonRequired] int ConflictCount,
    [property: JsonRequired] int StaleCount);

/// <summary>
/// Ordered conflict proposal: immutable rule version named by retained MatchEvidence
/// and its stored proposed category id — never reconstructed from current evaluation.
/// </summary>
public sealed record ClassifyConflictRuleProposal(
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] string ProposedCategoryId);

/// <summary>
/// Bounded public classify.outcome.get explanation
/// (FR-CLASSIFY-OUTCOME-EXPLANATION / FR-CLASSIFY-OUTCOME-INVALIDATION).
/// Never carries predicate values, normalized hashes, raw descriptions, or full requests.
/// </summary>
public sealed record ClassifyOutcomeGetResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] ClassifyOutcomeKind Kind,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] string RuleSetVersionId,
    [property: JsonRequired] string SafeReason,
    string? SuggestedCategoryId,
    string? SuggestedCategoryDisplayName,
    IReadOnlyList<string>? ContributingRuleVersionIds,
    IReadOnlyList<string>? MatchedFieldKeys,
    IReadOnlyList<ClassifyConflictRuleProposal>? ConflictProposals,
    [property: JsonRequired] bool IsStale,
    IReadOnlyList<string>? StaleDimensions,
    /// <summary>
    /// Sole permitted next operation when stale/conflict/no-suggestion: <c>classify.evaluate</c>.
    /// Null for a fresh non-stale suggestion.
    /// </summary>
    string? PermittedNextOperationId);

public sealed record ClassifyApplyPreviewResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string ExpiresAt,
    [property: JsonRequired] int SelectedCount,
    [property: JsonRequired] int AssignableCount,
    [property: JsonRequired] int CorrectableCount,
    [property: JsonRequired] string SelectionHash);

public sealed record ClassifyApplyItemResult(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] ClassifyApplyItemResultKind Kind,
    string? LedgerErrorCode,
    string? AllocationEventId);

public sealed record ClassifyApplyRunResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string ApplyId,
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] IReadOnlyList<ClassifyApplyItemResult> Items,
    [property: JsonRequired] int AppliedCount,
    [property: JsonRequired] int AlreadyAppliedCount,
    [property: JsonRequired] int RejectedCount,
    [property: JsonRequired] int FailedCount,
    [property: JsonRequired] int UnresolvedCount);

public sealed record ClassifyRuleSaveResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleId,
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string NormalizationVersion);

/// <summary>
/// Complete aggregate-only classify.rule.validate result for pre-authority gates.
/// Includes fingerprints, exact counters, canaries, and deterministic outcomes hash —
/// never private paths, descriptions, tokens, amounts, or raw rows.
/// </summary>
public sealed record ClassifyRuleValidateResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string ValidationId,
    [property: JsonRequired] string CandidateFingerprint,
    [property: JsonRequired] string CorpusFingerprint,
    [property: JsonRequired] string ExpectedOutcomeFingerprint,
    [property: JsonRequired] string ProjectionVersion,
    [property: JsonRequired] string SnapshotId,
    [property: JsonRequired] string SnapshotExpiresAt,
    [property: JsonRequired] string StoreGenerationFingerprint,
    [property: JsonRequired] string CategoryLifecycleFingerprint,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] string ReportFingerprint,
    [property: JsonRequired] string OutcomesCanonicalHash,
    [property: JsonRequired] int TotalRows,
    [property: JsonRequired] int AccountedRows,
    [property: JsonRequired] int SuggestionCount,
    [property: JsonRequired] int NoSuggestionCount,
    [property: JsonRequired] int ConflictCount,
    [property: JsonRequired] int StaleCount,
    [property: JsonRequired] int CoverageBasisPoints,
    [property: JsonRequired] int DriftCanaryCount,
    [property: JsonRequired] int IncorrectApplicationCanaries,
    [property: JsonRequired] int UnexplainedConflictCount,
    [property: JsonRequired] bool ActivationEligible,
    string? OwnerRulebookGateReceiptId = null,
    string? OwnerRulebookGateReceiptFingerprint = null);

public sealed record ClassifyRuleActivateResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleSetVersionId,
    [property: JsonRequired] string ValidationId,
    [property: JsonRequired] bool BroadApplyAllowed,
    string? OwnerRulebookGateReceiptId = null,
    string? OwnerRulebookGateReceiptFingerprint = null);

public sealed record ClassifyRuleRetireResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RetiredRuleVersionId,
    [property: JsonRequired] string SuccessorRuleSetVersionId);

public sealed record ClassifyFeedbackRecordResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string FeedbackId,
    [property: JsonRequired] string OutcomeId,
    string? ProposalId);

/// <summary>
/// classify.status envelope (FR-CLASSIFY-STATUS-HISTORY).
/// Common lifecycle / mutation / next-action fields plus exactly one typed bounded detail
/// matching <see cref="SubjectType"/>; all other detail properties are null.
/// Never embeds free-text reasons, descriptions, amounts, paths, tokens, or raw payloads.
/// </summary>
public sealed record ClassifyStatusResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] bool MutationMayHaveOccurred,
    [property: JsonRequired] string NextSafeOperationId,
    ClassifyRuleStatusDetail? Rule = null,
    ClassifyValidationStatusDetail? Validation = null,
    ClassifyEvaluationStatusDetail? Evaluation = null,
    ClassifyPreviewStatusDetail? Preview = null,
    ClassifyApplyStatusDetail? Apply = null,
    ClassifyFeedbackStatusDetail? Feedback = null,
    ClassifyAbandonmentStatusDetail? Abandonment = null,
    ClassifyCleanupStatusDetail? Cleanup = null);

/// <summary>One rule version entry in bounded rule history (no owner free-text reason).</summary>
public sealed record ClassifyRuleStatusVersion(
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] IReadOnlyList<string> RuleSetVersionIds,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] string CreatedAt,
    string? PriorRuleVersionId,
    [property: JsonRequired] IReadOnlyList<string> SuccessorRuleVersionIds);

/// <summary>Rule subject detail: active pointer + ordered versions for the stable rule identity.</summary>
public sealed record ClassifyRuleStatusDetail(
    string? ActiveRuleSetVersionId,
    [property: JsonRequired] string RuleId,
    [property: JsonRequired] IReadOnlyList<ClassifyRuleStatusVersion> Versions);

/// <summary>Validation subject detail from durable run + aggregate report only.</summary>
public sealed record ClassifyValidationStatusDetail(
    [property: JsonRequired] string CandidateFingerprint,
    [property: JsonRequired] string CorpusFingerprint,
    [property: JsonRequired] string ExpectedOutcomeFingerprint,
    string? ReportFingerprint,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] int TotalRows,
    [property: JsonRequired] int AccountedRows,
    [property: JsonRequired] int SuggestionCount,
    [property: JsonRequired] int NoSuggestionCount,
    [property: JsonRequired] int ConflictCount,
    [property: JsonRequired] int StaleCount,
    bool? ActivationEligible,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string StartedAt,
    string? CompletedAt,
    [property: JsonRequired] string StalenessState);

/// <summary>Evaluation subject detail from durable run metadata only (no corpus/Ledger reread).</summary>
public sealed record ClassifyEvaluationStatusDetail(
    [property: JsonRequired] string EvaluationFingerprint,
    [property: JsonRequired] string RuleSetVersionId,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] int InputCount,
    [property: JsonRequired] int SuggestionCount,
    [property: JsonRequired] int NoSuggestionCount,
    [property: JsonRequired] int ConflictCount,
    [property: JsonRequired] int StaleCount,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] string StalenessState);

/// <summary>Preview subject detail — authorization metadata and aggregate counts only.</summary>
public sealed record ClassifyPreviewStatusDetail(
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string EvaluationFingerprint,
    [property: JsonRequired] string SelectionHash,
    [property: JsonRequired] int SelectedCount,
    [property: JsonRequired] int ExclusionCount,
    [property: JsonRequired] int NoSuggestionCount,
    [property: JsonRequired] int ConflictCount,
    [property: JsonRequired] string ExpiresAt,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string CreatedAt);

/// <summary>Apply subject detail — authorized fingerprint, terminal totals, frontier, safety flags.</summary>
public sealed record ClassifyApplyStatusDetail(
    [property: JsonRequired] string ApplyId,
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string RequestFingerprint,
    [property: JsonRequired] int AppliedCount,
    [property: JsonRequired] int AlreadyAppliedCount,
    [property: JsonRequired] int RejectedCount,
    [property: JsonRequired] int FailedCount,
    [property: JsonRequired] int UnresolvedCount,
    [property: JsonRequired] int UnresolvedFrontier,
    [property: JsonRequired] bool ReplaySafe,
    [property: JsonRequired] bool ResumeSafe,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string StartedAt,
    string? CompletedAt);

/// <summary>Feedback subject detail — no free-text reason, transaction IDs, or reconstructed evidence.</summary>
public sealed record ClassifyFeedbackStatusDetail(
    [property: JsonRequired] string FeedbackId,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] string DecisionType,
    string? ProposalId,
    string? ProposalLifecycleState,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] string OccurredAt,
    [property: JsonRequired] IReadOnlyList<string> RuleVersionIds);

/// <summary>Abandonment tombstone aggregate metadata only.</summary>
public sealed record ClassifyAbandonmentStatusDetail(
    [property: JsonRequired] string TombstoneId,
    [property: JsonRequired] string AbandonedSubjectType,
    [property: JsonRequired] string AbandonedSubjectId,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] string AbandonedAt,
    [property: JsonRequired] int RemovedPayloadCount);

/// <summary>Cleanup event aggregate/per-kind counts — no artifact names or paths.</summary>
public sealed record ClassifyCleanupStatusDetail(
    [property: JsonRequired] string CleanupId,
    [property: JsonRequired] string PolicyVersion,
    [property: JsonRequired] string ActorId,
    [property: JsonRequired] string OccurredAt,
    [property: JsonRequired] int RemovedArtifactCount,
    [property: JsonRequired] int RetainedArtifactCount,
    [property: JsonRequired] int RecognizedRemovedCount,
    [property: JsonRequired] int ExpiredPreviewCount,
    [property: JsonRequired] int AbandonedPayloadCount);

public sealed record ClassifyAbandonResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] bool Abandoned);

/// <summary>
/// Metadata-only cleanup receipt (FR-CLASSIFY-STATE-RETENTION-CLEANUP).
/// Exposes cleanup identity, policy, aggregate removed/retained counts, and per-kind counts —
/// never paths, file names, subjects, or private payload.
/// </summary>
public sealed record ClassifyCleanupResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string CleanupId,
    [property: JsonRequired] string PolicyVersion,
    /// <summary>Total recognized artifacts removed across all kinds.</summary>
    [property: JsonRequired] int RemovedArtifactCount,
    /// <summary>Recognized CLASSIFY artifacts retained after cleanup.</summary>
    [property: JsonRequired] int RetainedArtifactCount,
    [property: JsonRequired] int RemovedTemporaryCount,
    [property: JsonRequired] int RemovedExpiredPreviewCount,
    [property: JsonRequired] int RemovedAbandonedPayloadCount);
