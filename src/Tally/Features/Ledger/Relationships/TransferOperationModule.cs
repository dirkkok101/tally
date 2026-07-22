using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Relationships;

public sealed class TransferOperationModule(ConfirmTransferHandler confirm, GetRelationshipHandler get)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transfer.confirm" => confirm.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ConfirmTransferInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.relationship.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetRelationshipInput)!, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class TransferOperationHandler(TransferOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
