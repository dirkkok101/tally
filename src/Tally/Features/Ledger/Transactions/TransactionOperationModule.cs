using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Features.Ledger.Transactions;

public sealed class TransactionOperationModule(
    RecordTransactionHandler record,
    GetTransactionHandler get,
    TransactionCorrectionHandler? correction = null)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transaction.record" => record.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RecordTransactionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetTransactionInput)!, cancellationToken),
        "ledger.transaction.void" when correction is not null => correction.VoidAsync(JsonSerializer.Deserialize(request.Input, TransactionCorrectionJsonContext.Default.VoidTransactionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.supersede" when correction is not null => correction.SupersedeAsync(JsonSerializer.Deserialize(request.Input, TransactionCorrectionJsonContext.Default.SupersedeTransactionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.void" or "ledger.transaction.supersede" => Task.FromResult(CommandResult<JsonElement>.Failure("operation.unavailable")),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class TransactionOperationHandler(TransactionOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
