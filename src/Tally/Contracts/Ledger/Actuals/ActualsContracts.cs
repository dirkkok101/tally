using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Contracts.Ledger.Actuals;

[JsonConverter(typeof(JsonStringEnumConverter<ActualsCategorySelectionScope>))]
public enum ActualsCategorySelectionScope
{
    [JsonStringEnumMemberName("exact")]
    Exact,
    [JsonStringEnumMemberName("subtree")]
    Subtree
}

[JsonConverter(typeof(JsonStringEnumConverter<ActualsGrouping>))]
public enum ActualsGrouping
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("pool")]
    Pool,
    [JsonStringEnumMemberName("category_direct")]
    CategoryDirect,
    [JsonStringEnumMemberName("category_subtree")]
    CategorySubtree,
    [JsonStringEnumMemberName("pool_category")]
    PoolCategory
}

[JsonConverter(typeof(JsonStringEnumConverter<ActualsRelationshipRole>))]
public enum ActualsRelationshipRole
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("transfer_outflow")]
    TransferOutflow,
    [JsonStringEnumMemberName("transfer_inflow")]
    TransferInflow,
    [JsonStringEnumMemberName("refund_original")]
    RefundOriginal,
    [JsonStringEnumMemberName("refund_credit")]
    RefundCredit
}

public sealed record ActualsFilterInput(
    IReadOnlyList<string>? AccountIds = null,
    string? EffectiveFrom = null,
    string? EffectiveTo = null,
    IReadOnlyList<string>? CategoryIds = null,
    ActualsCategorySelectionScope CategoryScope = ActualsCategorySelectionScope.Exact,
    IReadOnlyList<TransactionCategoryState>? CategorizationStates = null,
    IReadOnlyList<string>? PoolIds = null,
    IReadOnlyList<TransactionPoolState>? PoolStates = null,
    IReadOnlyList<string>? InstrumentIds = null,
    IReadOnlyList<TransactionKnowledgeState>? InstrumentStates = null,
    IReadOnlyList<string>? CardholderIds = null,
    IReadOnlyList<TransactionKnowledgeState>? CardholderStates = null,
    IReadOnlyList<EvidenceKind>? EvidenceKinds = null,
    IReadOnlyList<TransactionReconciliationState>? ReconciliationStates = null,
    IReadOnlyList<ActualsRelationshipRole>? RelationshipStates = null,
    IReadOnlyList<TransactionLifecycleStatus>? LifecycleStates = null,
    ActualsGrouping GroupBy = ActualsGrouping.None);

public sealed record QueryActualsInput(
    ActualsFilterInput? Filter = null,
    int? PageSize = null,
    string? Cursor = null);

public sealed record ActualsTotalsResult(
    string NetAccountMovement,
    string ExternalSpend,
    string BudgetActual);

public sealed record ActualsPageItem(
    int Ordinal,
    string TransactionId,
    string EffectiveDate,
    TransactionCategoryState CategoryState,
    string? CategoryId,
    IReadOnlyList<string> FrozenAncestryIds,
    TransactionPoolState PoolState,
    string? PoolId,
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId,
    IReadOnlyList<EvidenceKind> EvidenceKinds,
    TransactionReconciliationState ReconciliationState,
    ActualsRelationshipRole RelationshipState,
    ActualsTotalsResult Contribution);

public sealed record ActualsGroupResult(
    ActualsGrouping Kind,
    TransactionPoolState? PoolState,
    string? PoolId,
    TransactionCategoryState? CategoryState,
    string? CategoryId,
    ActualsTotalsResult Totals);

public sealed record ActualsQueryResult(
    string SnapshotId,
    string ExpiresAt,
    int TotalCount,
    IReadOnlyList<ActualsPageItem> Items,
    ActualsTotalsResult Totals,
    IReadOnlyList<ActualsGroupResult> Groups,
    string? Cursor);

public sealed record ActualsCursorPayload(
    int CursorVersion,
    string ContractVersion,
    string SnapshotId,
    int NextOrdinal,
    int PageSize,
    string FilterHash,
    string GenerationFingerprint,
    string CategoryHierarchyFingerprint,
    string ExpiresAt);

public static class ActualsErrors
{
    public const string InvalidFilter = "LEDGER-ACTUALS-FILTER-INVALID";
    public const string CursorInvalid = "LEDGER-SNAPSHOT-CURSOR-INVALID";
    public const string SnapshotNotFound = "LEDGER-SNAPSHOT-NOT-FOUND";
    public const string SnapshotExpired = "LEDGER-SNAPSHOT-EXPIRED";
    public const string ContractMismatch = "LEDGER-SNAPSHOT-CONTRACT-MISMATCH";
    public const string CursorFilterMismatch = "LEDGER-SNAPSHOT-FILTER-MISMATCH";
    public const string GenerationMismatch = "LEDGER-SNAPSHOT-GENERATION-MISMATCH";
    public const string HierarchyMismatch = "LEDGER-SNAPSHOT-HIERARCHY-MISMATCH";
    public const string SnapshotBusy = "LEDGER-SNAPSHOT-BUSY";
    public const string Invariant = "LEDGER-ACTUALS-INVARIANT";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueryActualsInput))]
[JsonSerializable(typeof(ActualsFilterInput))]
[JsonSerializable(typeof(ActualsQueryResult))]
[JsonSerializable(typeof(ActualsPageItem[]))]
[JsonSerializable(typeof(ActualsGroupResult[]))]
[JsonSerializable(typeof(ActualsCursorPayload))]
[JsonSerializable(typeof(string[]))]
public partial class ActualsJsonContext : JsonSerializerContext;
