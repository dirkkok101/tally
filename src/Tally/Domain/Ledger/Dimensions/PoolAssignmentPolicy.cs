using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Dimensions;

public sealed record PoolAssignmentCommand(string TransactionId, string ExpectedEventId, TransactionPoolState State, string? PoolId, string Reason);

public static class PoolAssignmentPolicy
{
    public const string InvalidError = "LEDGER-POOL-ASSIGNMENT-INVALID";
    public static bool TryCreate(string? transactionId, string? expectedEventId, PoolAssignmentInput? assignment, string? requestedReason, out PoolAssignmentCommand? command)
    {
        var reason = requestedReason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(transactionId, out _, out _) || !LedgerId.TryParse(expectedEventId, out _, out _)
            || assignment is null || !Enum.IsDefined(assignment.State)
            || assignment.State == TransactionPoolState.Assigned && !LedgerId.TryParse(assignment.PoolId, out _, out _)
            || assignment.State == TransactionPoolState.Unassigned && assignment.PoolId is not null
            || reason.Length is 0 or > 512 || reason.Any(char.IsControl))
        {
            command = null;
            return false;
        }
        command = new(transactionId!, expectedEventId!, assignment.State, assignment.PoolId, reason);
        return true;
    }
}
