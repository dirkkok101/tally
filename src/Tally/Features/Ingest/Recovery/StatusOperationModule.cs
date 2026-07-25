using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Recovery;

[SupportedOSPlatform("linux")]
public sealed class StatusOperationModule(StatusHandler handler)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Status,
            "tally ingest status",
            "query",
            false,
            IngestJsonContext.Default.IngestStatusInput,
            IngestJsonContext.Default.IngestStatusResult,
            "StatusOperationModule.Status",
            (_, _) => new StatusOperationHandler(handler),
            "tally ingest status --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId == IngestOperationIds.Status
        ? DispatchAsync(handler, request, cancellationToken)
        : Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"));

    private static async Task<CommandResult<JsonElement>> DispatchAsync(
        StatusHandler statusHandler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.IngestStatusInput);
            if (input is null) return CommandResult<JsonElement>.Failure(StatusErrors.InvalidInput);
            var result = await statusHandler.HandleAsync(new(input.BatchId, input.Limit, input.Cursor), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.IngestStatusResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(StatusErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(StatusErrors.InvalidInput, "validation", 3),
        new(StatusErrors.BatchNotFound, "not_found", 4),
        new(StatusErrors.SnapshotBusy, "conflict", 5),
        new(StatusErrors.SnapshotExpired, "lifecycle", 6),
        new(StatusErrors.CursorInvalid, "compatibility", 7),
        new(StatusErrors.ContractMismatch, "compatibility", 7),
        new(StatusErrors.GenerationMismatch, "compatibility", 7),
        new(StatusErrors.SnapshotNotFound, "not_found", 4)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class StatusOperationHandler(StatusHandler handler) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        new StatusOperationModule(handler).HandleAsync(IngestOperationIds.Status, request, cancellationToken);
}
