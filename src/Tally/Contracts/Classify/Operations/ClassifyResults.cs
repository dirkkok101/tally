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

public sealed record ClassifyStatusResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] bool MutationMayHaveOccurred,
    [property: JsonRequired] string NextSafeOperationId);

public sealed record ClassifyAbandonResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] bool Abandoned);

public sealed record ClassifyCleanupResult(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PolicyVersion,
    [property: JsonRequired] int RemovedTemporaryCount,
    [property: JsonRequired] int RemovedExpiredPreviewCount,
    [property: JsonRequired] int RemovedAbandonedPayloadCount);
