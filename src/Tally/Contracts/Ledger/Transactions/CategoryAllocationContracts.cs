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
    string? ExpectedActiveAllocationId = null,
    /// <summary>
    /// Released classification mutation contract. Null preserves legacy assign without preconditions.
    /// Must be <see cref="CategoryAllocationMutationVersions.ClassificationV1"/> when set.
    /// </summary>
    string? MutationContractVersion = null);

/// <summary>
/// Category correction requiring exact active allocation identity and projection revisions
/// (failure criterion: do not permit correction without exact expected allocation and revisions).
/// </summary>
public sealed record CorrectCategoryInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string Reason,
    /// <summary>Exact current active allocation identity; required for drift-safe correction.</summary>
    string? ExpectedActiveAllocationId = null,
    string? ExpectedTransactionRevision = null,
    string? ExpectedRelationshipRevision = null,
    string? ExpectedAllocationRevision = null,
    /// <summary>
    /// Released classification mutation contract. Null is allowed only with full preconditions.
    /// Must be <see cref="CategoryAllocationMutationVersions.ClassificationV1"/> when set.
    /// </summary>
    string? MutationContractVersion = null);

public sealed record CategoryAllocationResult(TransactionDetail Transaction, string AllocationEventId);

/// <summary>Released category-allocation mutation contract versions for CLASSIFY apply.</summary>
public static class CategoryAllocationMutationVersions
{
    public const string ClassificationV1 = "classification_v1";
}

/// <summary>Ledger-owned mutation precondition failure codes for CLASSIFY apply races.</summary>
public static class CategoryMutationPreconditionCodes
{
    public const string StalePrecondition = "LEDGER-CATEGORY-ALLOCATION-STALE-PRECONDITION";
    public const string ContractMismatch = "LEDGER-CATEGORY-ALLOCATION-CONTRACT-MISMATCH";
}
