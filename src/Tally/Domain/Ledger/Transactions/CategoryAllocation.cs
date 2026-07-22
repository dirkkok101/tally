namespace Tally.Domain.Ledger.Transactions;

public sealed record CategoryAllocation(string TransactionId, string CategoryId, string Reason)
{
    public const string InvalidError = "LEDGER-CATEGORY-ALLOCATION-INVALID";

    public static bool TryCreate(string? transactionId, string? categoryId, string? requestedReason, out CategoryAllocation? allocation)
    {
        var reason = requestedReason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(transactionId, out _, out _)
            || !LedgerId.TryParse(categoryId, out _, out _)
            || reason.Length is 0 or > 512
            || reason.Any(char.IsControl))
        {
            allocation = null;
            return false;
        }

        allocation = new(transactionId!, categoryId!, reason);
        return true;
    }
}
