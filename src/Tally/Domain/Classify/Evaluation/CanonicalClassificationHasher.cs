using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tally.Domain.Classify.Evaluation;

/// <summary>
/// Byte-stable SHA-256 helpers for CLASSIFY evaluation fingerprints and evidence
/// (DD-CLASSIFY-DETERMINISTIC-EVALUATION / DM-CLASSIFY-EVALUATION-OUTCOME).
/// Culture-invariant; no clock, random, or host inputs.
/// </summary>
public static class CanonicalClassificationHasher
{
    /// <summary>SHA-256 hex (lowercase) of UTF-8 bytes of <paramref name="value"/>.</summary>
    public static string HashUtf8(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// SHA-256 hex over a length-framed sequence of parts.
    /// Null and non-null values are framed separately, so delimiters inside values cannot alias
    /// another logical sequence and the string <c>null</c> remains distinct from absence.
    /// </summary>
    public static string HashParts(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var sb = new StringBuilder(parts.Length * 40);
        foreach (var part in parts)
        {
            if (part is null)
            {
                sb.Append("N;");
                continue;
            }

            sb.Append('S')
                .Append(part.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(part)
                .Append(';');
        }

        return HashUtf8(sb.ToString());
    }

    /// <summary>SHA-256 hex of a culture-invariant integer written in decimal.</summary>
    public static string HashInt32(int value) =>
        HashUtf8(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>SHA-256 hex of a culture-invariant long written in decimal.</summary>
    public static string HashInt64(long value) =>
        HashUtf8(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Ordered payload hash with one length-framed logical record per input value.
    /// Empty sequence hashes the empty string; embedded newlines cannot alias record boundaries.
    /// </summary>
    public static string HashOrderedLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            sb.Append(line.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(line)
                .Append(';');
        }

        return HashUtf8(sb.ToString());
    }
}
