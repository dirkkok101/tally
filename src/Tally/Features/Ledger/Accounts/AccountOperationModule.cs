using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;

namespace Tally.Features.Ledger.Accounts;

public sealed class AccountOperationModule(
    CreateAccountHandler create,
    GetAccountHandler get,
    ListAccountsHandler list,
    RenameAccountHandler rename,
    ArchiveAccountHandler archive)
{
    public Task<CommandResult<JsonElement>> CreateAsync(OperationRequest request, CancellationToken cancellationToken) =>
        create.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CreateAccountInput)!, request.Actor, request.IdempotencyKey, cancellationToken);

    public Task<CommandResult<JsonElement>> GetAsync(OperationRequest request, CancellationToken cancellationToken) =>
        get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetAccountInput)!, cancellationToken);

    public Task<CommandResult<JsonElement>> ListAsync(OperationRequest request, CancellationToken cancellationToken) =>
        list.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListAccountsInput)!, cancellationToken);

    public Task<CommandResult<JsonElement>> RenameAsync(OperationRequest request, CancellationToken cancellationToken) =>
        rename.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RenameAccountInput)!, request.Actor, request.IdempotencyKey, cancellationToken);

    public Task<CommandResult<JsonElement>> ArchiveAsync(OperationRequest request, CancellationToken cancellationToken) =>
        archive.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ArchiveAccountInput)!, request.Actor, request.IdempotencyKey, cancellationToken);
}

internal sealed class AccountOperationHandler(AccountOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.account.create" => module.CreateAsync(request, cancellationToken),
        "ledger.account.get" => module.GetAsync(request, cancellationToken),
        "ledger.account.list" => module.ListAsync(request, cancellationToken),
        "ledger.account.rename" => module.RenameAsync(request, cancellationToken),
        "ledger.account.archive" => module.ArchiveAsync(request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}
