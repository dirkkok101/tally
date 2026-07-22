using Tally.Contracts.Ledger.Relationships;

namespace Tally.Domain.Ledger.Relationships;

public static class RelationshipLifecycleErrors
{
    public const string Invalid = "LEDGER-RELATIONSHIP-LIFECYCLE-INVALID";
    public const string NotFound = TransferErrors.RelationshipNotFound;
    public const string AlreadyRetired = "LEDGER-RELATIONSHIP-ALREADY-RETIRED";
    public const string TypeMismatch = "LEDGER-RELATIONSHIP-TYPE-MISMATCH";
    public const string ReviewRequired = "operation.review_required";
}

public sealed record RelationshipReplacementProposal(
    string RelationshipId,
    FinancialRelationshipType Type,
    string SourceTransactionId,
    string TargetTransactionId,
    string Reason);

public sealed record StatementRelationshipReplacementResult(
    bool ReviewRequired,
    string? ErrorCode,
    string? LifecycleEventId,
    FinancialRelationshipDetail? Relationship,
    FinancialRelationshipDetail? ReplacementRelationship);

public static class RelationshipLifecycle
{
    public static bool TryRevoke(RevokeRelationshipInput input, out string normalizedReason)
    {
        normalizedReason = input.Reason?.Trim() ?? string.Empty;
        return LedgerId.TryParse(input.RelationshipId, out _, out _) && ValidReason(normalizedReason);
    }

    public static bool TryReplace(ReplaceTransferInput input, out RelationshipReplacementProposal? proposal) =>
        TryCreate(
            input.RelationshipId,
            FinancialRelationshipType.Transfer,
            input.OutflowTransactionId,
            input.InflowTransactionId,
            input.Reason,
            out proposal);

    public static bool TryReplace(ReplaceRefundInput input, out RelationshipReplacementProposal? proposal) =>
        TryCreate(
            input.RelationshipId,
            FinancialRelationshipType.Refund,
            input.OriginalTransactionId,
            input.RefundTransactionId,
            input.Reason,
            out proposal);

    public static bool ValidReason(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return ValidReason(normalized);
    }

    private static bool TryCreate(
        string relationshipId,
        FinancialRelationshipType type,
        string sourceTransactionId,
        string targetTransactionId,
        string? reason,
        out RelationshipReplacementProposal? proposal)
    {
        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(relationshipId, out _, out _)
            || !LedgerId.TryParse(sourceTransactionId, out _, out _)
            || !LedgerId.TryParse(targetTransactionId, out _, out _)
            || sourceTransactionId == targetTransactionId
            || !ValidReason(normalizedReason))
        {
            proposal = null;
            return false;
        }

        proposal = new(relationshipId, type, sourceTransactionId, targetTransactionId, normalizedReason);
        return true;
    }

    private static bool ValidReason(string value) => value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
}
