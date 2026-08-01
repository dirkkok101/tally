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
    // Resolve the static contract before any durable service is initialized. Discovery and
    // unknown-operation paths remain independent of Ledger, CLASSIFY state, and private corpus.
    var registry = OperationRegistry.Create();
    var requiresRuntime = RequiresRuntime(args, registry);
    LedgerDb? database = null;
    var dataRoot = Environment.GetEnvironmentVariable("TALLY_DATA_ROOT");
    if (requiresRuntime && !string.IsNullOrWhiteSpace(dataRoot))
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        }

        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(dataRoot, cancellationToken);
    }

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
        if (IsKnownClassifyInvocation(args, registry))
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

static bool RequiresRuntime(string[] args, OperationRegistry registry)
{
    if (args is ["version"] or ["help"] or ["schema", "list"] or ["schema", "show", _])
    {
        return false;
    }

    return ResolveRequestedDescriptor(args, registry) is not null;
}

static bool IsKnownClassifyInvocation(string[] args, OperationRegistry registry) =>
    ResolveRequestedDescriptor(args, registry)?.OperationId.StartsWith(
        "classify.", StringComparison.Ordinal) == true;

static OperationDescriptor? ResolveRequestedDescriptor(string[] args, OperationRegistry registry)
{
    var inputIndex = Array.IndexOf(args, "--input");
    var operationArguments = inputIndex < 0 ? args : args[..inputIndex];
    return registry.FindByArguments(operationArguments);
}
