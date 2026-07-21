using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Accounts;

[JsonConverter(typeof(JsonStringEnumConverter<AccountType>))]
public enum AccountType
{
    [JsonStringEnumMemberName("cheque")]
    Cheque,
    [JsonStringEnumMemberName("savings")]
    Savings,
    [JsonStringEnumMemberName("credit_card")]
    CreditCard,
    [JsonStringEnumMemberName("other_asset")]
    OtherAsset,
    [JsonStringEnumMemberName("other_liability")]
    OtherLiability
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountClass>))]
public enum AccountClass
{
    [JsonStringEnumMemberName("asset")]
    Asset,
    [JsonStringEnumMemberName("liability")]
    Liability
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountStatus>))]
public enum AccountStatus
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("archived")]
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<AccountLifecycleAction>))]
public enum AccountLifecycleAction
{
    [JsonStringEnumMemberName("create")]
    Create,
    [JsonStringEnumMemberName("rename")]
    Rename,
    [JsonStringEnumMemberName("archive")]
    Archive
}

public sealed record CreateAccountInput(
    [property: JsonRequired] string InstitutionName,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] AccountType AccountType,
    [property: JsonRequired] string MaskedIdentifier,
    [property: JsonRequired] string CurrencyCode);

public sealed record GetAccountInput([property: JsonRequired] string AccountId, bool IncludeHistory = false);
public sealed record ListAccountsInput(AccountStatus? Status = null, string? InstitutionName = null);
public sealed record RenameAccountInput([property: JsonRequired] string AccountId, [property: JsonRequired] string NewDisplayName, [property: JsonRequired] string Reason);
public sealed record ArchiveAccountInput([property: JsonRequired] string AccountId, [property: JsonRequired] string Reason);

public sealed record AccountLifecycleHistoryItem(
    string LifecycleEventId,
    AccountLifecycleAction Action,
    string? PreviousDisplayName,
    string? NewDisplayName,
    string? Reason,
    string Actor,
    string OccurredAt,
    string? PreviousLifecycleEventId);

public sealed record AccountDetail(
    string AccountId,
    string InstitutionName,
    string DisplayName,
    AccountType AccountType,
    AccountClass AccountClass,
    string MaskedIdentifier,
    string CurrencyCode,
    AccountStatus Status,
    string CreatedActor,
    string CreatedAt,
    string? ArchivedAt,
    IReadOnlyList<AccountLifecycleHistoryItem> LifecycleHistory);

public sealed record AccountSummary(
    string AccountId,
    string InstitutionName,
    string DisplayName,
    AccountType AccountType,
    AccountClass AccountClass,
    string MaskedIdentifier,
    string CurrencyCode,
    AccountStatus Status);

public sealed record AccountListResult(IReadOnlyList<AccountSummary> Items);
public sealed record AccountLifecycleResult(AccountDetail Account, string LifecycleEventId);
