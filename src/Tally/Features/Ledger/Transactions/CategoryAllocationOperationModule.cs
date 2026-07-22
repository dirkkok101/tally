using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Transactions;

public sealed class CategoryAllocationOperationModule(AssignCategoryHandler assign, CorrectCategoryHandler correct)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transaction.category.assign" => assign.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.AssignCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.category.correct" => correct.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CorrectCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class CategoryAllocationOperationHandler(CategoryAllocationOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.HandleAsync(operationId, request, cancellationToken);
}
