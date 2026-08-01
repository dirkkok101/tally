using System.Runtime.Versioning;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Owner-only, non-reentrant per-apply-run OS lock (DD-CLASSIFY-APPLY-SAGA).
/// Process loss releases the handle so crash recovery can re-acquire the same apply identity.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationApplyLock
{
    private readonly ClassifyStorePaths paths;
    private readonly HostArtifactProtection protection;

    public ClassificationApplyLock(ClassifyStorePaths paths, HostArtifactProtection? protection = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.paths = paths;
        this.protection = protection ?? new HostArtifactProtection();
    }

    public ClassificationApplyLock(string dataRoot, HostArtifactProtection? protection = null)
        : this(new ClassifyStorePaths(dataRoot), protection)
    {
    }

    public string LockDirectory => Path.Combine(paths.ClassifyDirectory, "locks");

    public string LockPath(string applyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        var safe = applyId.Trim().Replace("/", "_", StringComparison.Ordinal).Replace("\\", "_", StringComparison.Ordinal);
        return Path.Combine(LockDirectory, safe + ".lock");
    }

    /// <summary>
    /// Try to acquire exclusive ownership of <paramref name="applyId"/>.
    /// Returns null when another process holds the lock or the path is not owner-writable.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string applyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        cancellationToken.ThrowIfCancellationRequested();

        protection.EnsureDataRoot(paths.DataRoot);
        protection.EnsureDataRoot(paths.ClassifyDirectory);
        protection.EnsureDataRoot(LockDirectory);

        var path = LockPath(applyId);
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
                protection.ProtectArtifact(path);
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
