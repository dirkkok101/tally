using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Evidence;

namespace Tally.Features.Ledger.Evidence;

public sealed class EvidenceRegistryOperationModule(RegisterEvidenceHandler register, GetEvidenceHandler get)
{
    public Task<CommandResult<JsonElement>> RegisterAsync(OperationRequest request, CancellationToken cancellationToken) =>
        register.HandleAsync(
            JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RegisterEvidenceInput)!,
            request.Actor,
            request.IdempotencyKey,
            cancellationToken);

    public Task<CommandResult<JsonElement>> GetAsync(OperationRequest request, CancellationToken cancellationToken) =>
        get.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetEvidenceInput)!, cancellationToken);
}

internal sealed class EvidenceRegistryOperationHandler(EvidenceRegistryOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.evidence.register" => module.RegisterAsync(request, cancellationToken),
        "ledger.evidence.get" => module.GetAsync(request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}
