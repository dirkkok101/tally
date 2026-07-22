using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Evidence;

public sealed class EvidenceLinkOperationModule(LinkSupportingEvidenceHandler linkSupporting)
{
    public Task<CommandResult<JsonElement>> LinkSupportingAsync(OperationRequest request, CancellationToken cancellationToken) =>
        linkSupporting.HandleAsync(
            JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.LinkSupportingEvidenceInput)!,
            request.Actor,
            request.IdempotencyKey,
            cancellationToken);
}

internal sealed class EvidenceLinkOperationHandler(EvidenceLinkOperationModule module) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.LinkSupportingAsync(request, cancellationToken);
}
