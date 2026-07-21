using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Process;

[SupportedOSPlatform("linux")]
public sealed class PublishedBinaryCoreTests(PublishedBinaryFixture fixture) : IClassFixture<PublishedBinaryFixture>, IAsyncLifetime
{
    private const string ValidRequest = "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"published-core-test\"},\"input\":{}}";
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"tally-published-core-{Guid.NewGuid():N}");

    // TC-LEDGER-OFFLINE-SELF-CONTAINED
    [Fact]
    public void Published_artifact_is_a_native_executable()
    {
        Assert.True(File.Exists(fixture.BinaryPath));
        Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], File.ReadAllBytes(fixture.BinaryPath)[..4]);
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(fixture.BinaryPath) & UnixFileMode.UserExecute);
    }

    // DM-LEDGER-OPERATION-DESCRIPTOR, DM-LEDGER-STORE-GENERATION
    [Fact]
    public async Task Typed_probe_emits_one_success_envelope_and_initializes_the_current_store()
    {
        var result = await RunAsync(ValidRequest, ["version", "--input", "-"]);

        Assert.Equal(0, result.ExitCode);
        AssertEnvelope(result.Stdout, "system.version", "success");
        var database = await CurrentDatabaseAsync();
        await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    // DM-LEDGER-STORE-GENERATION
    [Fact]
    public async Task Published_store_contains_every_v1_and_v2_migration_record()
    {
        Assert.Equal(0, (await RunAsync(ValidRequest, ["version", "--input", "-"])).ExitCode);
        var database = await CurrentDatabaseAsync();
        await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fragment_name FROM migration_metadata ORDER BY version, fragment_name;";
        await using var reader = await command.ExecuteReaderAsync();
        var fragments = new List<string>();
        while (await reader.ReadAsync()) fragments.Add(reader.GetString(0));

        Assert.Equal(CompleteLedgerSchema.CurrentFragmentNames.Order(StringComparer.Ordinal), fragments.Order(StringComparer.Ordinal));
    }

    // TC-LEDGER-OFFLINE-SELF-CONTAINED
    [Fact]
    public async Task Repeated_typed_probe_is_stable_and_does_not_create_another_generation()
    {
        var first = await RunAsync(ValidRequest, ["version", "--input", "-"]);
        var current = await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT"));
        var second = await RunAsync(ValidRequest, ["version", "--input", "-"]);

        Assert.Equal(first.Stdout, second.Stdout);
        Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT")));
        Assert.Single(Directory.GetDirectories(Path.Combine(dataRoot, "generations")));
    }

    // ADR-CORE-0030, TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public async Task Published_schema_is_provider_neutral_and_complete()
    {
        var result = await RunAsync(ValidRequest, ["schema", "list", "--input", "-"]);
        using var envelope = JsonDocument.Parse(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(73, envelope.RootElement.GetProperty("result").GetProperty("operations").GetArrayLength());
        foreach (var canary in new[] { "mailbox", "mime", "recipient", "whatsapp", "providerCursor", "rawPayload" })
        {
            Assert.DoesNotContain(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Malformed_typed_input_has_one_stable_error_envelope()
    {
        var result = await RunAsync("{", ["version", "--input", "-"]);

        Assert.Equal(3, result.ExitCode);
        AssertEnvelope(result.Stdout, "system.process", "error", "validation.invalid_input");
        Assert.Equal("tally: validation.invalid_input", result.Stderr.Trim());
    }

    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Sensitive_input_path_is_not_echoed_by_the_published_process()
    {
        const string secretPath = "/private/bank/mailbox/message.eml";
        var result = await RunAsync(null, ["version", "--input", secretPath]);

        Assert.Equal(2, result.ExitCode);
        AssertEnvelope(result.Stdout, "system.process", "error", "usage.invalid_input_path");
        Assert.DoesNotContain(secretPath, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, result.Stderr, StringComparison.Ordinal);
    }

    // TC-LEDGER-OFFLINE-SELF-CONTAINED
    [Fact]
    public async Task Native_process_runs_with_an_invalid_dotnet_root()
    {
        var result = await RunAsync(ValidRequest, ["version", "--input", "-"], new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = "/definitely-not-a-runtime"
        });

        Assert.Equal(0, result.ExitCode);
        AssertEnvelope(result.Stdout, "system.version", "success");
    }

    // TC-LEDGER-OFFLINE-SELF-CONTAINED
    [Fact]
    public async Task Waiting_published_process_has_no_child_process_or_socket()
    {
        using var process = Start(["version", "--input", "-"]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await WaitForFileAsync(Path.Combine(dataRoot, "CURRENT"), process);

        var children = await File.ReadAllTextAsync($"/proc/{process.Id}/task/{process.Id}/children");
        Assert.True(string.IsNullOrWhiteSpace(children));
        Assert.DoesNotContain(Directory.EnumerateFiles($"/proc/{process.Id}/fd"), HasSocketTarget);

        await process.StandardInput.WriteAsync(ValidRequest);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        AssertEnvelope(await stdout, "system.version", "success");
        Assert.True(string.IsNullOrWhiteSpace(await stderr));
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(dataRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        return Task.CompletedTask;
    }

    private async Task<PublishedProcessResult> RunAsync(string? input, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = Start(arguments, environment);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new(process.ExitCode, (await stdout).TrimEnd(), (await stderr).TrimEnd());
    }

    private System.Diagnostics.Process Start(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        if (environment is not null)
        {
            foreach (var variable in environment) start.Environment[variable.Key] = variable.Value;
        }

        return Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
    }

    private async Task<LedgerDb> CurrentDatabaseAsync()
    {
        var generationId = (await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT"))).Trim();
        return new LedgerDb(dataRoot, generationId);
    }

    private static async Task WaitForFileAsync(string path, System.Diagnostics.Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException("Published process exited before initializing storage.");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static bool HasSocketTarget(string descriptor)
    {
        try { return new FileInfo(descriptor).LinkTarget?.StartsWith("socket:[", StringComparison.Ordinal) is true; }
        catch (IOException) { return false; }
    }

    private static void AssertEnvelope(string stdout, string operationId, string outcome, string? errorCode = null)
    {
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(outcome, document.RootElement.GetProperty("outcome").GetString());
        if (errorCode is not null) Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PublishedProcessResult(int ExitCode, string Stdout, string Stderr);
}

[SupportedOSPlatform("linux")]
public sealed class PublishedBinaryFixture : IDisposable
{
    private readonly string? ownedRoot;

    public PublishedBinaryFixture()
    {
        var supplied = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            BinaryPath = Path.GetFullPath(supplied);
            return;
        }

        ownedRoot = Path.Combine(Path.GetTempPath(), $"tally-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        var repositoryRoot = FindRepositoryRoot();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        foreach (var argument in new[]
                 {
                     "publish", "src/Tally/Tally.csproj", "-c", "Release", "-r", "linux-x64",
                     "--self-contained", "true", "--no-restore", "-p:PublishAot=true", "-o", ownedRoot
                 }) start.ArgumentList.Add(argument);
        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Native publish failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        BinaryPath = Path.Combine(ownedRoot, "tally");
    }

    public string BinaryPath { get; }

    public void Dispose()
    {
        if (ownedRoot is not null && Directory.Exists(ownedRoot)) Directory.Delete(ownedRoot, true);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Tally repository root.");
    }
}
