using System.Text.Json;
using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Contracts.Common;

public sealed record ResultEnvelope(string ContractVersion, string OperationId, string Outcome, JsonElement? Result, ProcessError? Error);
public sealed record ProcessError(string Code, string Category, string Message, IReadOnlyList<string>? Fields = null);
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
public sealed record SafeActor(string Kind, string Label, string? RunId = null);
public sealed record RequestEnvelope(string ContractVersion, SafeActor Actor, JsonElement Input, string? IdempotencyKey = null);
public sealed record EmptyInput;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResultEnvelope))]
[JsonSerializable(typeof(ProcessError))]
[JsonSerializable(typeof(SafeActor))]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(EmptyInput))]
[JsonSerializable(typeof(Tally.Contracts.System.VersionResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaListResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaShowResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaShowRequest))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationUnavailableResult))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationSchema))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationSchema[]))]
[JsonSerializable(typeof(RegisterEvidenceInput))]
[JsonSerializable(typeof(GetEvidenceInput))]
[JsonSerializable(typeof(EvidenceRecordDetail))]
[JsonSerializable(typeof(EvidenceLinkHistoryItem[]))]
[JsonSerializable(typeof(CreateAccountInput))]
[JsonSerializable(typeof(GetAccountInput))]
[JsonSerializable(typeof(ListAccountsInput))]
[JsonSerializable(typeof(RenameAccountInput))]
[JsonSerializable(typeof(ArchiveAccountInput))]
[JsonSerializable(typeof(AccountDetail))]
[JsonSerializable(typeof(AccountSummary[]))]
[JsonSerializable(typeof(AccountLifecycleHistoryItem[]))]
[JsonSerializable(typeof(AccountListResult))]
[JsonSerializable(typeof(AccountLifecycleResult))]
[JsonSerializable(typeof(CreateCategoryInput))]
[JsonSerializable(typeof(GetCategoryInput))]
[JsonSerializable(typeof(ListCategoriesInput))]
[JsonSerializable(typeof(RenameCategoryInput))]
[JsonSerializable(typeof(ReparentCategoryInput))]
[JsonSerializable(typeof(ArchiveCategoryInput))]
[JsonSerializable(typeof(ReactivateCategoryInput))]
[JsonSerializable(typeof(CategoryDetail))]
[JsonSerializable(typeof(CategorySummary[]))]
[JsonSerializable(typeof(CategoryLifecycleHistoryItem[]))]
[JsonSerializable(typeof(CategoryParentHistoryItem[]))]
[JsonSerializable(typeof(CategoryListResult))]
[JsonSerializable(typeof(CategoryLifecycleResult))]
[JsonSerializable(typeof(CategoryReparentResult))]
[JsonSerializable(typeof(CreatePaymentInstrumentInput))]
[JsonSerializable(typeof(GetPaymentInstrumentInput))]
[JsonSerializable(typeof(ListPaymentInstrumentsInput))]
[JsonSerializable(typeof(RenamePaymentInstrumentInput))]
[JsonSerializable(typeof(ArchivePaymentInstrumentInput))]
[JsonSerializable(typeof(ReactivatePaymentInstrumentInput))]
[JsonSerializable(typeof(PaymentInstrumentDetail))]
[JsonSerializable(typeof(PaymentInstrumentDetail[]))]
[JsonSerializable(typeof(PaymentIdentityHistoryItem[]))]
[JsonSerializable(typeof(PaymentInstrumentListResult))]
[JsonSerializable(typeof(PaymentInstrumentLifecycleResult))]
[JsonSerializable(typeof(CreateCardholderInput))]
[JsonSerializable(typeof(GetCardholderInput))]
[JsonSerializable(typeof(ListCardholdersInput))]
[JsonSerializable(typeof(RenameCardholderInput))]
[JsonSerializable(typeof(ArchiveCardholderInput))]
[JsonSerializable(typeof(ReactivateCardholderInput))]
[JsonSerializable(typeof(CardholderDetail))]
[JsonSerializable(typeof(CardholderDetail[]))]
[JsonSerializable(typeof(CardholderListResult))]
[JsonSerializable(typeof(CardholderLifecycleResult))]
public partial class LedgerJsonContext : JsonSerializerContext;
