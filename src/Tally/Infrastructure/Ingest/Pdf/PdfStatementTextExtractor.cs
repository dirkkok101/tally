using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Tally.Contracts.Ingest;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-DOCUMENT-EXTRACTION
public sealed class PdfStatementTextExtractor
{
    private static readonly NearestNeighbourWordExtractor WordExtractor = new(
        new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions
        {
            MaxDegreeOfParallelism = 1
        });

    private static readonly DefaultPageSegmenter PageSegmenter = new(
        new DefaultPageSegmenter.DefaultPageSegmenterOptions
        {
            MaxDegreeOfParallelism = 1
        });

    public ValueTask<PdfExtractionResult> ExtractAsync(
        ImmutableArray<byte> source,
        PdfExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();

        if (limits.MaxDuration <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-RESOURCE-TIME",
                IngestErrorCategory.Resource,
                "The statement exceeded the configured processing-time limit."));
        }

        if (source.IsDefaultOrEmpty)
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-MALFORMED",
                IngestErrorCategory.UnsafeSource,
                "The statement is not a readable PDF document."));
        }

        if (limits.MaxBytes < 0 || source.Length > limits.MaxBytes)
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-RESOURCE-BYTES",
                IngestErrorCategory.Resource,
                "The statement exceeded the configured byte limit."));
        }

        if (limits.MaxPages <= 0 || limits.MaxGlyphs <= 0)
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-RESOURCE-LIMIT",
                IngestErrorCategory.Resource,
                "The statement extraction limits are invalid."));
        }

        try
        {
            using var document = PdfDocument.Open(source.AsMemory());
            cancellationToken.ThrowIfCancellationRequested();

            if (timer.Elapsed >= limits.MaxDuration)
            {
                return ValueTask.FromResult(Failure(
                    "INGEST-PDF-RESOURCE-TIME",
                    IngestErrorCategory.Resource,
                    "The statement exceeded the configured processing-time limit."));
            }

            if (document.NumberOfPages > limits.MaxPages)
            {
                return ValueTask.FromResult(Failure(
                    "INGEST-PDF-RESOURCE-PAGES",
                    IngestErrorCategory.Resource,
                    "The statement exceeded the configured page limit."));
            }

            var pages = new List<PdfPageEvidence>(document.NumberOfPages);
            long glyphCount = 0;

            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= limits.MaxDuration)
                {
                    return ValueTask.FromResult(Failure(
                        "INGEST-PDF-RESOURCE-TIME",
                        IngestErrorCategory.Resource,
                        "The statement exceeded the configured processing-time limit."));
                }

                var page = document.GetPage(pageNumber);
                var glyphs = new List<PdfGlyphEvidence>(page.Letters.Count);
                foreach (var letter in page.Letters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    glyphCount++;
                    if (glyphCount > limits.MaxGlyphs)
                    {
                        return ValueTask.FromResult(Failure(
                            "INGEST-PDF-RESOURCE-GLYPHS",
                            IngestErrorCategory.Resource,
                            "The statement exceeded the configured glyph limit."));
                    }

                    var rectangle = letter.BoundingBox;
                    var baselineY = letter.StartBaseLine.Y;
                    if (!CoordinatesAreFinite(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top, baselineY))
                    {
                        return ValueTask.FromResult(Failure(
                            "INGEST-PDF-MALFORMED",
                            IngestErrorCategory.UnsafeSource,
                            "The statement contains invalid PDF coordinates."));
                    }

                    glyphs.Add(new PdfGlyphEvidence(
                        letter.Value,
                        rectangle.Left,
                        rectangle.Bottom,
                        rectangle.Right,
                        rectangle.Top,
                        glyphs.Count,
                        baselineY,
                        letter.TextSequence));
                }

                var managedLines = CaptureManagedLines(page.Letters, glyphCount, limits, timer, cancellationToken);
                if (managedLines is null)
                {
                    return ValueTask.FromResult(Failure(
                        "INGEST-PDF-RESOURCE-TIME",
                        IngestErrorCategory.Resource,
                        "The statement exceeded the configured processing-time limit."));
                }

                pages.Add(new PdfPageEvidence(pageNumber, page.Width, page.Height, glyphs, managedLines));
            }

            if (glyphCount == 0)
            {
                return ValueTask.FromResult(Failure(
                    "INGEST-PDF-UNSUPPORTED-SCAN",
                    IngestErrorCategory.Unsupported,
                    "The statement contains no extractable text evidence."));
            }

            if (timer.Elapsed >= limits.MaxDuration)
            {
                return ValueTask.FromResult(Failure(
                    "INGEST-PDF-RESOURCE-TIME",
                    IngestErrorCategory.Resource,
                    "The statement exceeded the configured processing-time limit."));
            }

            var evidence = new PdfDocumentEvidence(
                Convert.ToHexStringLower(SHA256.HashData(source.AsSpan())),
                source.Length,
                pages);
            return ValueTask.FromResult(new PdfExtractionResult(evidence, null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-UNSUPPORTED-ENCRYPTION",
                IngestErrorCategory.Unsupported,
                "The statement requires an unsupported opening password."));
        }
        catch (Exception exception) when (IsRecoverableParserFailure(exception))
        {
            return ValueTask.FromResult(Failure(
                "INGEST-PDF-MALFORMED",
                IngestErrorCategory.UnsafeSource,
                "The statement is not a readable PDF document."));
        }
    }

    private static List<PdfManagedLineEvidence>? CaptureManagedLines(
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters,
        long currentGlyphCount,
        PdfExtractionLimits limits,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timer.Elapsed >= limits.MaxDuration)
        {
            return null;
        }

        // Bounded passive extraction only: deterministic single-threaded PdfPig layout analysis.
        var words = WordExtractor.GetWords(letters).ToArray();
        if (timer.Elapsed >= limits.MaxDuration)
        {
            return null;
        }

        var blocks = PageSegmenter.GetBlocks(words);
        var managed = new List<PdfManagedLineEvidence>();
        var blockOrder = 0;
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= limits.MaxDuration)
            {
                return null;
            }

            var lineOrder = 0;
            foreach (var line in block.TextLines)
            {
                var box = line.BoundingBox;
                if (!CoordinatesAreFinite(box.Left, box.Bottom, box.Right, box.Top))
                {
                    continue;
                }

                managed.Add(new PdfManagedLineEvidence(
                    blockOrder,
                    lineOrder,
                    line.Text ?? string.Empty,
                    box.Left,
                    box.Bottom,
                    box.Right,
                    box.Top));
                lineOrder++;
            }

            blockOrder++;
        }

        // Glyph budget already enforced; managed line count is derived from the same page letters.
        _ = currentGlyphCount;
        return managed;
    }

    private static bool CoordinatesAreFinite(params double[] values) => values.All(double.IsFinite);

    private static bool IsRecoverableParserFailure(Exception exception) => exception is not OutOfMemoryException;

    private static PdfExtractionResult Failure(string code, IngestErrorCategory category, string safeMessage) =>
        new(null, new IngestError(
            code,
            category,
            safeMessage,
            null,
            null,
            MutationPossibility.None,
            null,
            IngestRetryAction.CorrectSource,
            "source"));
}
