using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Reconciliation;

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationProjectionOutcome>))]
public enum ReconciliationProjectionOutcome
{
    [JsonStringEnumMemberName("no_candidate")]
    NoCandidate,
    [JsonStringEnumMemberName("unique_candidate")]
    UniqueCandidate,
    [JsonStringEnumMemberName("guard_only")]
    GuardOnly,
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,
    [JsonStringEnumMemberName("conflict")]
    Conflict
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationCandidateKind>))]
public enum ReconciliationCandidateKind
{
    [JsonStringEnumMemberName("exact")]
    Exact,
    [JsonStringEnumMemberName("guard")]
    Guard
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationCandidateReason>))]
public enum ReconciliationCandidateReason
{
    [JsonStringEnumMemberName("exact_compatible")]
    ExactCompatible,
    [JsonStringEnumMemberName("signed_amount_differs")]
    SignedAmountDiffers,
    [JsonStringEnumMemberName("effective_date_differs")]
    EffectiveDateDiffers
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationExclusionReason>))]
public enum ReconciliationExclusionReason
{
    [JsonStringEnumMemberName("wrong_account")]
    WrongAccount,
    [JsonStringEnumMemberName("outside_statement_scope")]
    OutsideStatementScope,
    [JsonStringEnumMemberName("inactive_transaction")]
    InactiveTransaction,
    [JsonStringEnumMemberName("already_reconciled")]
    AlreadyReconciled,
    [JsonStringEnumMemberName("active_statement_confirmation")]
    ActiveStatementConfirmation,
    [JsonStringEnumMemberName("currency_conflict")]
    CurrencyConflict
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationProjectionConflictReason>))]
public enum ReconciliationProjectionConflictReason
{
    [JsonStringEnumMemberName("evidence_already_confirmed")]
    EvidenceAlreadyConfirmed,
    [JsonStringEnumMemberName("evidence_has_current_decision")]
    EvidenceHasCurrentDecision
}

public sealed record GetReconciliationCandidatesInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string PolicyId,
    [property: JsonRequired] string PolicyVersion);

public sealed record ReconciliationComparisonBasis(
    bool AccountMatches,
    bool CurrencyMatches,
    bool SignedAmountMatches,
    bool EffectiveDateMatches);

public sealed record ReconciliationProjectionCandidate(
    string TransactionId,
    ReconciliationCandidateKind Kind,
    ReconciliationComparisonBasis Basis,
    IReadOnlyList<ReconciliationCandidateReason> Reasons);

public sealed record ReconciliationProjectionExclusion(ReconciliationExclusionReason Reason, int Count);

public sealed record ReconciliationProjectionConflict(ReconciliationProjectionConflictReason Reason);

public sealed record ReconciliationProjectionResult(
    string EvidenceId,
    string EvidenceFingerprint,
    string ScopeId,
    string PolicyId,
    string PolicyVersion,
    ReconciliationProjectionOutcome Outcome,
    IReadOnlyList<ReconciliationProjectionCandidate> ExactCandidates,
    IReadOnlyList<ReconciliationProjectionCandidate> GuardCandidates,
    IReadOnlyList<ReconciliationProjectionExclusion> Exclusions,
    IReadOnlyList<ReconciliationProjectionConflict> Conflicts,
    string AdvisoryToken,
    bool AdvisoryOnly,
    bool GrantsAutomaticAuthority);

public static class ReconciliationProjectionErrors
{
    public const string InvalidInput = "validation.invalid_input";
    public const string EvidenceNotFound = "LEDGER-RECONCILIATION-EVIDENCE-NOT-FOUND";
    public const string StatementEvidenceRequired = "LEDGER-RECONCILIATION-STATEMENT-EVIDENCE-REQUIRED";
    public const string IncompleteObservation = "LEDGER-RECONCILIATION-OBSERVATION-INCOMPLETE";
    public const string ScopeNotFound = "LEDGER-RECONCILIATION-SCOPE-NOT-FOUND";
    public const string ScopeConflict = "LEDGER-RECONCILIATION-SCOPE-CONFLICT";
    public const string ScopeInactive = "LEDGER-RECONCILIATION-SCOPE-INACTIVE";
    public const string UnsupportedPolicy = "LEDGER-RECONCILIATION-POLICY-UNSUPPORTED";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GetReconciliationCandidatesInput))]
[JsonSerializable(typeof(ReconciliationProjectionResult))]
[JsonSerializable(typeof(ReconciliationProjectionCandidate[]))]
[JsonSerializable(typeof(ReconciliationProjectionExclusion[]))]
[JsonSerializable(typeof(ReconciliationProjectionConflict[]))]
public partial class ReconciliationProjectionJsonContext : JsonSerializerContext;
