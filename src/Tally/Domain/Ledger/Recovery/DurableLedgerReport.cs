namespace Tally.Domain.Ledger.Recovery;

public sealed record DurableArtifactReport(
    string Name,
    long Length,
    string Checksum,
    string Permissions);

public sealed record DurableTypeReport(
    string Name,
    long RowCount,
    string Fingerprint);

public sealed record DurableActualsReport(
    string Grouping,
    int MemberCount,
    int CellCount,
    long NetAccountMovementMinor,
    long ExternalSpendMinor,
    long BudgetActualMinor,
    string CellFingerprint);

public sealed record DurableLedgerReport(
    int SchemaVersion,
    string StorageContractVersion,
    IReadOnlyList<string> ReconciliationPolicyVersions,
    IReadOnlyList<DurableArtifactReport> Artifacts,
    IReadOnlyList<DurableTypeReport> Types,
    IReadOnlyList<DurableActualsReport> Actuals,
    string CategoryHierarchyFingerprint,
    string TransactionReplacementFingerprint,
    string RelationshipFingerprint,
    string ReconciliationFingerprint,
    string IdempotencyFingerprint,
    string NormalizedFingerprint);

public sealed record DurableLedgerVerificationResult(
    DurableLedgerReport? Report,
    string? ErrorCode,
    string? SafeType,
    int ViolationCount)
{
    public bool IsVerified => ErrorCode is null;

    public static DurableLedgerVerificationResult Verified(DurableLedgerReport report) => new(report, null, null, 0);

    public static DurableLedgerVerificationResult Failure(string errorCode, string safeType, int violationCount = 1) =>
        new(null, errorCode, safeType, violationCount);
}

public static class DurableLedgerErrors
{
    public const string LiveStore = "LEDGER-VERIFY-LIVE-STORE";
    public const string HostProtection = "LEDGER-VERIFY-HOST-PROTECTION";
    public const string ChecksumMismatch = "LEDGER-VERIFY-CHECKSUM-MISMATCH";
    public const string IntegrityFailure = "LEDGER-VERIFY-INTEGRITY";
    public const string SchemaIncompatible = "LEDGER-VERIFY-SCHEMA-INCOMPATIBLE";
    public const string PolicyIncompatible = "LEDGER-VERIFY-POLICY-INCOMPATIBLE";
    public const string PrivacyViolation = "LEDGER-VERIFY-PRIVACY";
    public const string InvariantViolation = "LEDGER-VERIFY-INVARIANT";
    public const string StateChanged = "LEDGER-VERIFY-STATE-CHANGED";
}
