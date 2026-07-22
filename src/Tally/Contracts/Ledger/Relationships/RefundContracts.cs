using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Relationships;

public sealed record ConfirmRefundInput(
    [property: JsonRequired] string OriginalTransactionId,
    [property: JsonRequired] string RefundTransactionId,
    [property: JsonRequired] string Reason);
