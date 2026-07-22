using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Relationships;

public sealed class RelationshipLifecycleOperationModule(RelationshipLifecycleHandler lifecycle, GetRelationshipHandler get)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transfer.revoke" => lifecycle.RevokeTransferAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RevokeRelationshipInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transfer.replace" => lifecycle.ReplaceTransferAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReplaceTransferInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.refund.revoke" => lifecycle.RevokeRefundAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RevokeRelationshipInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.refund.replace" => lifecycle.ReplaceRefundAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReplaceRefundInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.relationship.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetRelationshipInput)!, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class RelationshipLifecycleOperationHandler(RelationshipLifecycleOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
