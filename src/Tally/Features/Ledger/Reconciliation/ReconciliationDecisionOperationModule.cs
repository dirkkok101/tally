using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationDecisionOperationModule(
    GetReconciliationDecisionHandler get,
    ReconciliationDecisionMutationHandler mutation)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (operationId)
            {
                case "ledger.reconciliation.decision.get":
                    var getInput = JsonSerializer.Deserialize(request.Input, ReconciliationDecisionJsonContext.Default.GetReconciliationDecisionInput);
                    return getInput is null ? Invalid() : await get.HandleAsync(getInput, cancellationToken);
                case "ledger.reconciliation.decision.confirm":
                    var confirmInput = JsonSerializer.Deserialize(request.Input, ReconciliationDecisionJsonContext.Default.ConfirmReconciliationDecisionInput);
                    return confirmInput is null ? Invalid() : await mutation.ConfirmAsync(confirmInput, request.Actor, request.IdempotencyKey, cancellationToken);
                case "ledger.reconciliation.decision.reject":
                    var rejectInput = JsonSerializer.Deserialize(request.Input, ReconciliationDecisionJsonContext.Default.RejectReconciliationDecisionInput);
                    return rejectInput is null ? Invalid() : await mutation.RejectAsync(rejectInput, request.Actor, request.IdempotencyKey, cancellationToken);
                case "ledger.reconciliation.decision.revoke":
                    var revokeInput = JsonSerializer.Deserialize(request.Input, ReconciliationDecisionJsonContext.Default.RevokeReconciliationDecisionInput);
                    return revokeInput is null ? Invalid() : await mutation.RevokeAsync(revokeInput, request.Actor, request.IdempotencyKey, cancellationToken);
                case "ledger.reconciliation.decision.replace":
                    var replaceInput = JsonSerializer.Deserialize(request.Input, ReconciliationDecisionJsonContext.Default.ReplaceReconciliationDecisionInput);
                    return replaceInput is null ? Invalid() : await mutation.ReplaceAsync(replaceInput, request.Actor, request.IdempotencyKey, cancellationToken);
                default:
                    return CommandResult<JsonElement>.Failure("operation.not_found");
            }
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    private static CommandResult<JsonElement> Invalid() => CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.InvalidInput);
}

internal sealed class ReconciliationDecisionOperationHandler(ReconciliationDecisionOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.HandleAsync(operationId, request, cancellationToken);
}
