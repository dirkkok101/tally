using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Reconciliation;

public sealed record RegisterReconciliationScopeInput(
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string PeriodStart,
    [property: JsonRequired] string PeriodEnd,
    [property: JsonRequired] string ManifestOpaqueReference,
    [property: JsonRequired] IReadOnlyList<string> EvidenceIds);

public sealed record ReconciliationScopeDetail(
    string ScopeId,
    string AccountId,
    string PeriodStart,
    string PeriodEnd,
    string ManifestOpaqueReference,
    string Status,
    IReadOnlyList<string> EvidenceIds,
    string CreatedBy,
    string CreatedAt);

public static class ReconciliationScopeErrors
{
    public const string InvalidInput = "validation.invalid_input";
    public const string AccountNotFound = "LEDGER-ACCOUNT-NOT-FOUND";
    public const string AccountInactive = "LEDGER-ACCOUNT-ARCHIVED";
    public const string EvidenceNotFound = "LEDGER-SCOPE-EVIDENCE-NOT-FOUND";
    public const string StatementEvidenceRequired = "LEDGER-SCOPE-STATEMENT-EVIDENCE-REQUIRED";
    public const string IncompleteObservation = "LEDGER-SCOPE-INCOMPLETE-OBSERVATION";
    public const string AccountDateConflict = "LEDGER-SCOPE-ACCOUNT-DATE-CONFLICT";
    public const string EvidenceAlreadyScoped = "LEDGER-SCOPE-EVIDENCE-ALREADY-SCOPED";
    public const string AccountPeriodConflict = "LEDGER-SCOPE-ACCOUNT-PERIOD-CONFLICT";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RegisterReconciliationScopeInput))]
[JsonSerializable(typeof(ReconciliationScopeDetail))]
public partial class ReconciliationScopeJsonContext : JsonSerializerContext;
