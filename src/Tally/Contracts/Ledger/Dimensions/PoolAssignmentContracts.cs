using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Contracts.Ledger.Dimensions;

public sealed record PoolAssignmentInput([property: JsonRequired] TransactionPoolState State, string? PoolId = null);
public sealed record AssignPoolInput([property: JsonRequired] string TransactionId, [property: JsonRequired] string ExpectedPoolAssignmentEventId, [property: JsonRequired] PoolAssignmentInput Assignment, [property: JsonRequired] string Reason);
public sealed record CorrectPoolInput([property: JsonRequired] string TransactionId, [property: JsonRequired] string ExpectedPoolAssignmentEventId, [property: JsonRequired] PoolAssignmentInput Assignment, [property: JsonRequired] string Reason);
public sealed record PoolAssignmentResult(TransactionDetail Transaction, string PoolAssignmentEventId);
public sealed record PoolCarryForwardResult(string SourceTransactionId, string ReplacementTransactionId, string ReconciliationDecisionId, string PoolAssignmentEventId);
