using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Dimensions;

[JsonConverter(typeof(JsonStringEnumConverter<PaymentIdentityStatus>))]
public enum PaymentIdentityStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("archived")]
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<PaymentIdentityLifecycleAction>))]
public enum PaymentIdentityLifecycleAction
{
    [JsonStringEnumMemberName("create")]
    Create,
    [JsonStringEnumMemberName("rename")]
    Rename,
    [JsonStringEnumMemberName("archive")]
    Archive,
    [JsonStringEnumMemberName("reactivate")]
    Reactivate
}

public sealed record PaymentIdentityHistoryItem(
    string LifecycleEventId,
    PaymentIdentityLifecycleAction Action,
    string? PreviousLabel,
    string? NewLabel,
    string? Reason,
    string Actor,
    string OccurredAt,
    string? PreviousLifecycleEventId);

public sealed record CreatePaymentInstrumentInput([property: JsonRequired] string Label, string? AccountId = null, string? MaskedSuffix = null);
public sealed record GetPaymentInstrumentInput([property: JsonRequired] string InstrumentId, bool IncludeHistory = false);
public sealed record ListPaymentInstrumentsInput(PaymentIdentityStatus? Status = null, string? AccountId = null);
public sealed record RenamePaymentInstrumentInput([property: JsonRequired] string InstrumentId, [property: JsonRequired] string NewLabel, [property: JsonRequired] string Reason);
public sealed record ArchivePaymentInstrumentInput([property: JsonRequired] string InstrumentId, [property: JsonRequired] string Reason);
public sealed record ReactivatePaymentInstrumentInput([property: JsonRequired] string InstrumentId, [property: JsonRequired] string Reason);

public sealed record PaymentInstrumentDetail(
    string InstrumentId,
    string Label,
    string? AccountId,
    string? MaskedSuffix,
    PaymentIdentityStatus Status,
    string CreatedActor,
    string CreatedAt,
    IReadOnlyList<PaymentIdentityHistoryItem> LifecycleHistory);

public sealed record PaymentInstrumentListResult(IReadOnlyList<PaymentInstrumentDetail> Items);
public sealed record PaymentInstrumentLifecycleResult(PaymentInstrumentDetail Instrument, string LifecycleEventId);

public sealed record CreateCardholderInput([property: JsonRequired] string Label);
public sealed record GetCardholderInput([property: JsonRequired] string CardholderId, bool IncludeHistory = false);
public sealed record ListCardholdersInput(PaymentIdentityStatus? Status = null);
public sealed record RenameCardholderInput([property: JsonRequired] string CardholderId, [property: JsonRequired] string NewLabel, [property: JsonRequired] string Reason);
public sealed record ArchiveCardholderInput([property: JsonRequired] string CardholderId, [property: JsonRequired] string Reason);
public sealed record ReactivateCardholderInput([property: JsonRequired] string CardholderId, [property: JsonRequired] string Reason);

public sealed record CardholderDetail(
    string CardholderId,
    string Label,
    PaymentIdentityStatus Status,
    string CreatedActor,
    string CreatedAt,
    IReadOnlyList<PaymentIdentityHistoryItem> LifecycleHistory);

public sealed record CardholderListResult(IReadOnlyList<CardholderDetail> Items);
public sealed record CardholderLifecycleResult(CardholderDetail Cardholder, string LifecycleEventId);
