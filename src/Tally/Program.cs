using Tally.Cli;
using Tally.Contracts.Common;

ProcessResult result;
using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
try
{
    var process = new TallyProcess(OperationRegistry.Create());
    result = await process.RunAsync(args, Console.IsInputRedirected ? await Console.In.ReadToEndAsync(cancellationSource.Token) : null, cancellationSource.Token);
}
catch { result = TallyProcess.UnexpectedFailure(); }
Console.Out.WriteLine(result.Stdout);
if (!string.IsNullOrEmpty(result.Stderr)) Console.Error.WriteLine(result.Stderr);
return result.ExitCode;
