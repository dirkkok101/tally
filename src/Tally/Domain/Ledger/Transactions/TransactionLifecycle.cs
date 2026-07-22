using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Transactions;

public static class TransactionLifecycle
{
    public const string InvalidError = "LEDGER-TRANSACTION-CORRECTION-INVALID";
    public const string NotFoundError = "LEDGER-TRANSACTION-NOT-FOUND";
    public const string InactiveError = "LEDGER-TRANSACTION-INACTIVE";
    public const string ReplacementConflictError = "LEDGER-TRANSACTION-REPLACEMENT-CONFLICT";

    public static bool RequiresReplacement(TransactionLifecycleAction action) => action is
        TransactionLifecycleAction.Superseded or TransactionLifecycleAction.StatementAuthoritativeReplacement;

    public static bool TryReason(string? value, out string reason)
    {
        reason = value?.Trim() ?? string.Empty;
        return reason.Length is > 0 and <= 512 && reason.All(character => !char.IsControl(character));
    }

    public static string? ValidateActive(TransactionDetail? transaction) => transaction switch
    {
        null => NotFoundError,
        { LifecycleStatus: not TransactionLifecycleStatus.Active } => InactiveError,
        _ => null
    };
}
