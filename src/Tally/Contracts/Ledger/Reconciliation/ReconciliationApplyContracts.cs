using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Dimensions;

namespace Tally.Contracts.Ledger.Reconciliation;

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationApplyDisposition>))]
public enum ReconciliationApplyDisposition
{
    [JsonStringEnumMemberName("match_existing")]
    MatchExisting,
    [JsonStringEnumMemberName("create_statement_only")]
    CreateStatementOnly,
    [JsonStringEnumMemberName("record_ambiguous")]
    RecordAmbiguous,
    [JsonStringEnumMemberName("record_exception")]
    RecordException,
    [JsonStringEnumMemberName("correct_existing_from_statement")]
    CorrectExistingFromStatement
}

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationAuthorityKind>))]
public enum ReconciliationAuthorityKind
{
    [JsonStringEnumMemberName("owner")]
    Owner,
    [JsonStringEnumMemberName("deterministic_policy")]
    DeterministicPolicy
}

public sealed record AuthoritativeStatementFact(
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string SignedAmount,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string TransactionDate,
    string? PostingDate,
    [property: JsonRequired] string OriginalDescription);

public sealed record ReconciliationApplyInput(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string EvidenceFingerprint,
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string ExpectedProjectionToken,
    [property: JsonRequired] ReconciliationApplyDisposition Disposition,
    [property: JsonRequired] ReconciliationAuthorityKind AuthorityKind,
    [property: JsonRequired] IReadOnlyList<string> ReviewedCandidateIds,
    string? TargetTransactionId,
    AuthoritativeStatementFact? StatementFact,
    string? ExceptionCode,
    [property: JsonRequired] string Reason);

public sealed record ReconciliationApplyResult(
    string DecisionId,
    string EvidenceId,
    string ScopeId,
    ReconciliationApplyDisposition Disposition,
    ReconciliationAuthorityKind AuthorityKind,
    string? ActiveTransactionId,
    bool CreatedStatementOnly,
    string? ConfirmingLinkEventId,
    string? ExceptionId,
    string? ExceptionCode,
    IReadOnlyList<string> ReviewedCandidateIds,
    string Reason,
    string PolicyId,
    string PolicyVersion,
    string ProjectionToken,
    StatementCorrectionApplyResult? Correction = null);

public sealed record StatementCorrectionApplyResult(
    string CorrectionId,
    string PriorTransactionId,
    string ReplacementTransactionId,
    string SupersessionLifecycleEventId,
    string? CategoryAllocationEventId,
    string PoolAssignmentEventId,
    string AttributionEventId,
    PaymentAttributionCarryForwardResolution PaymentResolution,
    IReadOnlyList<string> RelationshipLifecycleEventIds);

public static class ReconciliationApplyErrors
{
    public const string InvalidInput = "validation.invalid_input";
    public const string UnsupportedAutomaticAuthority = "LEDGER-RECONCILIATION-AUTOMATIC-UNSUPPORTED";
    public const string UnsupportedStatementCorrection = "LEDGER-RECONCILIATION-CORRECTION-UNSUPPORTED";
    public const string EvidenceFingerprintChanged = "LEDGER-RECONCILIATION-EVIDENCE-CHANGED";
    public const string ProjectionChanged = "LEDGER-RECONCILIATION-PROJECTION-CHANGED";
    public const string CandidateSetChanged = "LEDGER-RECONCILIATION-CANDIDATES-CHANGED";
    public const string TargetNotCandidate = "LEDGER-RECONCILIATION-TARGET-NOT-CANDIDATE";
    public const string ProjectionConflict = "LEDGER-RECONCILIATION-PROJECTION-CONFLICT";
    public const string DispositionIncompatible = "LEDGER-RECONCILIATION-DISPOSITION-INCOMPATIBLE";
    public const string StatementFactMismatch = "LEDGER-RECONCILIATION-STATEMENT-FACT-MISMATCH";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReconciliationApplyInput))]
[JsonSerializable(typeof(ReconciliationApplyResult))]
[JsonSerializable(typeof(StatementCorrectionApplyResult))]
[JsonSerializable(typeof(AuthoritativeStatementFact))]
public partial class ReconciliationApplyJsonContext : JsonSerializerContext;
