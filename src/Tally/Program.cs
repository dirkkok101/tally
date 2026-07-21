using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Infrastructure.Storage;

ProcessResult result;
using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
try
{
    var dataRoot = Environment.GetEnvironmentVariable("TALLY_DATA_ROOT");
    if (!string.IsNullOrWhiteSpace(dataRoot))
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        }

        await LedgerRuntimeBootstrap.InitializeCurrentAsync(dataRoot, cancellationSource.Token);
    }

    var process = new TallyProcess(OperationRegistry.Create());
    result = await process.RunAsync(args, Console.IsInputRedirected ? await Console.In.ReadToEndAsync(cancellationSource.Token) : null, cancellationSource.Token);
}
catch { result = TallyProcess.UnexpectedFailure(); }
Console.Out.WriteLine(result.Stdout);
if (!string.IsNullOrEmpty(result.Stderr)) Console.Error.WriteLine(result.Stderr);
return result.ExitCode;
