using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Relationships;

public sealed record TransferCommand(string OutflowTransactionId, string InflowTransactionId, string Reason);

public static class TransferErrors
{
    public const string Invalid = "LEDGER-TRANSFER-INVALID";
    public const string TransactionInactive = "LEDGER-TRANSFER-TRANSACTION-INACTIVE";
    public const string SameAccount = "LEDGER-TRANSFER-SAME-ACCOUNT";
    public const string Sign = "LEDGER-TRANSFER-SIGN";
    public const string Amount = "LEDGER-TRANSFER-AMOUNT";
    public const string Currency = "LEDGER-TRANSFER-CURRENCY";
    public const string ActiveRoleConflict = "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT";
    public const string RelationshipNotFound = "LEDGER-RELATIONSHIP-NOT-FOUND";
}

public static class TransferPolicy
{
    public static bool TryCreateCommand(ConfirmTransferInput input, out TransferCommand? command)
    {
        var reason = input.Reason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(input.OutflowTransactionId, out _, out _)
            || !LedgerId.TryParse(input.InflowTransactionId, out _, out _)
            || input.OutflowTransactionId == input.InflowTransactionId
            || reason.Length is 0 or > 512 || reason.Any(char.IsControl))
        {
            command = null;
            return false;
        }

        command = new(input.OutflowTransactionId, input.InflowTransactionId, reason);
        return true;
    }

    public static bool TryPrincipal(
        TransactionDetail outflow,
        TransactionDetail inflow,
        out long principalMinor,
        out string? error)
    {
        principalMinor = 0;
        if (outflow.AccountId == inflow.AccountId) return Fail(TransferErrors.SameAccount, out error);
        if (outflow.CurrencyCode != inflow.CurrencyCode || outflow.CurrencyCode != "ZAR") return Fail(TransferErrors.Currency, out error);
        if (!Money.TryParseTransactionAmount(outflow.SignedAmount, out var outflowAmount, out _)
            || !Money.TryParseTransactionAmount(inflow.SignedAmount, out var inflowAmount, out _))
        {
            return Fail(TransferErrors.Amount, out error);
        }

        if (outflowAmount.MinorUnits >= 0 || inflowAmount.MinorUnits <= 0) return Fail(TransferErrors.Sign, out error);
        if (outflowAmount.MinorUnits != -inflowAmount.MinorUnits) return Fail(TransferErrors.Amount, out error);
        principalMinor = inflowAmount.MinorUnits;
        error = null;
        return true;
    }

    private static bool Fail(string value, out string? error)
    {
        error = value;
        return false;
    }
}
