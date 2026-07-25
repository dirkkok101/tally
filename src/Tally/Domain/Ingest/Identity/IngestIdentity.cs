using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Domain.Ingest.Identity;

public sealed record SourceRecordIdentityInput(string SourceFingerprint, string StructuralPosition, string RawEvidenceFingerprint, string ImmutableFactsSchemaVersion);

public sealed record BatchIdentityInput(string SourceFingerprint, string SelectedAccountId, string AdapterVersion, string LedgerContractVersion);

public sealed record CandidateIdentityInput(string AccountId, string SourceRecordId, long SignedAmountMinor, string CurrencyCode, string TransactionDate, string? PostingDate, string OriginalDescription);

public sealed record CandidateIdentity(string CandidateId, string OpaqueExternalReference, string IdempotencyKey);

public static class IngestIdentity
{
    public static string BatchId(BatchIdentityInput input) => Hash("batch-v1", input.SourceFingerprint, input.SelectedAccountId, input.AdapterVersion, input.LedgerContractVersion);

    public static string SourceRecordId(SourceRecordIdentityInput input) => Hash("source-record-v1", input.SourceFingerprint, input.StructuralPosition, input.RawEvidenceFingerprint, input.ImmutableFactsSchemaVersion);

    public static CandidateIdentity Candidate(CandidateIdentityInput input)
    {
        var candidateId = Hash("candidate-v1", input.AccountId, input.SourceRecordId, input.SignedAmountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture), input.CurrencyCode, input.TransactionDate, input.PostingDate ?? string.Empty, input.OriginalDescription.Normalize(NormalizationForm.FormC));
        return new CandidateIdentity(candidateId, $"ingest:{candidateId}", $"ingest:{candidateId}");
    }

    public static RegisterEvidenceInput StatementEvidence(CandidateIdentityInput input)
    {
        var candidate = Candidate(input);
        return new RegisterEvidenceInput(
            EvidenceKind.StatementRow,
            candidate.CandidateId,
            candidate.OpaqueExternalReference,
            input.SourceRecordId,
            new EvidenceObservation(
                input.AccountId,
                input.SignedAmountMinor,
                input.CurrencyCode,
                input.TransactionDate,
                input.PostingDate,
                null,
                null,
                DescriptionFingerprint(input.OriginalDescription)));
    }

    public static bool HasImmutableFactConflict(string existingCandidateId, CandidateIdentity current) => !StringComparer.Ordinal.Equals(existingCandidateId, current.CandidateId);

    private static string DescriptionFingerprint(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description.Normalize(NormalizationForm.FormC)))).ToLowerInvariant();
    }

    private static string Hash(string version, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(version));
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var field = Encoding.UTF8.GetBytes(value);
            bytes.AddRange(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(field.Length)));
            bytes.AddRange(field);
        }

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }
}
