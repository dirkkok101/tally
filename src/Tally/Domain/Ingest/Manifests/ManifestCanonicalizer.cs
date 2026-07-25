using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tally.Contracts.Ingest;

namespace Tally.Domain.Ingest.Manifests;

public sealed record CanonicalManifestOutcome(
    string SourceRecordId,
    int Order,
    SourceRecordDisposition Disposition,
    string ReasonCode,
    string? CandidateId,
    string? PriorCanonicalRef);

public sealed record CanonicalManifestInput(
    string SourceFingerprint,
    string SelectedAccountId,
    string AdapterVersion,
    string LedgerContractVersion,
    string ManifestSchemaVersion,
    StatementPeriod StatementPeriod,
    IReadOnlyList<CanonicalManifestOutcome> OrderedRecordOutcomes,
    IReadOnlyList<ImportCandidate> OrderedCandidates,
    IReadOnlyList<ReconciliationControl> OrderedControls);

public sealed record CanonicalManifest(string CanonicalJson, string CanonicalDigest, string ManifestRevisionId);

public static class ManifestCanonicalizer
{
    public static CanonicalManifest Canonicalize(CanonicalManifestInput input)
    {
        var payload = new CanonicalManifestPayload(
            "manifest-canonical-v1",
            input.SourceFingerprint,
            input.SelectedAccountId,
            input.AdapterVersion,
            input.LedgerContractVersion,
            input.ManifestSchemaVersion,
            input.StatementPeriod,
            input.OrderedRecordOutcomes,
            input.OrderedCandidates,
            input.OrderedControls);
        var json = JsonSerializer.Serialize(payload, ManifestCanonicalJsonContext.Default.CanonicalManifestPayload);
        var canonicalBytes = Encoding.UTF8.GetBytes(json);
        var digest = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        return new(json, digest, digest);
    }
}

internal sealed record CanonicalManifestPayload(
    string Schema,
    string SourceFingerprint,
    string SelectedAccountId,
    string AdapterVersion,
    string LedgerContractVersion,
    string ManifestSchemaVersion,
    StatementPeriod StatementPeriod,
    IReadOnlyList<CanonicalManifestOutcome> RecordOutcomes,
    IReadOnlyList<ImportCandidate> Candidates,
    IReadOnlyList<ReconciliationControl> Controls);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CanonicalManifestPayload))]
internal partial class ManifestCanonicalJsonContext : JsonSerializerContext;
