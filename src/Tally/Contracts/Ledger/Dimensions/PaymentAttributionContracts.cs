using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Contracts.Ledger.Dimensions;

public sealed record InstrumentAttributionInput([property: JsonRequired] TransactionKnowledgeState State, string? InstrumentId = null);
public sealed record CardholderAttributionInput([property: JsonRequired] TransactionKnowledgeState State, string? CardholderId = null);

public sealed record AssignPaymentAttributionInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string ExpectedAttributionEventId,
    InstrumentAttributionInput? Instrument,
    CardholderAttributionInput? Cardholder,
    [property: JsonRequired] string Reason);

public sealed record CorrectPaymentAttributionInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string ExpectedAttributionEventId,
    InstrumentAttributionInput? Instrument,
    CardholderAttributionInput? Cardholder,
    [property: JsonRequired] string Reason);

public sealed record PaymentAttributionResult(TransactionDetail Transaction, string AttributionEventId);

[JsonConverter(typeof(JsonStringEnumConverter<PaymentAttributionCarryForwardResolution>))]
public enum PaymentAttributionCarryForwardResolution
{
    [JsonStringEnumMemberName("carry_forward")]
    CarryForward,
    [JsonStringEnumMemberName("unknown_initialization")]
    UnknownInitialization
}

public sealed record PaymentAttributionCarryForwardResult(
    string SourceTransactionId,
    string ReplacementTransactionId,
    string ReconciliationDecisionId,
    string AttributionEventId,
    PaymentAttributionCarryForwardResolution Resolution,
    bool ReviewRequired);
