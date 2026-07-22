using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Reconciliation;

[JsonConverter(typeof(JsonStringEnumConverter<StatementCoverageMemberKind>))]
public enum StatementCoverageMemberKind
{
    [JsonStringEnumMemberName("statement_row")]
    StatementRow,
    [JsonStringEnumMemberName("eligible_transaction")]
    EligibleTransaction
}

[JsonConverter(typeof(JsonStringEnumConverter<StatementCoverageOutcome>))]
public enum StatementCoverageOutcome
{
    [JsonStringEnumMemberName("confirmed_existing")]
    ConfirmedExisting,
    [JsonStringEnumMemberName("corrected_from_statement")]
    CorrectedFromStatement,
    [JsonStringEnumMemberName("statement_only")]
    StatementOnly,
    [JsonStringEnumMemberName("statement_reconciled")]
    StatementReconciled,
    [JsonStringEnumMemberName("recorded_absent_from_statement")]
    RecordedAbsentFromStatement,
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,
    [JsonStringEnumMemberName("exception")]
    Exception,
    [JsonStringEnumMemberName("owner_confirmed_match")]
    OwnerConfirmedMatch
}

public sealed record CompleteStatementCoverageInput(
    [property: JsonRequired] string ScopeId,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string PeriodStart,
    [property: JsonRequired] string PeriodEnd,
    [property: JsonRequired] string ManifestOpaqueReference,
    [property: JsonRequired] IReadOnlyList<string> ExpectedEvidenceIds,
    [property: JsonRequired] string PolicyId,
    [property: JsonRequired] string PolicyVersion);

public sealed record GetStatementCoverageInput([property: JsonRequired] string ScopeId);

public sealed record StatementCoverageMember(
    StatementCoverageMemberKind Kind,
    string StableId,
    string? EvidenceId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    StatementCoverageOutcome Outcome,
    string Reason,
    string? DecisionId);

public sealed record StatementCoverageHistoryItem(
    string CoverageEntryId,
    StatementCoverageMemberKind Kind,
    string StableId,
    string? EvidenceId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    StatementCoverageOutcome Outcome,
    string Reason,
    string? DecisionId,
    string RecordedBy,
    string RecordedAt);

public sealed record StatementCoverageCount(
    StatementCoverageMemberKind Kind,
    StatementCoverageOutcome Outcome,
    int Count);

public sealed record StatementCoverageSummary(
    string ScopeId,
    string AccountId,
    string PeriodStart,
    string PeriodEnd,
    string ManifestOpaqueReference,
    string PolicyId,
    string PolicyVersion,
    int EvidenceCount,
    int EligibleTransactionCount,
    IReadOnlyList<StatementCoverageMember> CurrentMembers,
    IReadOnlyList<StatementCoverageCount> Counts,
    IReadOnlyList<StatementCoverageHistoryItem> History,
    string CompletedAt);

public static class ReconciliationCoverageErrors
{
    public const string InvalidInput = "validation.invalid_input";
    public const string ScopeNotFound = "LEDGER-RECONCILIATION-COVERAGE-SCOPE-NOT-FOUND";
    public const string ScopeIncomplete = "LEDGER-RECONCILIATION-COVERAGE-SCOPE-INCOMPLETE";
    public const string ScopeInactive = "LEDGER-RECONCILIATION-COVERAGE-SCOPE-INACTIVE";
    public const string ScopeConflict = "LEDGER-RECONCILIATION-COVERAGE-SCOPE-CONFLICT";
    public const string EvidenceSetChanged = "LEDGER-RECONCILIATION-COVERAGE-EVIDENCE-CHANGED";
    public const string PolicyUnsupported = "LEDGER-RECONCILIATION-COVERAGE-POLICY-UNSUPPORTED";
    public const string MissingOutcome = "LEDGER-RECONCILIATION-COVERAGE-OUTCOME-MISSING";
    public const string DuplicateTransactionOutcome = "LEDGER-RECONCILIATION-COVERAGE-TRANSACTION-DUPLICATE";
    public const string AlreadyCompleted = "LEDGER-RECONCILIATION-COVERAGE-ALREADY-COMPLETED";
    public const string NotFound = "LEDGER-RECONCILIATION-COVERAGE-NOT-FOUND";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CompleteStatementCoverageInput))]
[JsonSerializable(typeof(GetStatementCoverageInput))]
[JsonSerializable(typeof(StatementCoverageSummary))]
[JsonSerializable(typeof(StatementCoverageMember[]))]
[JsonSerializable(typeof(StatementCoverageCount[]))]
[JsonSerializable(typeof(StatementCoverageHistoryItem[]))]
public partial class ReconciliationCoverageJsonContext : JsonSerializerContext;
