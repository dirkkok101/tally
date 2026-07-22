using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.Security;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class HostProtectionEvidenceTests(PublishedTallyFixture fixture)
{
    [Fact]
    public async Task EXT_LEDGER_HOST_OS_SECURITY_missing_deployment_evidence_fails_closed()
    {
        var result = await CheckEvidence(new Dictionary<string, string?>());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("host-protection.evidence_missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EXT_LEDGER_HOST_OS_SECURITY_ext4_claim_is_not_accepted_as_encryption_evidence()
    {
        await WithEvidence(
            "{\"schemaVersion\":1,\"protection\":\"ext4\",\"dataRoot\":\"/srv/tally\",\"verifiedBy\":\"owner\"}",
            async evidence =>
            {
                var result = await CheckEvidence(EnvironmentFor(evidence, "/srv/tally"));

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("host-protection.evidence_invalid", result.Stderr, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task EXT_LEDGER_HOST_OS_SECURITY_permissive_evidence_file_fails_closed()
    {
        await WithEvidence(ValidEvidence("/srv/tally"), async evidence =>
        {
            File.SetUnixFileMode(evidence, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            var result = await CheckEvidence(EnvironmentFor(evidence, "/srv/tally"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("host-protection.evidence_permissions", result.Stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EXT_LEDGER_HOST_OS_SECURITY_symlinked_evidence_file_fails_closed()
    {
        await WithEvidence(ValidEvidence("/srv/tally"), async evidence =>
        {
            var link = evidence + ".link";
            File.CreateSymbolicLink(link, evidence);

            var result = await CheckEvidence(EnvironmentFor(link, "/srv/tally"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("host-protection.evidence_invalid", result.Stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EXT_LEDGER_HOST_OS_SECURITY_owner_only_bound_attestation_is_accepted()
    {
        await WithEvidence(ValidEvidence("/srv/tally"), async evidence =>
        {
            var result = await CheckEvidence(EnvironmentFor(evidence, "/srv/tally"));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("host-protection: verified host-managed encrypted volume for configured data root", result.Stdout);
            Assert.True(string.IsNullOrEmpty(result.Stderr));
        });
    }

    [Fact]
    public void DD_LEDGER_EMBEDDED_STORAGE_contains_no_custom_encryption_or_key_derivation()
    {
        var source = SourceText();
        string[] forbidden =
        [
            "Aes.Create", "DES.Create", "TripleDES.Create", "RC2.Create", "Rijndael", "Rfc2898DeriveBytes",
            "PasswordDeriveBytes", "Argon2", "Scrypt", "libsodium", "ProtectedData.Protect"
        ];

        Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        Assert.Contains("SHA256", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TALLY_HERMES_BOUNDARY_has_no_provider_or_listener_dependency()
    {
        var source = SourceText();
        string[] forbidden =
        [
            "using MailKit", "using MimeKit", "using Twilio", "AgentMailClient", "WhatsAppClient",
            "HttpListener", "TcpListener", "WebApplication.CreateBuilder", "UseKestrel", "Process.Start("
        ];

        Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TC_LEDGER_OFFLINE_SELF_CONTAINED_process_keeps_owner_identity_and_opens_no_socket_or_child()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "tally-security-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var process = StartPublished(dataRoot);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForFileAsync(Path.Combine(dataRoot, "CURRENT"), process);

            var status = await File.ReadAllLinesAsync($"/proc/{process.Id}/status");
            var effectiveUid = status.Single(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
            Assert.Equal(Environment.GetEnvironmentVariable("UID") ?? EffectiveUid(), effectiveUid);
            Assert.True(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync($"/proc/{process.Id}/task/{process.Id}/children")));
            Assert.DoesNotContain(Directory.EnumerateFiles($"/proc/{process.Id}/fd"), HasSocketTarget);

            await process.StandardInput.WriteAsync(EmptyRequest());
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(await stderr));
            using var envelope = JsonDocument.Parse(await stdout);
            Assert.Equal("system.version", envelope.RootElement.GetProperty("operationId").GetString());
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    private static async Task WithEvidence(string content, Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tally-host-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var evidence = Path.Combine(directory, "host-protection.json");
        await File.WriteAllTextAsync(evidence, content);
        File.SetUnixFileMode(evidence, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await test(evidence);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Dictionary<string, string?> EnvironmentFor(string evidence, string dataRoot) => new()
    {
        ["TALLY_HOST_PROTECTION_EVIDENCE_FILE"] = evidence,
        ["TALLY_DATA_ROOT"] = dataRoot
    };

    private static string ValidEvidence(string dataRoot) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        protection = "host-managed-encrypted-volume",
        dataRoot,
        verifiedBy = "deployment-owner"
    });

    private static async Task<ProcessResult> CheckEvidence(IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot()
        };
        start.ArgumentList.Add("scripts/verify-ledger-security.sh");
        start.ArgumentList.Add("--check-host-protection");
        foreach (var variable in environment) start.Environment[variable.Key] = variable.Value;
        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, (await stdout).TrimEnd(), (await stderr).TrimEnd());
    }

    private System.Diagnostics.Process StartPublished(string dataRoot)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("version");
        start.ArgumentList.Add("--input");
        start.ArgumentList.Add("-");
        start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        return Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
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

    private static string EffectiveUid()
    {
        var status = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
        return status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
    }

    private static string EmptyRequest() =>
        "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"security-process\"},\"input\":{}}";

    private static string SourceText() => string.Join('\n',
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Tally"), "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Tally repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
