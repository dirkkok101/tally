using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Tally.Infrastructure.Artifacts;

[SupportedOSPlatform("linux")]
public sealed class ArtifactReconciler
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<string> ReconcileAsync(string destinationPath, ReadOnlyMemory<byte> stableContent, string expectedChecksum, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) && string.Equals(await ChecksumAsync(destinationPath, cancellationToken), expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            ProtectDirectory(Path.GetDirectoryName(destinationPath));
            ProtectFile(destinationPath);
            return expectedChecksum;
        }

        var actualChecksum = Checksum(stableContent.Span);
        if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact checksum mismatch.");

        var directory = ProtectDirectory(Path.GetDirectoryName(destinationPath));
        var stagingPath = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + ".staging");
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await staging.WriteAsync(stableContent, cancellationToken);
                await staging.FlushAsync(cancellationToken);
                staging.Flush(flushToDisk: true);
            }
            ProtectFile(stagingPath);
            if (!string.Equals(await ChecksumAsync(stagingPath, cancellationToken), expectedChecksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact checksum mismatch.");
            File.Move(stagingPath, destinationPath, overwrite: true);
            ProtectFile(destinationPath);
            if (!string.Equals(await ChecksumAsync(destinationPath, cancellationToken), expectedChecksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact checksum mismatch.");
            return expectedChecksum;
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string Checksum(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string ProtectDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("An artifact destination directory is required.", "destinationPath");
        }

        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory);
        if (File.GetUnixFileMode(path) != OwnerDirectory)
        {
            throw new InvalidOperationException("The artifact directory is not owner-only.");
        }

        return path;
    }

    private static void ProtectFile(string path)
    {
        File.SetUnixFileMode(path, OwnerFile);
        if (File.GetUnixFileMode(path) != OwnerFile)
        {
            throw new InvalidOperationException("The artifact is not owner-only.");
        }
    }
}
