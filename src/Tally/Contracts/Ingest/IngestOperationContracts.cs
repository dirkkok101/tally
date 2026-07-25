using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Tally.Contracts.Common;

namespace Tally.Contracts.Ingest;

// DM-INGEST-OPERATION-CONTRACTS
// Concrete versioned request and result shapes for the eight public INGEST CLI operations. All
// operations use the common Tally operation envelope and reject unknown fields. sourcePath is
// forbidden as a named CLI argument and appears only inside PreviewImportInput.

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactKind>))]
public enum ArtifactKind
{
    [JsonStringEnumMemberName("manifest")]
    Manifest,
    [JsonStringEnumMemberName("candidates")]
    Candidates,
    [JsonStringEnumMemberName("receipt")]
    Receipt,
    [JsonStringEnumMemberName("metadata")]
    Metadata
}

public sealed record IngestVersions(
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string ManifestSchemaVersion);

public sealed record IngestOutcomeCounts(
    [property: JsonRequired] int AcceptedCandidates,
    [property: JsonRequired] int ExactDuplicates,
    [property: JsonRequired] int ExcludedNonTransactions,
    [property: JsonRequired] int Blocked);

public sealed record ReconciliationControl(
    [property: JsonRequired] string Name,
    [property: JsonRequired] bool Satisfied,
    string? Detail);

public sealed record ReconciliationSummary(
    [property: JsonRequired] bool FullyReconciled,
    [property: JsonRequired] IReadOnlyList<ReconciliationControl> Controls);

public sealed record ManifestApprovalState(
    [property: JsonRequired] bool Approved,
    string? ApprovalId,
    string? ApprovedAt);

// ingest.preview

public sealed record PreviewImportInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string SourcePath,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] SafeActor Actor);

public sealed record PreviewImportResult(
    [property: JsonRequired] string BatchId,
    string? ManifestRevisionId,
    [property: JsonRequired] BatchStatus Status,
    string? Adapter,
    [property: JsonRequired] IngestOutcomeCounts Counts,
    [property: JsonRequired] ReconciliationSummary ReconciliationSummary,
    string? ExactReplayOf,
    IngestRetryAction? RetryAction);

// ingest.inspect

public sealed record InspectManifestInput(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId);

public sealed record InspectManifestResult(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string CanonicalDigest,
    [property: JsonRequired] IngestVersions Versions,
    [property: JsonRequired] string SelectedAccountId,
    [property: JsonRequired] IReadOnlyList<SourceRecordOutcome> RecordOutcomes,
    [property: JsonRequired] IReadOnlyList<ImportCandidate> Candidates,
    [property: JsonRequired] IReadOnlyList<SourceRecordOutcome> Exclusions,
    [property: JsonRequired] IReadOnlyList<SourceRecordOutcome> Duplicates,
    [property: JsonRequired] IReadOnlyList<SourceRecordOutcome> Conflicts,
    [property: JsonRequired] IReadOnlyList<ReconciliationControl> Controls,
    [property: JsonRequired] ManifestApprovalState ApprovalState);

// ingest.approve

public sealed record ApproveManifestInput(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string ManifestDigest,
    [property: JsonRequired] SafeActor Actor);

public sealed record ApproveManifestResult(
    [property: JsonRequired] string ApprovalId,
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string ApprovedAt);

// ingest.commit

public sealed record CommitBatchInput(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string ManifestRevisionId,
    [property: JsonRequired] string ManifestDigest);

// ingest.resume

public sealed record ResumeBatchInput([property: JsonRequired] string BatchId);

// ingest.status

public sealed record IngestStatusInput(string? BatchId = null, [property: Range(1, 100)] int Limit = 50, string? Cursor = null);

public sealed record IngestStatusResult(
    BatchStatusDetail? Detail,
    IReadOnlyList<BatchStatusSummary>? Items,
    string? NextCursor);

// ingest.abandon

public sealed record AbandonBatchInput(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] string Reason);

public sealed record AbandonBatchResult(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] BatchStatus Status,
    [property: JsonRequired] bool RetainedMetadata,
    [property: JsonRequired] int PriorLedgerEffectCount);

// ingest.cleanup

public sealed record CleanupBatchInput(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] BatchStatus ExpectedTerminalStatus);

public sealed record CleanupBatchResult(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] BatchStatus Status,
    [property: JsonRequired] IReadOnlyList<ArtifactKind> RemovedArtifactKinds);
