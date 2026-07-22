using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationApplyOperationModule(
    ReconciliationApplyHandler handler,
    StatementAuthoritativeCorrectionCoordinator? correctionCoordinator = null)
{
    public const string OperationId = "ledger.reconciliation.apply";

    public async Task<CommandResult<JsonElement>> ApplyAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, ReconciliationApplyJsonContext.Default.ReconciliationApplyInput);
            if (input is null) return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.InvalidInput);
            if (input.Disposition != ReconciliationApplyDisposition.CorrectExistingFromStatement)
                return await handler.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
            return correctionCoordinator is null
                ? CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.UnsupportedStatementCorrection)
                : await correctionCoordinator.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.InvalidInput);
        }
    }
}

internal sealed class ReconciliationApplyOperationHandler(ReconciliationApplyOperationModule module) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.ApplyAsync(request, cancellationToken);
}
