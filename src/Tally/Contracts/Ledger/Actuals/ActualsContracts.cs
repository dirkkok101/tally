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

/// <summary>
/// Purpose-scoped classification projection on ledger.actuals.query
/// (DD-CLASSIFY-LEDGER-PUBLIC-PROJECTION / DM-CLASSIFY-LEDGER-PROJECTION-CONTRACT).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassificationProjectionPurpose>))]
public enum ClassificationProjectionPurpose
{
    [JsonStringEnumMemberName("evaluation")]
    Evaluation,
    [JsonStringEnumMemberName("apply_preflight")]
    ApplyPreflight
}

[JsonConverter(typeof(JsonStringEnumConverter<CategoryMutationState>))]
public enum CategoryMutationState
{
    [JsonStringEnumMemberName("assignable")]
    Assignable,
    [JsonStringEnumMemberName("correctable")]
    Correctable,
    [JsonStringEnumMemberName("ineligible")]
    Ineligible
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassificationAmountDirection>))]
public enum ClassificationAmountDirection
{
    [JsonStringEnumMemberName("expense")]
    Expense,
    [JsonStringEnumMemberName("income")]
    Income,
    [JsonStringEnumMemberName("zero")]
    Zero
}

/// <summary>Stable classification projection wire version consumed by CLASSIFY.</summary>
public static class ClassificationProjectionVersions
{
    public const string ClassificationV1 = "classification_v1";
    public const int MaxApplyPreflightIds = 200;
}

public sealed record ClassificationCategoryIdentity(
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string LifecycleState);

public sealed record ClassificationProjectionItem(
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string EffectiveDate,
    [property: JsonRequired] string SignedAmount,
    [property: JsonRequired] string SourceDescription,
    [property: JsonRequired] ClassificationAmountDirection AmountDirection,
    [property: JsonRequired] CategoryMutationState CategoryMutationState,
    string? CurrentCategoryId,
    string? CurrentAllocationId,
    [property: JsonRequired] string TransactionRevision,
    [property: JsonRequired] string RelationshipRevision,
    [property: JsonRequired] string AllocationRevision);

/// <summary>
/// Durable freeze of a purpose-scoped classification projection (catalogue + items + missing IDs)
/// stored under one SnapshotId so later pages cannot drift to live store state.
/// </summary>
public sealed record ClassificationFrozenPayload(
    [property: JsonRequired] string ProjectionVersion,
    [property: JsonRequired] string CatalogueFingerprint,
    [property: JsonRequired] IReadOnlyList<ClassificationCategoryIdentity> ActiveCategories,
    [property: JsonRequired] IReadOnlyList<ClassificationProjectionItem> Items,
    IReadOnlyList<string>? MissingTransactionIds,
    [property: JsonRequired] ActualsTotalsResult Totals);

public sealed record QueryActualsInput(
    ActualsFilterInput? Filter = null,
    int? PageSize = null,
    string? Cursor = null,
    /// <summary>When set, enables purpose-scoped classification_v1 projection semantics.</summary>
    ClassificationProjectionPurpose? Purpose = null,
    /// <summary>Must be classification_v1 when Purpose is set.</summary>
    string? ItemProjection = null,
    /// <summary>Required and bounded for apply_preflight; omitted for evaluation.</summary>
    IReadOnlyList<string>? TransactionIds = null);

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
    string? Cursor,
    // BUDGET composition evidence (DM-BUDGET-LEDGER-COMPOSITION-CONTRACT): contract version + store generation for page/atomicity checks.
    string LedgerContractVersion = ActualsContractVersions.Current,
    string? StoreGenerationFingerprint = null,
    /// <summary>classification_v1 when purpose-scoped; null for ordinary actuals queries.</summary>
    string? ProjectionVersion = null,
    /// <summary>Fingerprint over active category identities for drift detection.</summary>
    string? CategoryIdentityLifecycleFingerprint = null,
    IReadOnlyList<ClassificationCategoryIdentity>? ActiveCategories = null,
    /// <summary>Purpose-scoped classification items (evaluation or apply_preflight).</summary>
    IReadOnlyList<ClassificationProjectionItem>? ClassificationItems = null,
    /// <summary>apply_preflight only: selected IDs absent from the store.</summary>
    IReadOnlyList<string>? MissingTransactionIds = null);

/// <summary>Stable actuals wire-contract version exposed to BUDGET composition (FR-BUDGET-LEDGER-COMPOSITION).</summary>
public static class ActualsContractVersions
{
    public const string Current = "1.0";
}

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
[JsonSerializable(typeof(ClassificationProjectionItem))]
[JsonSerializable(typeof(ClassificationProjectionItem[]))]
[JsonSerializable(typeof(ClassificationCategoryIdentity))]
[JsonSerializable(typeof(ClassificationCategoryIdentity[]))]
[JsonSerializable(typeof(ClassificationFrozenPayload))]
[JsonSerializable(typeof(ClassificationFrozenPayload[]))]
[JsonSerializable(typeof(string[]))]
public partial class ActualsJsonContext : JsonSerializerContext;
