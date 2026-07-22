using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Security;

[SupportedOSPlatform("linux")]
public sealed class ArtifactPermissionsTests : IAsyncLifetime
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-security-permissions-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;

    [Fact]
    public void TC_LEDGER_LOCAL_DATA_PROTECTION_bootstrap_artifacts_are_owner_only()
    {
        AssertDirectory(root);
        AssertDirectory(Path.Combine(root, "generations"));
        AssertDirectory(database.GenerationDirectory);
        AssertFile(database.DatabasePath);
        AssertFile(database.ManifestPath);
        AssertFile(Path.Combine(root, "CURRENT"));
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_wal_and_shm_are_owner_only_while_open()
    {
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(
            database,
            CompleteLedgerSchema.CurrentVersion,
            CancellationToken.None);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE; INSERT INTO account VALUES ('security-account', 'Bank', 'cheque', 'asset', '***1', 'ZAR', '2026-07-22T00:00:00Z'); ROLLBACK;");

        AssertFile(database.DatabasePath + "-wal");
        AssertFile(database.DatabasePath + "-shm");
    }

    [Fact]
    public void IHostArtifactProtection_repairs_a_permissive_directory()
    {
        var path = Directory.CreateDirectory(Path.Combine(root, "repair-directory")).FullName;
        File.SetUnixFileMode(path, OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.OtherExecute);

        protection.EnsureDataRoot(path);

        AssertDirectory(path);
    }

    [Fact]
    public async Task IHostArtifactProtection_repairs_a_permissive_regular_file()
    {
        var path = Path.Combine(root, "repair-file");
        await File.WriteAllTextAsync(path, "safe metadata");
        File.SetUnixFileMode(path, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        protection.ProtectArtifact(path);

        AssertFile(path);
    }

    [Fact]
    public async Task IHostArtifactProtection_rejects_a_permissive_regular_file()
    {
        var path = Path.Combine(root, "reject-file");
        await File.WriteAllTextAsync(path, "safe metadata");
        File.SetUnixFileMode(path, OwnerFile | UnixFileMode.GroupRead);

        Assert.Throws<InvalidOperationException>(() => protection.RequireOwnerOnlyArtifact(path));
    }

    [Fact]
    public void IHostArtifactProtection_rejects_a_permissive_directory()
    {
        var path = Directory.CreateDirectory(Path.Combine(root, "reject-directory")).FullName;
        File.SetUnixFileMode(path, OwnerDirectory | UnixFileMode.GroupExecute);

        Assert.Throws<InvalidOperationException>(() => protection.RequireOwnerOnlyDirectory(path));
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_existing_unsafe_store_fails_before_reopen()
    {
        var current = await File.ReadAllTextAsync(Path.Combine(root, "CURRENT"));
        File.SetUnixFileMode(database.DatabasePath, OwnerFile | UnixFileMode.GroupRead);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None));

        Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(root, "CURRENT")));
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_optional_guidance_artifacts_are_owner_only()
    {
        var scope = Path.Combine(root, "guidance-scope");
        Directory.CreateDirectory(scope);
        File.SetUnixFileMode(scope, OwnerDirectory);
        var request = Request(new InstallGuidanceInput("codex", scope), "security-guidance-install");

        var result = await new TallyProcess(OperationRegistry.Create()).RunAsync(
            ["system", "guidance", "install", "--input", "-"],
            request,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var installRoot = Path.Combine(scope, ".agents", "skills", "tally-ledger");
        AssertDirectory(installRoot);
        AssertFile(Path.Combine(installRoot, "SKILL.md"));
        AssertFile(Path.Combine(installRoot, ".tally-guidance.json"));
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_candidate_database_artifacts_are_owner_only()
    {
        var candidate = new LedgerDb(root, Guid.NewGuid().ToString("N"));
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(
            candidate,
            CompleteLedgerSchema.CurrentVersion,
            CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);

        AssertDirectory(candidate.GenerationDirectory);
        AssertFile(candidate.DatabasePath);
        AssertFile(candidate.DatabasePath + "-wal");
        AssertFile(candidate.DatabasePath + "-shm");
    }

    public async Task InitializeAsync() =>
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static void AssertDirectory(string path) => Assert.Equal(OwnerDirectory, File.GetUnixFileMode(path));
    private static void AssertFile(string path) => Assert.Equal(OwnerFile, File.GetUnixFileMode(path));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Request<T>(T input, string idempotencyKey) => JsonSerializer.Serialize(
        new RequestEnvelope(
            "1.0",
            new SafeActor("automation", "security-permissions"),
            JsonSerializer.SerializeToElement(input, typeof(T), LedgerJsonContext.Default),
            idempotencyKey),
        LedgerJsonContext.Default.RequestEnvelope);
}
