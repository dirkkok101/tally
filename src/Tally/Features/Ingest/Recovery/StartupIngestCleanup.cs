using System.Runtime.Versioning;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Recovery;

/// <summary>
/// Removes only known stale INGEST lock/atomic artifacts when no live lock is held.
/// Never touches sources, manifests, receipts, or Ledger data (DD-INGEST-STATE-STORE).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class StartupIngestCleanup(IngestDatabase database, BatchCommitLock batchLock, IngestArtifactProtection protection)
{
    public static readonly string[] KnownAtomicSuffixes = [".lock", ".atomic"];

    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken)
    {
        protection.EnsureOwnerOnlyDirectory(database.DataRoot);
        protection.EnsureOwnerOnlyDirectory(database.IngestDirectory);

        var removed = new List<string>();
        var lockDirectory = batchLock.LockDirectory;
        if (Directory.Exists(lockDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(lockDirectory, "*.lock", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                // Batch lock files are named after sanitized batch ids.
                await using var held = await batchLock.TryAcquireAsync(name, cancellationToken);
                if (held is null)
                {
                    continue; // live lock — leave untouched
                }

                // We hold the lock; the file is ours to clear after dispose.
                await held.DisposeAsync();
                try
                {
                    File.Delete(path);
                    removed.Add(path);
                }
                catch (IOException)
                {
                    // leave for next startup
                }
            }
        }

        // Known atomic sidecar next to the ingest database only.
        foreach (var suffix in KnownAtomicSuffixes)
        {
            var candidate = database.DatabasePath + suffix;
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                // Only remove when the main database is not exclusively locked by another process.
                await using var probe = new FileStream(
                    database.DatabasePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
                File.Delete(candidate);
                removed.Add(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // live database lock, shared use, or missing artifact
            }
        }

        return removed;
    }
}
