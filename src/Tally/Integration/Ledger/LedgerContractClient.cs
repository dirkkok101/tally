using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Integration.Ledger;

public sealed record LedgerContractResult<T>(int ExitCode, T? Value, ProcessError? Error, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

public sealed class LedgerContractClient(OperationRegistry registry, TallyProcess process)
{
    private const string AccountGet = "ledger.account.get";
    private const string TransactionRecord = "ledger.transaction.record";
    private const string TransactionGet = "ledger.transaction.get";

    public Task<LedgerContractResult<AccountDetail>> GetAccountAsync(
        string accountId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            AccountGet,
            contractVersion,
            actor,
            new GetAccountInput(accountId),
            null,
            LedgerJsonContext.Default.GetAccountInput,
            LedgerJsonContext.Default.AccountDetail,
            cancellationToken);

    public Task<LedgerContractResult<TransactionDetail>> RecordTransactionAsync(
        FrozenLedgerRecordRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.OperationId, TransactionRecord, StringComparison.Ordinal))
        {
            return Task.FromResult(Incompatible<TransactionDetail>());
        }

        return ExecuteAsync(
            request.OperationId,
            request.LedgerContractVersion,
            request.Actor,
            request.Input,
            request.IdempotencyKey,
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            cancellationToken);
    }

    public Task<LedgerContractResult<TransactionDetail>> GetTransactionAsync(
        string transactionId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            TransactionGet,
            contractVersion,
            actor,
            new GetTransactionInput(transactionId, IncludeHistory: false),
            null,
            LedgerJsonContext.Default.GetTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            cancellationToken);

    private async Task<LedgerContractResult<TResult>> ExecuteAsync<TInput, TResult>(
        string operationId,
        string contractVersion,
        SafeActor actor,
        TInput input,
        string? idempotencyKey,
        JsonTypeInfo<TInput> inputType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(operationId);
        if (descriptor is null
            || descriptor.RequestTypeInfo.Type != typeof(TInput)
            || descriptor.ResultTypeInfo.Type != typeof(TResult)
            || !SupportsVersion(descriptor, contractVersion))
        {
            return Incompatible<TResult>();
        }

        var inputElement = JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope(contractVersion, actor, inputElement, idempotencyKey);
        var requestJson = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = descriptor.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        var processResult = await process.RunAsync(arguments, requestJson, cancellationToken);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)
            ?? throw new InvalidOperationException("The public Ledger executor returned no result envelope.");

        if (processResult.ExitCode != 0)
        {
            return new(processResult.ExitCode, default, envelope.Error, processResult.Stderr);
        }

        if (envelope.Outcome != "success" || envelope.Result is null)
        {
            throw new InvalidOperationException("The public Ledger executor returned an invalid success envelope.");
        }

        var value = JsonSerializer.Deserialize(envelope.Result.Value, resultType)
            ?? throw new InvalidOperationException("The public Ledger executor returned no typed result.");
        return new(processResult.ExitCode, value, null, processResult.Stderr);
    }

    private static bool SupportsVersion(OperationDescriptor descriptor, string contractVersion) =>
        Version.TryParse(contractVersion, out var requested)
        && Version.TryParse(descriptor.MinimumContractVersion, out var minimum)
        && Version.TryParse(descriptor.MaximumContractVersion, out var maximum)
        && requested >= minimum
        && requested <= maximum;

    private static LedgerContractResult<T> Incompatible<T>() => new(
        7,
        default,
        new ProcessError("contract.incompatible", "compatibility", "The Ledger contract version or operation is not supported."),
        "tally: contract.incompatible");
}
