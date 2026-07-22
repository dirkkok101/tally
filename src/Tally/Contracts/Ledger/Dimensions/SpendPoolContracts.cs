using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Dimensions;

[JsonConverter(typeof(JsonStringEnumConverter<SpendPoolStatus>))]
public enum SpendPoolStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("archived")]
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<SpendPoolLifecycleAction>))]
public enum SpendPoolLifecycleAction
{
    [JsonStringEnumMemberName("create")]
    Create,
    [JsonStringEnumMemberName("rename")]
    Rename,
    [JsonStringEnumMemberName("archive")]
    Archive,
    [JsonStringEnumMemberName("reactivate")]
    Reactivate
}

public sealed record SpendPoolHistoryItem(
    string LifecycleEventId,
    SpendPoolLifecycleAction Action,
    string? PreviousName,
    string? NewName,
    string? Reason,
    string Actor,
    string OccurredAt,
    string? PreviousLifecycleEventId);

public sealed record CreateSpendPoolInput([property: JsonRequired] string Name);
public sealed record GetSpendPoolInput([property: JsonRequired] string PoolId, bool IncludeHistory = false);
public sealed record ListSpendPoolsInput(SpendPoolStatus? Status = null);
public sealed record RenameSpendPoolInput([property: JsonRequired] string PoolId, [property: JsonRequired] string NewName, [property: JsonRequired] string Reason);
public sealed record ArchiveSpendPoolInput([property: JsonRequired] string PoolId, [property: JsonRequired] string Reason);
public sealed record ReactivateSpendPoolInput([property: JsonRequired] string PoolId, [property: JsonRequired] string Reason);

public sealed record SpendPoolDetail(
    string PoolId,
    string Name,
    SpendPoolStatus Status,
    long CurrentAssignmentCount,
    long HistoricalAssignmentCount,
    string CreatedActor,
    string CreatedAt,
    IReadOnlyList<SpendPoolHistoryItem> LifecycleHistory);

public sealed record SpendPoolListResult(IReadOnlyList<SpendPoolDetail> Items);
public sealed record SpendPoolLifecycleResult(SpendPoolDetail Pool, string LifecycleEventId);
