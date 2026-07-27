using System.Runtime.Versioning;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;

ProcessResult result;
using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
try
{
    result = await RunAsync(args, cancellationSource.Token);
}
catch
{
    result = TallyProcess.UnexpectedFailure();
}

Console.Out.WriteLine(result.Stdout);
if (!string.IsNullOrEmpty(result.Stderr))
{
    Console.Error.WriteLine(result.Stderr);
}

return result.ExitCode;

[SupportedOSPlatform("linux")]
static async Task<ProcessResult> RunAsync(string[] args, CancellationToken cancellationToken)
{
    LedgerDb? database = null;
    var dataRoot = Environment.GetEnvironmentVariable("TALLY_DATA_ROOT");
    if (!string.IsNullOrWhiteSpace(dataRoot))
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        }

        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(dataRoot, cancellationToken);
    }

    var registry = OperationRegistry.Create();
    var services = database is null
        ? LedgerServices.Create()
        : LedgerServices.Create(database);

    // Bootstrap process for LedgerContractClient; then attach INGEST modules that consume it.
    var bootstrapProcess = new TallyProcess(registry, services);
    if (database is not null && !string.IsNullOrWhiteSpace(dataRoot) && OperatingSystem.IsLinux())
    {
        var ledgerClient = new LedgerContractClient(registry, bootstrapProcess);
        var ingest = IngestOperationBundle.CreateServices(dataRoot, ledgerClient);
        services = services with { Ingest = ingest.Operations };
    }

    var process = new TallyProcess(registry, services);
    var stdin = Console.IsInputRedirected
        ? await Console.In.ReadToEndAsync(cancellationToken)
        : null;
    return await process.RunAsync(args, stdin, cancellationToken);
}
