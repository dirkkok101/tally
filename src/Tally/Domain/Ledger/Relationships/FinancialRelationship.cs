using Tally.Contracts.Ledger.Relationships;

namespace Tally.Domain.Ledger.Relationships;

public sealed record FinancialRelationship(
    string RelationshipId,
    FinancialRelationshipType Type,
    string SourceTransactionId,
    FinancialRelationshipRole SourceRole,
    string TargetTransactionId,
    FinancialRelationshipRole TargetRole,
    long PrincipalMinor,
    string Actor,
    string CreatedAt,
    string? ReconciliationDecisionId = null);
