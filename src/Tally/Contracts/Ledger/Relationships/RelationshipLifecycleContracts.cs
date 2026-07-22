using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Relationships;

public sealed record RevokeRelationshipInput(
    [property: JsonRequired] string RelationshipId,
    [property: JsonRequired] string Reason);

public sealed record ReplaceTransferInput(
    [property: JsonRequired] string RelationshipId,
    [property: JsonRequired] string OutflowTransactionId,
    [property: JsonRequired] string InflowTransactionId,
    [property: JsonRequired] string Reason);

public sealed record ReplaceRefundInput(
    [property: JsonRequired] string RelationshipId,
    [property: JsonRequired] string OriginalTransactionId,
    [property: JsonRequired] string RefundTransactionId,
    [property: JsonRequired] string Reason);

public sealed record RelationshipLifecycleResult(
    FinancialRelationshipDetail Relationship,
    string LifecycleEventId,
    FinancialRelationshipDetail? ReplacementRelationship);
