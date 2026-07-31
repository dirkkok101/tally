using System.Text.Json.Serialization;

namespace Tally.Contracts.Common;

/// <summary>
/// Typed shared limit metadata for public operation descriptors (DM-CLASSIFY-OPERATION-CONTRACTS / C11).
/// Units: counts are inclusive maximums (accept at max, reject max+1); time is milliseconds; memory is bytes.
/// Use <see cref="NotApplicable"/> for dimensions that do not apply to an operation — never omit and never use 0 as “unknown”.
/// </summary>
public sealed record OperationLimits(
    [property: JsonRequired] long MaxTransactionCount,
    [property: JsonRequired] long MaxRuleCount,
    [property: JsonRequired] long MaxEvidenceRowCount,
    [property: JsonRequired] long MaxCorpusRowCount,
    [property: JsonRequired] long MaxMemoryBytes,
    [property: JsonRequired] long MaxProcessingTimeMs)
{
    /// <summary>Explicit inapplicable dimension (not an unknown-limit sentinel; 0 remains a real zero bound).</summary>
    public const long NotApplicable = -1;

    public bool IsTransactionCountApplicable => MaxTransactionCount != NotApplicable;
    public bool IsRuleCountApplicable => MaxRuleCount != NotApplicable;
    public bool IsEvidenceRowCountApplicable => MaxEvidenceRowCount != NotApplicable;
    public bool IsCorpusRowCountApplicable => MaxCorpusRowCount != NotApplicable;
    public bool IsMemoryApplicable => MaxMemoryBytes != NotApplicable;
    public bool IsProcessingTimeApplicable => MaxProcessingTimeMs != NotApplicable;

    /// <summary>
    /// Inclusive-max boundary: applicable dimensions accept <c>value &lt;= max</c> and reject <c>value == max + 1</c>.
    /// Inapplicable dimensions always pass the count check.
    /// </summary>
    public bool AcceptsTransactionCount(long value) =>
        !IsTransactionCountApplicable || (value >= 0 && value <= MaxTransactionCount);

    public bool AcceptsRuleCount(long value) =>
        !IsRuleCountApplicable || (value >= 0 && value <= MaxRuleCount);

    public bool AcceptsEvidenceRowCount(long value) =>
        !IsEvidenceRowCountApplicable || (value >= 0 && value <= MaxEvidenceRowCount);

    public bool AcceptsCorpusRowCount(long value) =>
        !IsCorpusRowCountApplicable || (value >= 0 && value <= MaxCorpusRowCount);

    public bool AcceptsMemoryBytes(long value) =>
        !IsMemoryApplicable || (value >= 0 && value <= MaxMemoryBytes);

    public bool AcceptsProcessingTimeMs(long value) =>
        !IsProcessingTimeApplicable || (value >= 0 && value <= MaxProcessingTimeMs);
}
