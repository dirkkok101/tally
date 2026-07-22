using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationProjectionOperationModule(ReconciliationProjectionHandler handler)
{
    public const string OperationId = "ledger.reconciliation.candidates";

    public async Task<CommandResult<JsonElement>> CandidatesAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                request.Input,
                ReconciliationProjectionJsonContext.Default.GetReconciliationCandidatesInput);
            return input is null
                ? CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.InvalidInput)
                : await handler.HandleAsync(input, cancellationToken);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.InvalidInput);
        }
    }
}

internal sealed class ReconciliationProjectionOperationHandler(ReconciliationProjectionOperationModule module) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.CandidatesAsync(request, cancellationToken);
}
