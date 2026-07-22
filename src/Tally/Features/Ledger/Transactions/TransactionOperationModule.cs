using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Transactions;

public sealed class TransactionOperationModule(RecordTransactionHandler record, GetTransactionHandler get)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transaction.record" => record.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RecordTransactionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetTransactionInput)!, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class TransactionOperationHandler(TransactionOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
