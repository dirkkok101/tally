using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Dimensions;

public sealed class PaymentAttributionOperationModule(AssignPaymentAttributionHandler assign, CorrectPaymentAttributionHandler correct)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.transaction.attribution.assign" => assign.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.AssignPaymentAttributionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.transaction.attribution.correct" => correct.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CorrectPaymentAttributionInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class PaymentAttributionOperationHandler(PaymentAttributionOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
