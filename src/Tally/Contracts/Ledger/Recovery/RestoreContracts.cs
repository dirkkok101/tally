using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Recovery;

public sealed record PrepareRestoreInput(
    [property: JsonRequired] string ArtifactPath,
    [property: JsonRequired] string ExpectedArtifactChecksum);

public sealed record ActivateRestoreInput(
    [property: JsonRequired] string CandidateId,
    [property: JsonRequired] string ExpectedCurrentFingerprint,
    [property: JsonRequired] string ExpectedCandidateFingerprint,
    [property: JsonRequired] bool AuthorizeReplacement);

public sealed record RestorePrepareResult(
    string CandidateId,
    string SourceArtifactChecksum,
    string SourceNormalizedFingerprint,
    string CandidateNormalizedFingerprint,
    int SchemaVersion,
    string StorageContractVersion,
    IReadOnlyList<string> ReconciliationPolicyVersions,
    IReadOnlyList<BackupTypeResult> Types,
    IReadOnlyList<BackupActualsResult> Actuals,
    string CategoryHierarchyFingerprint,
    string TransactionReplacementFingerprint,
    string RelationshipFingerprint,
    string ReconciliationFingerprint,
    string IdempotencyFingerprint);

public sealed record RestoreActivationResult(string CurrentGenerationId, string NormalizedFingerprint);

internal sealed record RestoreActivationArtifact(
    string ContractVersion,
    string RequestFingerprint,
    string ExpectedCurrentFingerprint,
    string ExpectedCandidateFingerprint,
    RestoreActivationResult Result);

public static class RestoreErrors
{
    public const string Invalid = "LEDGER-RESTORE-INVALID";
    public const string CandidateConflict = "LEDGER-RESTORE-CANDIDATE-CONFLICT";
    public const string Integrity = "LEDGER-RESTORE-INTEGRITY";
    public const string Incompatible = "LEDGER-RESTORE-INCOMPATIBLE";
    public const string StaleCurrent = "LEDGER-RESTORE-STALE-CURRENT";
    public const string StaleCandidate = "LEDGER-RESTORE-STALE-CANDIDATE";
    public const string ActivationConflict = "LEDGER-RESTORE-ACTIVATION-CONFLICT";
    public const string NotAuthorized = "LEDGER-RESTORE-NOT-AUTHORIZED";
    public const string HostProtection = "LEDGER-RESTORE-HOST-PROTECTION";
    public const string Permission = "LEDGER-RESTORE-PERMISSION";
    public const string Disk = "LEDGER-RESTORE-DISK";
    public const string Busy = "LEDGER-RESTORE-BUSY";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PrepareRestoreInput))]
[JsonSerializable(typeof(ActivateRestoreInput))]
[JsonSerializable(typeof(RestorePrepareResult))]
[JsonSerializable(typeof(RestoreActivationResult))]
public partial class RestoreJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RestoreActivationArtifact))]
internal partial class RestoreArtifactJsonContext : JsonSerializerContext;
