namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-FORMAT-ADAPTERS
public interface IStatementAdapter
{
    FormatVariantDescriptor Descriptor { get; }

    VariantProbeResult Probe(PdfDocumentEvidence evidence);

    ExtractedStatement Extract(PdfDocumentEvidence evidence, StatementAccountContext selectedAccount);
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

public sealed record StatementAccountContext(string AccountId);

public sealed record SourceRecordEvidence(
    string SourceRecordId,
    int PageNumber,
    int RecordOrdinal,
    string RecordKind,
    string OriginalTextEvidence,
    string? SourceReference,
    string RawDateEvidence,
    string RawAmountEvidence,
    string? RawControlEvidence);

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
    string StatementPeriod,
    string AccountEvidence,
    IReadOnlyList<SourceRecordEvidence> OrderedRecords,
    IReadOnlyList<ReconciliationControl> Controls);
