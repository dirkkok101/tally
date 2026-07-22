using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Dimensions;

public sealed class SpendPoolOperationModule(
    CreateSpendPoolHandler create,
    GetSpendPoolHandler get,
    ListSpendPoolsHandler list,
    RenameSpendPoolHandler rename,
    ArchiveSpendPoolHandler archive,
    ReactivateSpendPoolHandler reactivate)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.pool.create" => create.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CreateSpendPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.pool.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetSpendPoolInput)!, cancellationToken),
        "ledger.pool.list" => list.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListSpendPoolsInput)!, cancellationToken),
        "ledger.pool.rename" => rename.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RenameSpendPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.pool.archive" => archive.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ArchiveSpendPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.pool.reactivate" => reactivate.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReactivateSpendPoolInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class SpendPoolOperationHandler(SpendPoolOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
