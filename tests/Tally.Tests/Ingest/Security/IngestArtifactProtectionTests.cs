using Tally.Infrastructure.Ingest.Storage;
using System.Runtime.Versioning;
using Xunit;

namespace Tally.Tests.Ingest.Security;

[SupportedOSPlatform("linux")]
public sealed class IngestArtifactProtectionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-security-{Guid.NewGuid():N}");
    private readonly IngestArtifactProtection protection = new();

    // TC-INGEST-ARTIFACT-PROTECTION
    [Fact]
    public void Directories_are_created_owner_only() { protection.EnsureOwnerOnlyDirectory(Path.Combine(root, "ingest")); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, "ingest"))); }

    // DD-INGEST-ARTIFACT-SECURITY
    [Fact]
    public void Existing_artifacts_are_restricted_to_the_owner()
    {
        Directory.CreateDirectory(root);
        var artifact = Path.Combine(root, "ingest.db");
        File.WriteAllText(artifact, "state");
        File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        protection.EnsureOwnerOnly(artifact);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(artifact));
    }

    // DD-INGEST-ARTIFACT-SECURITY
    [Fact]
    public void Missing_artifacts_block_persistence() => Assert.Throws<FileNotFoundException>(() => protection.EnsureOwnerOnly(Path.Combine(root, "missing")));

    // TC-INGEST-ARTIFACT-PROTECTION
    [Fact]
    public async Task Database_and_sqlite_sidecars_are_owner_only()
    {
        var database = new IngestDatabase(root, protection);
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand()) { command.CommandText = "CREATE TABLE sidecar_probe (id INTEGER PRIMARY KEY);"; await command.ExecuteNonQueryAsync(); }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath + "-wal"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath + "-shm"));
    }

    // TC-INGEST-ARTIFACT-PROTECTION
    [Theory]
    [InlineData(".lock")]
    [InlineData(".atomic")]
    public void Lock_and_atomic_artifacts_are_protected_when_persisted(string suffix)
    {
        Directory.CreateDirectory(root);
        var artifact = Path.Combine(root, "ingest.db" + suffix);
        File.WriteAllText(artifact, "state");

        protection.EnsureOwnerOnly(artifact);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(artifact));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(root)) { Directory.Delete(root, true); } return Task.CompletedTask; }
}
