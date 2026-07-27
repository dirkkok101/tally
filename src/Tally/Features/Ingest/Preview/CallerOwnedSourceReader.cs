using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Tally.Features.Ingest.Preview;

public sealed record CallerOwnedSourceSnapshot(
    ImmutableArray<byte> Bytes,
    string SourceFingerprint,
    long ByteLength,
    DateTimeOffset LastWriteTimeUtc);

public sealed record CallerOwnedSourceReadResult(
    CallerOwnedSourceSnapshot? Snapshot,
    string? ErrorCode,
    string? SafeMessage);

// DD-INGEST-ARTIFACT-SECURITY
[SupportedOSPlatform("linux")]
public sealed class CallerOwnedSourceReader
{
    public const string PathInvalid = "INGEST-PREVIEW-SOURCE-PATH-INVALID";
    public const string SourceUnreadable = "INGEST-PREVIEW-SOURCE-UNREADABLE";
    public const string SourceChanged = "INGEST-PREVIEW-SOURCE-CHANGED";
    public const string SourceTooLarge = "INGEST-PREVIEW-SOURCE-TOO-LARGE";

    public CallerOwnedSourceReadResult Read(string sourcePath, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !Path.IsPathRooted(sourcePath) ||
            sourcePath.Contains("..", StringComparison.Ordinal))
        {
            return Failure(PathInvalid, "The source path is invalid.");
        }

        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            var before = new FileInfo(fullPath);
            if (!before.Exists ||
                before.Attributes.HasFlag(FileAttributes.Directory) ||
                before.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Failure(SourceUnreadable, "The source could not be opened for read-only access.");
            }

            if (maxBytes < 0 || before.Length > maxBytes)
            {
                return Failure(SourceTooLarge, "The source exceeds the configured byte limit.");
            }

            var beforeWrite = before.LastWriteTimeUtc;
            var beforeLength = before.Length;
            byte[] bytes;
            using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length > maxBytes)
                {
                    return Failure(SourceTooLarge, "The source exceeds the configured byte limit.");
                }

                bytes = new byte[checked((int)stream.Length)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        return Failure(SourceUnreadable, "The source could not be read completely.");
                    }

                    offset += read;
                }
            }

            var after = new FileInfo(fullPath);
            if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite)
            {
                return Failure(SourceChanged, "The source changed while it was being read.");
            }

            // Re-hash on-disk bytes to detect content mutation with unchanged timestamps.
            var onDisk = File.ReadAllBytes(fullPath);
            var memoryDigest = SHA256.HashData(bytes);
            var diskDigest = SHA256.HashData(onDisk);
            if (onDisk.Length != bytes.Length ||
                !CryptographicOperations.FixedTimeEquals(memoryDigest, diskDigest))
            {
                return Failure(SourceChanged, "The source changed while it was being read.");
            }

            return new(
                new CallerOwnedSourceSnapshot(
                    ImmutableArray.Create(bytes),
                    Convert.ToHexStringLower(memoryDigest),
                    bytes.Length,
                    beforeWrite),
                null,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure(SourceUnreadable, "The source could not be opened for read-only access.");
        }
    }

    private static CallerOwnedSourceReadResult Failure(string code, string message) => new(null, code, message);
}
