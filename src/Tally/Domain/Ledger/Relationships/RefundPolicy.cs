using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Relationships;

public sealed record RefundCommand(string OriginalTransactionId, string RefundTransactionId, string Reason);

public static class RefundErrors
{
    public const string Invalid = "LEDGER-REFUND-INVALID";
    public const string TransactionInactive = "LEDGER-REFUND-TRANSACTION-INACTIVE";
    public const string Account = "LEDGER-REFUND-ACCOUNT";
    public const string Sign = "LEDGER-REFUND-SIGN";
    public const string Amount = "LEDGER-REFUND-AMOUNT";
    public const string Currency = "LEDGER-REFUND-CURRENCY";
    public const string ActiveRoleConflict = TransferErrors.ActiveRoleConflict;
}

public static class RefundPolicy
{
    public static bool TryCreateCommand(ConfirmRefundInput input, out RefundCommand? command)
    {
        var reason = input.Reason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(input.OriginalTransactionId, out _, out _)
            || !LedgerId.TryParse(input.RefundTransactionId, out _, out _)
            || input.OriginalTransactionId == input.RefundTransactionId
            || reason.Length is 0 or > 512 || reason.Any(char.IsControl))
        {
            command = null;
            return false;
        }

        command = new(input.OriginalTransactionId, input.RefundTransactionId, reason);
        return true;
    }

    public static bool TryFullAmount(
        TransactionDetail original,
        TransactionDetail refund,
        out long amountMinor,
        out string? error)
    {
        amountMinor = 0;
        if (original.AccountId != refund.AccountId) return Fail(RefundErrors.Account, out error);
        if (original.CurrencyCode != refund.CurrencyCode || original.CurrencyCode != "ZAR") return Fail(RefundErrors.Currency, out error);
        if (!Money.TryParseTransactionAmount(original.SignedAmount, out var originalAmount, out _)
            || !Money.TryParseTransactionAmount(refund.SignedAmount, out var refundAmount, out _))
        {
            return Fail(RefundErrors.Amount, out error);
        }

        if (originalAmount.MinorUnits >= 0 || refundAmount.MinorUnits <= 0) return Fail(RefundErrors.Sign, out error);
        if (originalAmount.MinorUnits != -refundAmount.MinorUnits) return Fail(RefundErrors.Amount, out error);
        amountMinor = refundAmount.MinorUnits;
        error = null;
        return true;
    }

    private static bool Fail(string value, out string? error)
    {
        error = value;
        return false;
    }
}
