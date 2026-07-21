using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;

namespace Tally.Features.Ledger.Categories;

public sealed class CategoryOperationModule(CreateCategoryHandler create, GetCategoryHandler get, ListCategoriesHandler list, RenameCategoryHandler rename, ReparentCategoryHandler reparent, ArchiveCategoryHandler archive, ReactivateCategoryHandler reactivate)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.category.create" => create.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CreateCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.category.get" => get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetCategoryInput)!, cancellationToken),
        "ledger.category.list" => list.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListCategoriesInput)!, cancellationToken),
        "ledger.category.rename" => rename.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RenameCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.category.reparent" => reparent.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReparentCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.category.archive" => archive.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ArchiveCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.category.reactivate" => reactivate.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReactivateCategoryInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class CategoryOperationHandler(CategoryOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
