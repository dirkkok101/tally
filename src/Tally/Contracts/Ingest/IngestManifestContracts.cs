using System.Text.Json.Serialization;
using Tally.Contracts.Common;

namespace Tally.Contracts.Ingest;

// DM-INGEST-IMPORT-MANIFEST

[JsonConverter(typeof(JsonStringEnumConverter<BatchStatus>))]
public enum BatchStatus
{
    [JsonStringEnumMemberName("previewed")]
    Previewed,
    [JsonStringEnumMemberName("approved")]
    Approved,
    [JsonStringEnumMemberName("committing")]
    Committing,
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("abandoned")]
    Abandoned,
    [JsonStringEnumMemberName("cleaned")]
    Cleaned
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceRecordDisposition>))]
public enum SourceRecordDisposition
{
    [JsonStringEnumMemberName("accepted_candidate")]
    AcceptedCandidate,
    [JsonStringEnumMemberName("exact_duplicate")]
    ExactDuplicate,
    [JsonStringEnumMemberName("excluded_non_transaction")]
    ExcludedNonTransaction,
    [JsonStringEnumMemberName("blocked")]
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter<ImportProvenanceKind>))]
public enum ImportProvenanceKind
{
    [JsonStringEnumMemberName("statement_import")]
    StatementImport
}

public sealed record StatementPeriod(
    [property: JsonRequired] string StartDate,
    [property: JsonRequired] string EndDate);

public sealed record ImportProvenance(
    [property: JsonRequired] ImportProvenanceKind Kind,
    [property: JsonRequired] string Reference);

public sealed record ImportBatch(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string SourceFingerprint,
    [property: JsonRequired] string SelectedAccountId,
    [property: JsonRequired] string AdapterIdentity,
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string ManifestSchemaVersion,
    [property: JsonRequired] StatementPeriod StatementPeriod,
    [property: JsonRequired] BatchStatus Status,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] string UpdatedAt);

public sealed record ManifestRevision(
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] int RevisionNumber,
    [property: JsonRequired] string CanonicalDigest,
    string? PreviousRevisionId,
    [property: JsonRequired] bool Committable,
    [property: JsonRequired] string CreatedAt);

public sealed record SourceRecordOutcome(
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string SourceRecordId,
    [property: JsonRequired] int Order,
    [property: JsonRequired] SourceRecordDisposition Disposition,
    [property: JsonRequired] string ReasonCode,
    string? CandidateId,
    string? PriorCanonicalRef);

public sealed record ImportCandidate(
    [property: JsonRequired] string CandidateId,
    [property: JsonRequired] string SourceRecordId,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] long SignedAmountMinor,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string TransactionDate,
    string? PostingDate,
    [property: JsonRequired] string OriginalDescription,
    [property: JsonRequired] string SourceReference,
    [property: JsonRequired] ImportProvenance Provenance,
    [property: JsonRequired] string LedgerIdempotencyKey,
    [property: JsonRequired] FrozenLedgerRecordRequest FrozenLedgerRequest,
    string? CrossFileIdentity);

public sealed record ManifestApproval(
    [property: JsonRequired] string ApprovalId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string ManifestDigest,
    [property: JsonRequired] SafeActor Actor,
    [property: JsonRequired] string TrustedOsIdentity,
    [property: JsonRequired] string ApprovedAt,
    [property: JsonRequired] bool Active);
