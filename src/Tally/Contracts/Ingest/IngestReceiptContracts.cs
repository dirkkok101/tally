using System.Text.Json.Serialization;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Contracts.Ingest;

// DM-INGEST-IMPORT-RECEIPT

[JsonConverter(typeof(JsonStringEnumConverter<ImportReceiptStatus>))]
public enum ImportReceiptStatus
{
    [JsonStringEnumMemberName("approved")]
    Approved,
    [JsonStringEnumMemberName("committing")]
    Committing,
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("abandoned")]
    Abandoned
}

[JsonConverter(typeof(JsonStringEnumConverter<CandidateReceiptState>))]
public enum CandidateReceiptState
{
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("attempting")]
    Attempting,
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    [JsonStringEnumMemberName("exact_duplicate")]
    ExactDuplicate,
    [JsonStringEnumMemberName("conflicted")]
    Conflicted,
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    [JsonStringEnumMemberName("unresolved")]
    Unresolved
}

public sealed record ImportReceiptCounts(
    [property: JsonRequired] int Pending,
    [property: JsonRequired] int Attempting,
    [property: JsonRequired] int Accepted,
    [property: JsonRequired] int ExactDuplicates,
    [property: JsonRequired] int Conflicted,
    [property: JsonRequired] int Rejected,
    [property: JsonRequired] int Unresolved);

public sealed record CandidateReceipt(
    [property: JsonRequired] string CandidateId,
    [property: JsonRequired] CandidateReceiptState State,
    [property: JsonRequired] int AttemptNumber,
    [property: JsonRequired] string LedgerOperationId,
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string IdempotencyKey,
    string? LedgerTransactionId,
    string? StableErrorCode,
    [property: JsonRequired] IngestRetryAction RetryDisposition,
    string? AttemptedAt,
    string? TerminalAt);

public sealed record ImportReceipt(
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] ImportReceiptStatus Status,
    [property: JsonRequired] ImportReceiptCounts Counts,
    [property: JsonRequired] IReadOnlyList<string> UnresolvedCandidateIds,
    [property: JsonRequired] IReadOnlyList<CandidateReceipt> CandidateOutcomes,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] string UpdatedAt,
    string? CompletedAt);

/// <summary>
/// Completed compaction removes descriptions, amounts, balances, record text, controls, and request payloads.
/// </summary>
public sealed record CompletedMetadataReceipt(
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string SourceFingerprint,
    [property: JsonRequired] string SelectedAccountId,
    [property: JsonRequired] string AdapterIdentity,
    [property: JsonRequired] IngestVersions Versions,
    [property: JsonRequired] IngestOutcomeCounts OutcomeCounts,
    [property: JsonRequired] IReadOnlyList<string> CandidateSafeRefs,
    [property: JsonRequired] IReadOnlyList<string> LedgerTransactionRefs,
    [property: JsonRequired] string CompletedAt);

// DM-INGEST-LEDGER-COMMIT-CONTRACT
public sealed record FrozenLedgerRecordInput(
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string SignedAmount,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string TransactionDate,
    string? PostingDate,
    [property: JsonRequired] string OriginalDescription,
    string? InstrumentId,
    string? CardholderId,
    [property: JsonRequired] RegisterEvidenceInput InitialEvidence);

public sealed record FrozenLedgerRecordRequest(
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] SafeActor Actor,
    [property: JsonRequired] FrozenLedgerRecordInput Input);

public sealed record LedgerImmutableVerification(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string SignedAmount,
    [property: JsonRequired] string CurrencyCode,
    [property: JsonRequired] string TransactionDate,
    string? PostingDate,
    [property: JsonRequired] string OriginalDescription,
    string? InstrumentId,
    string? CardholderId,
    [property: JsonRequired] RegisterEvidenceInput InitialEvidence);
