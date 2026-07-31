using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Deterministic private corpus bounds (NFR-CLASSIFY-BOUNDED-EVALUATION / C11).
/// Aligns with published rule-validation corpus row limits.
/// </summary>
public static class PrivateCorpusLimits
{
    /// <summary>Maximum JSONL data rows (excludes empty trailing line).</summary>
    public const int MaxRowCount = (int)ClassifyOperationModule.V1Limits.MaxCorpusRowCount;

    /// <summary>Maximum UTF-16 length of sourceDescription after JSON decode (matches NormalizerV1.MaxInputLength).</summary>
    public const int MaxDescriptionLength = 2048;

    /// <summary>Maximum UTF-16 length of identifier fields (transactionId, accountId, fingerprints, category ids).</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>Maximum UTF-8 bytes for one JSONL line including trailing newline.</summary>
    public const int MaxLineUtf8Bytes = 16_384;

    /// <summary>
    /// Maximum exact file size accepted for fingerprint + stream.
    /// Sized for max rows at max line length with a small envelope.
    /// </summary>
    public const long MaxFileUtf8Bytes = (long)MaxRowCount * MaxLineUtf8Bytes + 4_096;

    /// <summary>Published processing-time bound (ms) for validation corpus work.</summary>
    public const long MaxProcessingTimeMs = ClassifyOperationModule.V1Limits.MaxProcessingTimeMs;
}
