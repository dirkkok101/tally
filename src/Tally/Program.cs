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

    // Descriptor-only registry: schema/help/unknown-op discovery never opens CLASSIFY state or corpus.
    var registry = OperationRegistry.Create();
    var services = database is null
        ? LedgerServices.Create()
        : LedgerServices.Create(database);

    // Bootstrap process for LedgerContractClient; then attach INGEST + BUDGET + complete CLASSIFY.
    var bootstrapProcess = new TallyProcess(registry, services);
    if (database is not null && !string.IsNullOrWhiteSpace(dataRoot) && OperatingSystem.IsLinux())
    {
        var ledgerClient = new LedgerContractClient(registry, bootstrapProcess);
        var ingest = IngestOperationBundle.CreateServices(dataRoot, ledgerClient);
        var budget = await BudgetOperationBundle.CreateServicesAsync(
            dataRoot, ledgerClient, cancellationToken: cancellationToken);

        ClassifyOperationBundle? classify = null;
        if (IsClassifyInvocation(args))
        {
            // Runtime path only: all twelve CLASSIFY handlers over owner-only state.
            classify = (await ClassifyOperationBundle.CreateServicesAsync(
                dataRoot,
                ledgerClient,
                cancellationToken: cancellationToken)).Operations;
        }

        services = services with
        {
            Ingest = ingest.Operations,
            Budget = budget.Operations,
            Classify = classify
        };
    }

    var process = new TallyProcess(registry, services);
    var stdin = Console.IsInputRedirected
        ? await Console.In.ReadToEndAsync(cancellationToken)
        : null;
    return await process.RunAsync(args, stdin, cancellationToken);
}

static bool IsClassifyInvocation(string[] args) =>
    args.Length > 0 && string.Equals(args[0], "classify", StringComparison.Ordinal);
