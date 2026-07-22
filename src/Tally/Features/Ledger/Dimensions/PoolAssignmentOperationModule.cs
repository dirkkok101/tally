using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Dimensions;

public sealed class PoolAssignmentOperationModule(AssignPoolHandler assign, CorrectPoolHandler correct)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transaction.pool.assign" => assign.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.AssignPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.pool.correct" => correct.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CorrectPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}
internal sealed class PoolAssignmentOperationHandler(PoolAssignmentOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
