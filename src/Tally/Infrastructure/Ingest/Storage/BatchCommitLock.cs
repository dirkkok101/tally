using System.Runtime.Versioning;

namespace Tally.Infrastructure.Ingest.Storage;

/// <summary>
/// Owner-only, non-reentrant per-batch OS lock. Process loss releases the handle (DD-INGEST-COMMIT-RECOVERY).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BatchCommitLock(IngestDatabase database, IngestArtifactProtection protection)
{
    public string LockDirectory => Path.Combine(database.IngestDirectory, "locks");

    public string LockPath(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        // Keep lock filenames free of path separators while remaining stable per batch.
        var safe = batchId.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(LockDirectory, $"{safe}.lock");
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        cancellationToken.ThrowIfCancellationRequested();

        protection.EnsureOwnerOnlyDirectory(database.DataRoot);
        protection.EnsureOwnerOnlyDirectory(database.IngestDirectory);
        protection.EnsureOwnerOnlyDirectory(LockDirectory);

        var path = LockPath(batchId);
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            try
            {
                protection.EnsureOwnerOnly(path);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }

            return new HeldLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class HeldLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
