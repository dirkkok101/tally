using System.Text.Json.Serialization;

namespace Tally.Contracts.Ingest;

// DM-INGEST-ERROR-STATUS-CONTRACTS
// Errors and status never contain source paths, statement rows, descriptions, amounts, balances,
// full bank identifiers, requests, manifests, stack traces, or parser exceptions.

[JsonConverter(typeof(JsonStringEnumConverter<IngestErrorCategory>))]
public enum IngestErrorCategory
{
    [JsonStringEnumMemberName("usage")]
    Usage,
    [JsonStringEnumMemberName("validation")]
    Validation,
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,
    [JsonStringEnumMemberName("unsafe_source")]
    UnsafeSource,
    [JsonStringEnumMemberName("compatibility")]
    Compatibility,
    [JsonStringEnumMemberName("permission")]
    Permission,
    [JsonStringEnumMemberName("resource")]
    Resource,
    [JsonStringEnumMemberName("reconciliation")]
    Reconciliation,
    [JsonStringEnumMemberName("overlap")]
    Overlap,
    [JsonStringEnumMemberName("ledger")]
    Ledger,
    [JsonStringEnumMemberName("interrupted")]
    Interrupted,
    [JsonStringEnumMemberName("conflict")]
    Conflict,
    [JsonStringEnumMemberName("unexpected")]
    Unexpected
}

[JsonConverter(typeof(JsonStringEnumConverter<MutationPossibility>))]
public enum MutationPossibility
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("possible")]
    Possible,
    [JsonStringEnumMemberName("confirmed")]
    Confirmed
}

[JsonConverter(typeof(JsonStringEnumConverter<IngestRetryAction>))]
public enum IngestRetryAction
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("retry")]
    Retry,
    [JsonStringEnumMemberName("repreview")]
    Repreview,
    [JsonStringEnumMemberName("resume")]
    Resume,
    [JsonStringEnumMemberName("abandon")]
    Abandon,
    [JsonStringEnumMemberName("correct_source")]
    CorrectSource
}

public sealed record IngestError(
    [property: JsonRequired] string Code,
    [property: JsonRequired] IngestErrorCategory Category,
    [property: JsonRequired] string SafeMessage,
    string? BatchId,
    string? CandidateId,
    [property: JsonRequired] MutationPossibility MutationPossibility,
    string? DurableState,
    [property: JsonRequired] IngestRetryAction RetryAction,
    string? Field);

public sealed record BatchStatusSummary(
    [property: JsonRequired] string BatchId,
    [property: JsonRequired] BatchStatus Status,
    string? AdapterId,
    [property: JsonRequired] string CreatedAt,
    [property: JsonRequired] string UpdatedAt,
    [property: JsonRequired] IngestOutcomeCounts OutcomeCounts,
    [property: JsonRequired] IReadOnlyList<string> NextAllowedOperations);

public sealed record BatchStatusDetail(
    [property: JsonRequired] BatchStatusSummary Summary,
    string? ManifestRevisionId,
    [property: JsonRequired] bool Approved,
    ImportReceiptStatus? ReceiptStatus,
    [property: JsonRequired] IngestOutcomeCounts TerminalCounts,
    [property: JsonRequired] IReadOnlyList<string> UnresolvedFrontier,
    IngestError? LastStableError,
    [property: JsonRequired] IReadOnlyList<ArtifactKind> RetainedArtifactKinds);
