using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;

namespace Tally.Features.System.Guidance;

public sealed class GuidanceOperationModule(GuidanceService service)
{
    public async Task<CommandResult<JsonElement>> ListAsync(OperationRequest request, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListGuidanceInput)!, registry, cancellationToken);
        return result.IsSuccess
            ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, LedgerJsonContext.Default.GuidanceListResult))
            : CommandResult<JsonElement>.Failure(result.ErrorCode!);
    }

    public async Task<CommandResult<JsonElement>> CheckAsync(OperationRequest request, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var result = await service.CheckAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CheckGuidanceInput)!, registry, cancellationToken);
        return result.IsSuccess
            ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, LedgerJsonContext.Default.GuidanceCheckResult))
            : CommandResult<JsonElement>.Failure(result.ErrorCode!);
    }

    public async Task<CommandResult<JsonElement>> InstallAsync(OperationRequest request, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var result = await service.InstallAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.InstallGuidanceInput)!, registry, cancellationToken);
        return result.IsSuccess
            ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, LedgerJsonContext.Default.GuidanceInstallResult))
            : CommandResult<JsonElement>.Failure(result.ErrorCode!);
    }
}

internal sealed class GuidanceOperationHandler(GuidanceOperationModule module, OperationRegistry registry, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "system.guidance.list" => module.ListAsync(request, registry, cancellationToken),
        "system.guidance.check" => module.CheckAsync(request, registry, cancellationToken),
        "system.guidance.install" => module.InstallAsync(request, registry, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}
