using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Recovery;

public sealed record CreateBackupInput([property: JsonRequired] string TargetPath);

public sealed record VerifyBackupInput(
    [property: JsonRequired] string ArtifactPath,
    string? ExpectedChecksum = null);

public sealed record BackupTypeResult(string Name, long RowCount, string Fingerprint);

public sealed record BackupActualsResult(
    string Grouping,
    long MemberCount,
    long CellCount,
    long NetAccountMovementMinor,
    long ExternalSpendMinor,
    long BudgetActualMinor,
    string CellFingerprint);

public sealed record BackupManifest(
    string FormatVersion,
    string RequestFingerprint,
    string DatabaseChecksum,
    int SchemaVersion,
    string StorageContractVersion,
    IReadOnlyList<string> ReconciliationPolicyVersions,
    IReadOnlyList<BackupTypeResult> Types,
    IReadOnlyList<BackupActualsResult> Actuals,
    string CategoryHierarchyFingerprint,
    string TransactionReplacementFingerprint,
    string RelationshipFingerprint,
    string ReconciliationFingerprint,
    string IdempotencyFingerprint,
    string NormalizedFingerprint)
{
    public const string CurrentFormatVersion = "1";
}

public sealed record BackupReceipt(
    string ArtifactName,
    long ArtifactLength,
    string ArtifactChecksum,
    BackupManifest Manifest);

public static class BackupErrors
{
    public const string Invalid = "LEDGER-BACKUP-INVALID";
    public const string TargetExists = "LEDGER-BACKUP-TARGET-EXISTS";
    public const string NotFound = "LEDGER-BACKUP-NOT-FOUND";
    public const string ChecksumMismatch = "LEDGER-BACKUP-CHECKSUM-MISMATCH";
    public const string Integrity = "LEDGER-BACKUP-INTEGRITY";
    public const string Incompatible = "LEDGER-BACKUP-INCOMPATIBLE";
    public const string HostProtection = "LEDGER-BACKUP-HOST-PROTECTION";
    public const string Permission = "LEDGER-BACKUP-PERMISSION";
    public const string Disk = "LEDGER-BACKUP-DISK";
    public const string Busy = "LEDGER-BACKUP-BUSY";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CreateBackupInput))]
[JsonSerializable(typeof(VerifyBackupInput))]
[JsonSerializable(typeof(BackupReceipt))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupTypeResult[]))]
[JsonSerializable(typeof(BackupActualsResult[]))]
[JsonSerializable(typeof(string[]))]
public partial class BackupJsonContext : JsonSerializerContext;
