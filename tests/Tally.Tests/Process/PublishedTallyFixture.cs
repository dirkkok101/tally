using System.Diagnostics;
using Xunit;

namespace Tally.Tests.Process;

public sealed class PublishedTallyFixture : IDisposable
{
    private readonly string? publishRoot;

    public PublishedTallyFixture()
    {
        var supplied = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            BinaryPath = Path.GetFullPath(supplied);
            return;
        }

        publishRoot = Path.Combine(Path.GetTempPath(), "tally-published-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishRoot);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot()
        };
        foreach (var argument in new[]
                 {
                     "publish", "src/Tally/Tally.csproj", "-c", "Release", "-r", "linux-x64",
                     "--self-contained", "true", "--no-restore", "-p:PublishAot=true", "-o", publishRoot
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Native publish failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        BinaryPath = Path.Combine(publishRoot, "tally");
    }

    public string BinaryPath { get; }

    public async Task<PublishedTallyResult> RunAsync(
        string dataRoot,
        IReadOnlyList<string> arguments,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(BinaryPath)
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

        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new(process.ExitCode, (await stdout).TrimEnd(), (await stderr).TrimEnd());
    }

    public void Dispose()
    {
        if (publishRoot is not null && Directory.Exists(publishRoot)) Directory.Delete(publishRoot, true);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Tally repository root.");
    }
}

public sealed record PublishedTallyResult(int ExitCode, string Stdout, string Stderr);

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PublishedTallyCollection : ICollectionFixture<PublishedTallyFixture>
{
    public const string Name = "published-tally";
}
