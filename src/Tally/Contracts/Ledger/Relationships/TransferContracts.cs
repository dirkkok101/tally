using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Relationships;

[JsonConverter(typeof(JsonStringEnumConverter<FinancialRelationshipType>))]
public enum FinancialRelationshipType
{
    [JsonStringEnumMemberName("transfer")]
    Transfer,
    [JsonStringEnumMemberName("refund")]
    Refund
}

[JsonConverter(typeof(JsonStringEnumConverter<FinancialRelationshipState>))]
public enum FinancialRelationshipState
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("retired")]
    Retired
}

[JsonConverter(typeof(JsonStringEnumConverter<FinancialRelationshipRole>))]
public enum FinancialRelationshipRole
{
    [JsonStringEnumMemberName("transfer_outflow")]
    TransferOutflow,
    [JsonStringEnumMemberName("transfer_inflow")]
    TransferInflow,
    [JsonStringEnumMemberName("refund_original")]
    RefundOriginal,
    [JsonStringEnumMemberName("refund_credit")]
    RefundCredit
}

[JsonConverter(typeof(JsonStringEnumConverter<RelationshipLifecycleAction>))]
public enum RelationshipLifecycleAction
{
    [JsonStringEnumMemberName("revoked")]
    Revoked,
    [JsonStringEnumMemberName("replaced")]
    Replaced
}

public sealed record ConfirmTransferInput(
    [property: JsonRequired] string OutflowTransactionId,
    [property: JsonRequired] string InflowTransactionId,
    [property: JsonRequired] string Reason);

public sealed record GetRelationshipInput(
    [property: JsonRequired] string RelationshipId,
    bool IncludeHistory = false);

public sealed record RelationshipLifecycleHistoryItem(
    string LifecycleEventId,
    RelationshipLifecycleAction Action,
    string? ReplacementRelationshipId,
    string? ReconciliationDecisionId,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record FinancialRelationshipDetail(
    string RelationshipId,
    FinancialRelationshipType Type,
    string SourceTransactionId,
    FinancialRelationshipRole SourceRole,
    string TargetTransactionId,
    FinancialRelationshipRole TargetRole,
    string PrincipalAmount,
    string CurrencyCode,
    FinancialRelationshipState State,
    string Actor,
    string CreatedAt,
    string? ReconciliationDecisionId,
    IReadOnlyList<RelationshipLifecycleHistoryItem> History);
