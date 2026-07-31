using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Transactions;

/// <summary>
/// Category assignment with optional Ledger-owned mutation preconditions
/// (DM-CLASSIFY-LEDGER-PROJECTION-CONTRACT / LedgerCategoryMutationPreconditions).
/// When revision/allocation expectations are omitted, behavior matches the original assign contract.
/// </summary>
public sealed record AssignCategoryInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string Reason,
    string? ExpectedTransactionRevision = null,
    string? ExpectedRelationshipRevision = null,
    string? ExpectedAllocationRevision = null,
    /// <summary>Must be null/absent for assign; a non-null value is a stale precondition.</summary>
    string? ExpectedActiveAllocationId = null);

/// <summary>
/// Category correction with required allocation identity and optional revision preconditions.
/// </summary>
public sealed record CorrectCategoryInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string Reason,
    /// <summary>Exact current active allocation identity; required for drift-safe correction.</summary>
    string? ExpectedActiveAllocationId = null,
    string? ExpectedTransactionRevision = null,
    string? ExpectedRelationshipRevision = null,
    string? ExpectedAllocationRevision = null);

public sealed record CategoryAllocationResult(TransactionDetail Transaction, string AllocationEventId);

/// <summary>Ledger-owned mutation precondition failure codes for CLASSIFY apply races.</summary>
public static class CategoryMutationPreconditionCodes
{
    public const string StalePrecondition = "LEDGER-CATEGORY-ALLOCATION-STALE-PRECONDITION";
}
