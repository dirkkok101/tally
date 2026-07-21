using System.Runtime.Versioning;
using System.Security.Cryptography;
using Tally.Infrastructure.Artifacts;
using Xunit;

namespace Tally.Tests.Infrastructure.Artifacts;

[SupportedOSPlatform("linux")]
public sealed class ArtifactReconcilerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-artifact-{Guid.NewGuid():N}");
    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact] public async Task Publish_writes_the_expected_checksum() { var path = Path.Combine(root, "safe-artifact"); var result = await new ArtifactReconciler().ReconcileAsync(path, "stable-content"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); Assert.Equal(Sha256("stable-content"u8.ToArray()), result); Assert.Equal("stable-content", await File.ReadAllTextAsync(path)); }
    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact] public async Task Retry_returns_existing_committed_artifact() { var path = Path.Combine(root, "safe-artifact"); var reconciler = new ArtifactReconciler(); await reconciler.ReconcileAsync(path, "stable-content"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); var result = await reconciler.ReconcileAsync(path, "ignored"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); Assert.Equal(Sha256("stable-content"u8.ToArray()), result); Assert.Equal("stable-content", await File.ReadAllTextAsync(path)); }
    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact] public async Task Retry_repairs_corrupt_destination() { var path = Path.Combine(root, "safe-artifact"); await File.WriteAllTextAsync(path, "corrupt"); var result = await new ArtifactReconciler().ReconcileAsync(path, "stable-content"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); Assert.Equal(Sha256("stable-content"u8.ToArray()), result); Assert.Equal("stable-content", await File.ReadAllTextAsync(path)); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Checksum_mismatch_does_not_publish_payload() { var path = Path.Combine(root, "safe-artifact"); await Assert.ThrowsAsync<InvalidOperationException>(() => new ArtifactReconciler().ReconcileAsync(path, "stable-content"u8.ToArray(), "wrong", CancellationToken.None)); Assert.False(File.Exists(path)); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Publication_and_retry_enforce_owner_only_permissions() { var directory = Path.Combine(root, "private"); var path = Path.Combine(directory, "safe-artifact"); var reconciler = new ArtifactReconciler(); await reconciler.ReconcileAsync(path, "stable-content"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); File.SetUnixFileMode(directory, UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); File.SetUnixFileMode(path, UnixFileMode.OtherRead | UnixFileMode.UserRead | UnixFileMode.UserWrite); await reconciler.ReconcileAsync(path, "ignored"u8.ToArray(), Sha256("stable-content"u8.ToArray()), CancellationToken.None); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory)); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)); }
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }
    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
