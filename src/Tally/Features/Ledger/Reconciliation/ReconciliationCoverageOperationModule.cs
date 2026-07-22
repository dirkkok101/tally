using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationCoverageOperationModule(
    CompleteStatementCoverageHandler complete,
    GetStatementCoverageHandler get)
{
    public const string CompleteOperationId = "ledger.reconciliation.coverage.complete";
    public const string GetOperationId = "ledger.reconciliation.coverage.get";

    public async Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return operationId switch
            {
                CompleteOperationId => await Complete(request, cancellationToken),
                GetOperationId => await Get(request, cancellationToken),
                _ => CommandResult<JsonElement>.Failure("operation.not_found")
            };
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.InvalidInput);
        }
    }

    private async Task<CommandResult<JsonElement>> Complete(OperationRequest request, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize(
            request.Input,
            ReconciliationCoverageJsonContext.Default.CompleteStatementCoverageInput);
        return input is null
            ? CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.InvalidInput)
            : await complete.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> Get(OperationRequest request, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize(
            request.Input,
            ReconciliationCoverageJsonContext.Default.GetStatementCoverageInput);
        return input is null
            ? CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.InvalidInput)
            : await get.HandleAsync(input, cancellationToken);
    }
}

internal sealed class ReconciliationCoverageOperationHandler(
    ReconciliationCoverageOperationModule module,
    string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.HandleAsync(operationId, request, cancellationToken);
}
