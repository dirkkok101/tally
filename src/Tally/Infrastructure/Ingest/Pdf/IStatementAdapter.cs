using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;

namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-FORMAT-ADAPTERS
public interface IStatementAdapter
{
    FormatVariantDescriptor Descriptor { get; }

    VariantProbeResult Probe(PdfDocumentEvidence evidence);

    ExtractedStatement Extract(PdfDocumentEvidence evidence, AccountDetail selectedAccount);
}

public sealed record FormatVariantDescriptor(
    string VariantId,
    string AdapterVersion,
    string ExtractionPolicyVersion,
    int ManifestSchemaVersion,
    string SupportedMediaType,
    PdfExtractionLimits HardLimits);

public enum VariantProbeOutcome
{
    NoMatch,
    ExactMatch,
    Unsafe
}

public sealed record VariantProbeResult(
    string VariantId,
    VariantProbeOutcome Outcome,
    IReadOnlyList<string> StructuralEvidenceCodes);

public sealed record StatementAccountEvidence(
    string AccountId,
    AccountClass AccountClass,
    string CurrencyCode,
    string MaskedIdentifier,
    string MetadataFingerprint,
    bool Matched);

public enum DescriptionEvidenceKind
{
    SourceText,
    SourceAbsentMarker
}

public sealed record SourceRecordEvidence(
    string SourceRecordId,
    int PageNumber,
    int RecordOrdinal,
    string RecordKind,
    string OriginalTextEvidence,
    DescriptionEvidenceKind DescriptionEvidenceKind,
    string? SourceReference,
    FinancialEvidence FinancialEvidence,
    long? RunningBalanceMinor,
    long? SourceControlMinor);

public enum ReconciliationControlKind
{
    OpeningBalance,
    ClosingBalance,
    RunningBalance,
    SourceTotal,
    RecordCount
}

public enum ReconciliationControlAvailability
{
    Verified,
    Failed,
    Unavailable
}

public sealed record ReconciliationControl(
    ReconciliationControlKind Kind,
    ReconciliationControlAvailability Availability,
    string SafeEvidenceRef);

public sealed record ExtractedStatement(
    FormatVariantDescriptor Variant,
    StatementPeriod StatementPeriod,
    StatementAccountEvidence AccountEvidence,
    IReadOnlyList<SourceRecordEvidence> OrderedRecords,
    long? OpeningEconomicBalanceMinor,
    long? ClosingEconomicBalanceMinor,
    IReadOnlyList<ReconciliationControlKind> AdvertisedControls);
