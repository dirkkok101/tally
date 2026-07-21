using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Evidence;

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))]
public enum EvidenceKind
{
    [JsonStringEnumMemberName("agent_capture")]
    AgentCapture,
    [JsonStringEnumMemberName("statement_row")]
    StatementRow,
    [JsonStringEnumMemberName("receipt")]
    Receipt,
    [JsonStringEnumMemberName("external_document")]
    ExternalDocument,
    [JsonStringEnumMemberName("owner_assertion")]
    OwnerAssertion
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceLinkRole>))]
public enum EvidenceLinkRole
{
    [JsonStringEnumMemberName("supporting")]
    Supporting,
    [JsonStringEnumMemberName("confirming")]
    Confirming
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceLinkAction>))]
public enum EvidenceLinkAction
{
    [JsonStringEnumMemberName("link")]
    Link,
    [JsonStringEnumMemberName("revoke")]
    Revoke,
    [JsonStringEnumMemberName("replace")]
    Replace
}

public sealed record EvidenceObservation(
    string? AccountId,
    long? SignedAmountMinor,
    string? CurrencyCode,
    string? TransactionDate,
    string? PostingDate,
    string? InstrumentId,
    string? CardholderId,
    string? DescriptionFingerprint);

public sealed record RegisterEvidenceInput(
    [property: JsonRequired] EvidenceKind Kind,
    [property: JsonRequired] string LogicalIdentityDigest,
    string? OpaqueExternalReference,
    string? ContentFingerprint,
    EvidenceObservation? Observation);

public sealed record GetEvidenceInput([property: JsonRequired] string EvidenceId);

public sealed record EvidenceLinkHistoryItem(
    string LinkEventId,
    string TransactionId,
    EvidenceLinkRole Role,
    EvidenceLinkAction Action,
    string? DecisionId,
    string Reason,
    string RecordedBy,
    string RecordedAt,
    string? PreviousLinkEventId);

public sealed record EvidenceRecordDetail(
    string EvidenceId,
    EvidenceKind Kind,
    string LogicalIdentityDigest,
    string? OpaqueExternalReference,
    string? ContentFingerprint,
    EvidenceObservation? Observation,
    string RecordedBy,
    string RecordedAt,
    IReadOnlyList<EvidenceLinkHistoryItem> LinkHistory);
