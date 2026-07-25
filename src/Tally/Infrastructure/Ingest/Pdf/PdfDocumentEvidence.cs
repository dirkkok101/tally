using Tally.Contracts.Ingest;

namespace Tally.Infrastructure.Ingest.Pdf;

// DM-INGEST-FORMAT-EVIDENCE
public sealed record PdfExtractionLimits(
    long MaxBytes,
    int MaxPages,
    long MaxGlyphs,
    TimeSpan MaxDuration)
{
    public static PdfExtractionLimits PrivateFixture { get; } = new(
        MaxBytes: 32L * 1024 * 1024,
        MaxPages: 64,
        MaxGlyphs: 2_000_000,
        MaxDuration: TimeSpan.FromSeconds(5));
}

public sealed record PdfGlyphEvidence(
    string Value,
    double Left,
    double Bottom,
    double Right,
    double Top,
    int ContentOrder);

public sealed record PdfPageEvidence(
    int PageNumber,
    double Width,
    double Height,
    IReadOnlyList<PdfGlyphEvidence> OrderedGlyphs);

public sealed record PdfDocumentEvidence(
    string SourceFingerprint,
    long ByteLength,
    IReadOnlyList<PdfPageEvidence> Pages);

public sealed record PdfExtractionResult(PdfDocumentEvidence? Evidence, IngestError? Error);
