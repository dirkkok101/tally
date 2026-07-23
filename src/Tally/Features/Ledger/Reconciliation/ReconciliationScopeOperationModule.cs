using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationScopeOperationModule(RegisterReconciliationScopeHandler register)
{
    public const string RegisterOperationId = "ledger.reconciliation.scope.register";

    public async Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken)
    {
        if (operationId != RegisterOperationId) return CommandResult<JsonElement>.Failure("operation.not_found");
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, ReconciliationScopeJsonContext.Default.RegisterReconciliationScopeInput);
            return input is null ? CommandResult<JsonElement>.Failure(ReconciliationScopeErrors.InvalidInput)
                : await register.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
        }
        catch (JsonException) { return CommandResult<JsonElement>.Failure(ReconciliationScopeErrors.InvalidInput); }
    }
}

internal sealed class ReconciliationScopeOperationHandler(ReconciliationScopeOperationModule module) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.HandleAsync(ReconciliationScopeOperationModule.RegisterOperationId, request, cancellationToken);
}
