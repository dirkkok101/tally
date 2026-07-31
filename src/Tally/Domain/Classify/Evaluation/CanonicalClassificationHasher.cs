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
    /// SHA-256 hex over a canonical pipe-joined sequence of parts.
    /// Null parts are encoded as the four characters <c>null</c> so absence is explicit.
    /// </summary>
    public static string HashParts(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var sb = new StringBuilder(parts.Length * 32);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }

            sb.Append(parts[i] ?? "null");
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
    /// Ordered multi-line payload hash: each line is one logical record, joined by LF only.
    /// Empty sequence hashes the empty string.
    /// </summary>
    public static string HashOrderedLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sb = new StringBuilder();
        var first = true;
        foreach (var line in lines)
        {
            if (!first)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            first = false;
        }

        return HashUtf8(sb.ToString());
    }
}
