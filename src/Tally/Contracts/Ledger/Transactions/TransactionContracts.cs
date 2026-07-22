using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Contracts.Ledger.Transactions;

[JsonConverter(typeof(JsonStringEnumConverter<TransactionLifecycleStatus>))]
public enum TransactionLifecycleStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("voided")]
    Voided,
    [JsonStringEnumMemberName("superseded")]
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionReconciliationState>))]
public enum TransactionReconciliationState
{
    [JsonStringEnumMemberName("recorded_unreconciled")]
    RecordedUnreconciled,
    [JsonStringEnumMemberName("statement_reconciled")]
    StatementReconciled,
    [JsonStringEnumMemberName("statement_only")]
    StatementOnly,
    [JsonStringEnumMemberName("recorded_absent_from_statement")]
    RecordedAbsentFromStatement,
    [JsonStringEnumMemberName("ambiguous_match")]
    AmbiguousMatch,
    [JsonStringEnumMemberName("owner_confirmed_match")]
    OwnerConfirmedMatch,
    [JsonStringEnumMemberName("reconciliation_exception")]
    ReconciliationException
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionKnowledgeState>))]
public enum TransactionKnowledgeState
{
    [JsonStringEnumMemberName("known")]
    Known,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionPoolState>))]
public enum TransactionPoolState
{
    [JsonStringEnumMemberName("assigned")]
    Assigned,
    [JsonStringEnumMemberName("unassigned")]
    Unassigned
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionCategoryState>))]
public enum TransactionCategoryState
{
    [JsonStringEnumMemberName("categorized")]
    Categorized,
    [JsonStringEnumMemberName("uncategorized")]
    Uncategorized
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionLifecycleAction>))]
public enum TransactionLifecycleAction
{
    [JsonStringEnumMemberName("void")]
    Void,
    [JsonStringEnumMemberName("superseded")]
    Superseded,
    [JsonStringEnumMemberName("statement_authoritative_replacement")]
    StatementAuthoritativeReplacement
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionAssignmentAction>))]
public enum TransactionAssignmentAction
{
    [JsonStringEnumMemberName("initialize")]
    Initialize,
    [JsonStringEnumMemberName("assign")]
    Assign,
    [JsonStringEnumMemberName("correct")]
    Correct,
    [JsonStringEnumMemberName("carry_forward")]
    CarryForward
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionCategoryAction>))]
public enum TransactionCategoryAction
{
    [JsonStringEnumMemberName("assign")]
    Assign,
    [JsonStringEnumMemberName("correct")]
    Correct,
    [JsonStringEnumMemberName("carry_forward")]
    CarryForward
}

public sealed record RecordTransactionInput(
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string SignedAmount,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string TransactionDate,
    string? PostingDate,
    [property: JsonRequired] string OriginalDescription,
    string? InstrumentId,
    string? CardholderId,
    [property: JsonRequired] RegisterEvidenceInput InitialEvidence);

public sealed record GetTransactionInput([property: JsonRequired] string TransactionId, bool IncludeHistory = false);

public sealed record TransactionPaymentAttribution(
    string AttributionEventId,
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId);

public sealed record TransactionPoolAssignment(TransactionPoolState State, string? PoolId);
public sealed record TransactionCategoryAssignment(TransactionCategoryState State, string? AllocationEventId, string? CategoryId, IReadOnlyList<string> CurrentAncestryIds);

public sealed record TransactionEvidenceDetail(
    string EvidenceId,
    EvidenceKind Kind,
    string LogicalIdentityDigest,
    string? OpaqueExternalReference,
    string? ContentFingerprint,
    EvidenceObservation? Observation,
    EvidenceLinkRole Role,
    string LinkEventId,
    string RecordedBy,
    string RecordedAt);

public sealed record TransactionLifecycleHistoryItem(
    string LifecycleEventId,
    TransactionLifecycleAction Action,
    string? ReplacementTransactionId,
    string? ReconciliationDecisionId,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record TransactionAttributionHistoryItem(
    string AttributionEventId,
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId,
    TransactionAssignmentAction Action,
    string? PreviousEventId,
    string? SourceTransactionId,
    string? ReconciliationDecisionId,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record TransactionPoolHistoryItem(
    string PoolAssignmentEventId,
    TransactionPoolState State,
    string? PoolId,
    TransactionAssignmentAction Action,
    string? PreviousEventId,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record TransactionCategoryHistoryItem(
    string AllocationEventId,
    string CategoryId,
    TransactionCategoryAction Action,
    string? PreviousEventId,
    string? SourceTransactionId,
    string? ReconciliationDecisionId,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record TransactionHistory(
    IReadOnlyList<TransactionLifecycleHistoryItem> Lifecycle,
    IReadOnlyList<TransactionAttributionHistoryItem> PaymentAttribution,
    IReadOnlyList<TransactionPoolHistoryItem> PoolAssignments,
    IReadOnlyList<TransactionCategoryHistoryItem> CategoryAssignments);

public sealed record TransactionDetail(
    string TransactionId,
    string AccountId,
    string SignedAmount,
    string CurrencyCode,
    string TransactionDate,
    string? PostingDate,
    string EffectiveDate,
    string OriginalDescription,
    TransactionLifecycleStatus LifecycleStatus,
    string? ActiveReplacementTransactionId,
    TransactionReconciliationState ReconciliationState,
    TransactionCategoryAssignment Category,
    TransactionPoolAssignment Pool,
    TransactionPaymentAttribution PaymentAttribution,
    IReadOnlyList<TransactionEvidenceDetail> Evidence,
    string RecordedByOsIdentity,
    string RecordedAt,
    TransactionHistory? History);
