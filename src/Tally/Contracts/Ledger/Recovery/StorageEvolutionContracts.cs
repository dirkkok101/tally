using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Recovery;

public sealed record StorageStatusInput;

public sealed record PrepareStorageEvolutionInput([property: JsonRequired] int TargetSchemaVersion);

public sealed record ActivateStorageEvolutionInput(
    [property: JsonRequired] string CandidateId,
    [property: JsonRequired] string ExpectedCurrentFingerprint,
    [property: JsonRequired] string ExpectedCandidateFingerprint,
    [property: JsonRequired] bool AuthorizeReplacement);

public sealed record StorageStatusResult(
    string ContractVersion,
    int SchemaVersion,
    int CurrentSchemaVersion,
    string StorageContractVersion,
    IReadOnlyList<string> ReconciliationPolicyVersions,
    string CurrentGenerationId,
    string CurrentFingerprint,
    string CurrentNormalizedFingerprint,
    bool OwnerOnlyPermissions,
    bool IntegrityVerified,
    bool HostProtectionVerified);

public sealed record StorageEvolutionPrepareResult(
    string CandidateId,
    string RecoveryGenerationId,
    string RecoveryArtifactChecksum,
    int SourceSchemaVersion,
    int TargetSchemaVersion,
    string SourceFingerprint,
    string CandidateNormalizedFingerprint,
    string StorageContractVersion,
    IReadOnlyList<string> ReconciliationPolicyVersions,
    IReadOnlyList<BackupTypeResult> Types,
    IReadOnlyList<BackupActualsResult> Actuals,
    string CategoryHierarchyFingerprint,
    string TransactionReplacementFingerprint,
    string RelationshipFingerprint,
    string ReconciliationFingerprint,
    string IdempotencyFingerprint);

public sealed record StorageEvolutionActivationResult(string CurrentGenerationId, string NormalizedFingerprint);

internal sealed record EvolutionCandidateArtifact(
    string ContractVersion,
    string CandidateId,
    string RecoveryGenerationId,
    int SourceSchemaVersion,
    int TargetSchemaVersion,
    string SourceFingerprint,
    string CandidateNormalizedFingerprint);

public static class StorageEvolutionErrors
{
    public const string Invalid = "LEDGER-STORAGE-EVOLUTION-INVALID";
    public const string AlreadyCurrent = "LEDGER-STORAGE-EVOLUTION-ALREADY-CURRENT";
    public const string Incompatible = "LEDGER-STORAGE-EVOLUTION-INCOMPATIBLE";
    public const string Integrity = "LEDGER-STORAGE-EVOLUTION-INTEGRITY";
    public const string CandidateConflict = "LEDGER-STORAGE-EVOLUTION-CANDIDATE-CONFLICT";
    public const string StaleCurrent = "LEDGER-STORAGE-EVOLUTION-STALE-CURRENT";
    public const string StaleCandidate = "LEDGER-STORAGE-EVOLUTION-STALE-CANDIDATE";
    public const string ActivationConflict = "LEDGER-STORAGE-EVOLUTION-ACTIVATION-CONFLICT";
    public const string NotAuthorized = "LEDGER-STORAGE-EVOLUTION-NOT-AUTHORIZED";
    public const string HostProtection = "LEDGER-STORAGE-EVOLUTION-HOST-PROTECTION";
    public const string Permission = "LEDGER-STORAGE-EVOLUTION-PERMISSION";
    public const string Disk = "LEDGER-STORAGE-EVOLUTION-DISK";
    public const string InsufficientSpace = "LEDGER-STORAGE-EVOLUTION-INSUFFICIENT-SPACE";
    public const string Busy = "LEDGER-STORAGE-EVOLUTION-BUSY";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StorageStatusInput))]
[JsonSerializable(typeof(PrepareStorageEvolutionInput))]
[JsonSerializable(typeof(ActivateStorageEvolutionInput))]
[JsonSerializable(typeof(StorageStatusResult))]
[JsonSerializable(typeof(StorageEvolutionPrepareResult))]
[JsonSerializable(typeof(StorageEvolutionActivationResult))]
public partial class StorageEvolutionJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(EvolutionCandidateArtifact))]
internal partial class StorageEvolutionArtifactJsonContext : JsonSerializerContext;
