using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Contracts.Ledger.Reconciliation;

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationDecisionAction>))]
public enum ReconciliationDecisionAction
{
    [JsonStringEnumMemberName("confirm")]
    Confirm,
    [JsonStringEnumMemberName("reject")]
    Reject,
    [JsonStringEnumMemberName("revoke")]
    Revoke,
    [JsonStringEnumMemberName("replace")]
    Replace
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationDecisionDisposition>))]
public enum ReconciliationDecisionDisposition
{
    [JsonStringEnumMemberName("confirmed_existing")]
    ConfirmedExisting,
    [JsonStringEnumMemberName("corrected_from_statement")]
    CorrectedFromStatement,
    [JsonStringEnumMemberName("statement_only")]
    StatementOnly,
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,
    [JsonStringEnumMemberName("exception")]
    Exception,
    [JsonStringEnumMemberName("owner_confirmed_match")]
    OwnerConfirmedMatch,
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    [JsonStringEnumMemberName("revoked")]
    Revoked,
    [JsonStringEnumMemberName("replaced")]
    Replaced
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationDecisionCurrentState>))]
public enum ReconciliationDecisionCurrentState
{
    [JsonStringEnumMemberName("confirmed_existing")]
    ConfirmedExisting,
    [JsonStringEnumMemberName("corrected_from_statement")]
    CorrectedFromStatement,
    [JsonStringEnumMemberName("statement_only")]
    StatementOnly,
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,
    [JsonStringEnumMemberName("reconciliation_exception")]
    Exception,
    [JsonStringEnumMemberName("owner_confirmed_match")]
    OwnerConfirmedMatch,
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    [JsonStringEnumMemberName("revoked")]
    Revoked,
    [JsonStringEnumMemberName("replaced")]
    Replaced
}

public sealed record GetReconciliationDecisionInput([property: JsonRequired] string EvidenceId);

public sealed record ConfirmReconciliationDecisionInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string ExpectedDecisionId,
    [property: JsonRequired] string TargetTransactionId,
    [property: JsonRequired] ReconciliationAuthorityKind AuthorityKind,
    [property: JsonRequired] string Reason);

public sealed record RejectReconciliationDecisionInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string ExpectedDecisionId,
    [property: JsonRequired] ReconciliationAuthorityKind AuthorityKind,
    [property: JsonRequired] string Reason);

public sealed record RevokeReconciliationDecisionInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string ExpectedDecisionId,
    [property: JsonRequired] ReconciliationAuthorityKind AuthorityKind,
    [property: JsonRequired] string Reason);

public sealed record ReplaceReconciliationDecisionInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string ExpectedDecisionId,
    [property: JsonRequired] string TargetTransactionId,
    [property: JsonRequired] ReconciliationAuthorityKind AuthorityKind,
    [property: JsonRequired] string Reason);

public sealed record ReconciliationDecisionLink(
    string LinkEventId,
    string TransactionId,
    EvidenceLinkRole Role,
    EvidenceLinkAction Action,
    string DecisionId,
    string Reason,
    string Actor,
    string RecordedAt,
    string? PreviousLinkEventId,
    bool IsActive);

public sealed record ReconciliationDecisionCarryForward(
    string CorrectionId,
    string? CategoryAllocationEventId,
    string PoolAssignmentEventId,
    string AttributionEventId,
    IReadOnlyList<string> RelationshipLifecycleEventIds);

public sealed record ReconciliationDecisionHistoryItem(
    string DecisionId,
    string EvidenceId,
    string? PreviousDecisionId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    ReconciliationDecisionDisposition Disposition,
    ReconciliationAuthorityKind AuthorityKind,
    string? StatementAuthorityBasis,
    string MatchBasis,
    string Reason,
    string Actor,
    string DecidedAt,
    string? PolicyId,
    string? PolicyVersion,
    IReadOnlyList<ReconciliationDecisionLink> Links,
    ReconciliationDecisionCarryForward? CarryForward);

public sealed record ReconciliationDecisionDetail(
    string EvidenceId,
    string CurrentDecisionId,
    ReconciliationDecisionCurrentState CurrentState,
    string? ActiveTransactionId,
    string? ActiveConfirmingLinkEventId,
    bool RequiresOwnerReview,
    IReadOnlyList<ReconciliationDecisionHistoryItem> History);

public sealed record ReconciliationDecisionMutationResult(
    ReconciliationDecisionAction Action,
    string EvidenceId,
    string DecisionId,
    string? PreviousDecisionId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    string? LinkEventId,
    ReconciliationDecisionCurrentState CurrentState,
    string Reason);

public static class ReconciliationDecisionErrors
{
    public const string InvalidInput = "validation.invalid_input";
    public const string NotFound = "LEDGER-RECONCILIATION-DECISION-NOT-FOUND";
    public const string StalePredecessor = "LEDGER-RECONCILIATION-DECISION-STALE";
    public const string TransitionIncompatible = "LEDGER-RECONCILIATION-DECISION-TRANSITION-INCOMPATIBLE";
    public const string CandidateNotFound = "LEDGER-RECONCILIATION-CANDIDATE-NOT-FOUND";
    public const string CandidateInactive = "LEDGER-RECONCILIATION-CANDIDATE-INACTIVE";
    public const string CandidateIncompatible = "LEDGER-RECONCILIATION-CANDIDATE-INCOMPATIBLE";
    public const string CandidateAlreadyReconciled = "LEDGER-RECONCILIATION-CANDIDATE-ALREADY-RECONCILED";
    public const string LinkConflict = "LEDGER-RECONCILIATION-LINK-CONFLICT";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GetReconciliationDecisionInput))]
[JsonSerializable(typeof(ConfirmReconciliationDecisionInput))]
[JsonSerializable(typeof(RejectReconciliationDecisionInput))]
[JsonSerializable(typeof(RevokeReconciliationDecisionInput))]
[JsonSerializable(typeof(ReplaceReconciliationDecisionInput))]
[JsonSerializable(typeof(ReconciliationDecisionDetail))]
[JsonSerializable(typeof(ReconciliationDecisionMutationResult))]
[JsonSerializable(typeof(ReconciliationDecisionHistoryItem[]))]
[JsonSerializable(typeof(ReconciliationDecisionLink[]))]
[JsonSerializable(typeof(ReconciliationDecisionCarryForward))]
public partial class ReconciliationDecisionJsonContext : JsonSerializerContext;
