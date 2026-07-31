using System.Security.Cryptography;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Exact-byte SHA-256 fingerprint of a private corpus file
/// (DM-CLASSIFY-VALIDATION-RUN / DD-CLASSIFY-PRIVATE-VALIDATION).
/// </summary>
public sealed class CorpusFingerprint : IEquatable<CorpusFingerprint>
{
    private CorpusFingerprint(string sha256Hex, long byteLength)
    {
        Sha256Hex = sha256Hex;
        ByteLength = byteLength;
    }

    /// <summary>Lowercase hex SHA-256 (64 characters) of the exact file bytes.</summary>
    public string Sha256Hex { get; }

    /// <summary>Exact byte length of the fingerprinted payload.</summary>
    public long ByteLength { get; }

    public static CorpusFingerprint FromExactBytes(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new CorpusFingerprint(hex, bytes.Length);
    }

    public static async Task<CorpusFingerprint> FromStreamAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new PrivateCorpusLimitException(PrivateCorpusErrors.LimitExceeded);
            }

            hasher.AppendData(buffer.AsSpan(0, read));
        }

        var digest = hasher.GetHashAndReset();
        return new CorpusFingerprint(Convert.ToHexStringLower(digest), total);
    }

    public bool Equals(CorpusFingerprint? other) =>
        other is not null
        && string.Equals(Sha256Hex, other.Sha256Hex, StringComparison.Ordinal)
        && ByteLength == other.ByteLength;

    public override bool Equals(object? obj) => obj is CorpusFingerprint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Sha256Hex, ByteLength);

    public override string ToString() => Sha256Hex;
}
