using System.Runtime.Versioning;
using Tally.Features.Ingest.Recovery;
using Tally.Infrastructure.Ingest.Storage;
using Xunit;

namespace Tally.Tests.Ingest.Recovery;

[SupportedOSPlatform("linux")]
public sealed class StartupCleanupTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-startup-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Startup_cleanup_removes_stale_lock_files()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        await using (var held = await locks.TryAcquireAsync("stale-batch", CancellationToken.None))
        {
            Assert.NotNull(held);
        }

        Assert.True(File.Exists(locks.LockPath("stale-batch")));
        var removed = await new StartupIngestCleanup(database, locks, protection).RunAsync(CancellationToken.None);
        Assert.Contains(removed, path => path.EndsWith("stale-batch.lock", StringComparison.Ordinal));
        Assert.False(File.Exists(locks.LockPath("stale-batch")));
    }

    [Fact]
    public async Task Startup_cleanup_preserves_live_locks()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        await using var held = await locks.TryAcquireAsync("live-batch", CancellationToken.None);
        Assert.NotNull(held);

        var removed = await new StartupIngestCleanup(database, locks, protection).RunAsync(CancellationToken.None);
        Assert.DoesNotContain(removed, path => path.Contains("live-batch", StringComparison.Ordinal));
        Assert.True(File.Exists(locks.LockPath("live-batch")));
    }

    [Fact]
    public async Task Startup_cleanup_ignores_unknown_files_in_lock_directory()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        protection.EnsureOwnerOnlyDirectory(database.DataRoot);
        protection.EnsureOwnerOnlyDirectory(database.IngestDirectory);
        protection.EnsureOwnerOnlyDirectory(locks.LockDirectory);
        var unknown = Path.Combine(locks.LockDirectory, "notes.txt");
        await File.WriteAllTextAsync(unknown, "not a lock");
        protection.EnsureOwnerOnly(unknown);

        var removed = await new StartupIngestCleanup(database, locks, protection).RunAsync(CancellationToken.None);
        Assert.DoesNotContain(unknown, removed);
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public async Task Startup_cleanup_does_not_touch_caller_owned_sources()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        var source = Path.Combine(root, "statement.pdf");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var before = await File.ReadAllBytesAsync(source);

        _ = await new StartupIngestCleanup(database, locks, protection).RunAsync(CancellationToken.None);
        Assert.True(File.Exists(source));
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Startup_cleanup_does_not_delete_ingest_database()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        }

        Assert.True(File.Exists(database.DatabasePath));
        _ = await new StartupIngestCleanup(database, new BatchCommitLock(database, protection), protection)
            .RunAsync(CancellationToken.None);
        Assert.True(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task Startup_cleanup_removes_stale_database_atomic_sidecar()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        }

        var atomic = database.DatabasePath + ".atomic";
        await File.WriteAllTextAsync(atomic, "stale");
        protection.EnsureOwnerOnly(atomic);

        var removed = await new StartupIngestCleanup(database, new BatchCommitLock(database, protection), protection)
            .RunAsync(CancellationToken.None);
        Assert.Contains(atomic, removed);
        Assert.False(File.Exists(atomic));
    }

    [Fact]
    public void Known_atomic_suffixes_are_explicit()
    {
        Assert.Equal([".lock", ".atomic"], StartupIngestCleanup.KnownAtomicSuffixes);
    }
}
