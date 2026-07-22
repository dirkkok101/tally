using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Transactions;

public sealed record AssignCategoryInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string Reason);

public sealed record CorrectCategoryInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string Reason);

public sealed record CategoryAllocationResult(TransactionDetail Transaction, string AllocationEventId);
