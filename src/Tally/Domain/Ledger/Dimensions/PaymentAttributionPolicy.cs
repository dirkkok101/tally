using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Dimensions;

public sealed record PaymentAttributionCommand(
    string TransactionId,
    string ExpectedAttributionEventId,
    InstrumentAttributionInput? Instrument,
    CardholderAttributionInput? Cardholder,
    string Reason);

public sealed record PaymentAttributionState(
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId);

public static class PaymentAttributionPolicy
{
    public const string InvalidError = "LEDGER-PAYMENT-ATTRIBUTION-INVALID";

    public static bool TryCreate(
        string? transactionId,
        string? expectedEventId,
        InstrumentAttributionInput? instrument,
        CardholderAttributionInput? cardholder,
        string? requestedReason,
        out PaymentAttributionCommand? command)
    {
        var reason = requestedReason?.Trim() ?? string.Empty;
        if (!LedgerId.TryParse(transactionId, out _, out _)
            || !LedgerId.TryParse(expectedEventId, out _, out _)
            || instrument is null && cardholder is null
            || !ValidInstrument(instrument)
            || !ValidCardholder(cardholder)
            || reason.Length is 0 or > 512
            || reason.Any(char.IsControl))
        {
            command = null;
            return false;
        }

        command = new(transactionId!, expectedEventId!, instrument, cardholder, reason);
        return true;
    }

    public static PaymentAttributionState Apply(PaymentAttributionState current, PaymentAttributionCommand command) => new(
        command.Instrument?.State ?? current.InstrumentState,
        command.Instrument is null ? current.InstrumentId : command.Instrument.InstrumentId,
        command.Cardholder?.State ?? current.CardholderState,
        command.Cardholder is null ? current.CardholderId : command.Cardholder.CardholderId);

    public static bool IsChanged(PaymentAttributionState current, PaymentAttributionState result) => current != result;

    public static bool AssignsKnownDimension(PaymentAttributionState current, PaymentAttributionCommand command) =>
        command.Instrument is not null && current.InstrumentState == TransactionKnowledgeState.Known
        || command.Cardholder is not null && current.CardholderState == TransactionKnowledgeState.Known;

    private static bool ValidInstrument(InstrumentAttributionInput? input) => input is null || input switch
    {
        { State: TransactionKnowledgeState.Unknown, InstrumentId: null } => true,
        { State: TransactionKnowledgeState.Known, InstrumentId: { } id } => LedgerId.TryParse(id, out _, out _),
        _ => false
    };

    private static bool ValidCardholder(CardholderAttributionInput? input) => input is null || input switch
    {
        { State: TransactionKnowledgeState.Unknown, CardholderId: null } => true,
        { State: TransactionKnowledgeState.Known, CardholderId: { } id } => LedgerId.TryParse(id, out _, out _),
        _ => false
    };
}
