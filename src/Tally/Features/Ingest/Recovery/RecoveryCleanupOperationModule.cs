using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Recovery;

[SupportedOSPlatform("linux")]
public sealed class RecoveryCleanupOperationModule(AbandonHandler abandonHandler, CleanupHandler cleanupHandler)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Abandon,
            "tally ingest abandon",
            "command",
            false,
            IngestJsonContext.Default.AbandonBatchInput,
            IngestJsonContext.Default.AbandonBatchResult,
            "RecoveryCleanupOperationModule.Abandon",
            (_, _) => new AbandonOperationHandler(abandonHandler),
            "tally ingest abandon --input -",
            AbandonErrorsList),
        new(
            IngestOperationIds.Cleanup,
            "tally ingest cleanup",
            "command",
            false,
            IngestJsonContext.Default.CleanupBatchInput,
            IngestJsonContext.Default.CleanupBatchResult,
            "RecoveryCleanupOperationModule.Cleanup",
            (_, _) => new CleanupOperationHandler(cleanupHandler),
            "tally ingest cleanup --input -",
            CleanupErrorsList)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId switch
    {
        IngestOperationIds.Abandon => DispatchAbandonAsync(abandonHandler, request, cancellationToken),
        IngestOperationIds.Cleanup => DispatchCleanupAsync(cleanupHandler, request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };

    private static async Task<CommandResult<JsonElement>> DispatchAbandonAsync(
        AbandonHandler handler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.AbandonBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(AbandonErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(new AbandonCommand(input.BatchId, input.Reason), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.AbandonBatchResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(AbandonErrors.InvalidInput);
        }
    }

    private static async Task<CommandResult<JsonElement>> DispatchCleanupAsync(
        CleanupHandler handler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.CleanupBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(CleanupErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(
                new CleanupCommand(input.BatchId, input.ExpectedTerminalStatus),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.CleanupBatchResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(CleanupErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> AbandonErrorsList =
    [
        new(AbandonErrors.InvalidInput, "validation", 3),
        new(AbandonErrors.NotFound, "not_found", 4),
        new(AbandonErrors.NotAbandonable, "validation", 3),
        new(AbandonErrors.LockHeld, "conflict", 5)
    ];

    private static readonly IReadOnlyList<ErrorSchema> CleanupErrorsList =
    [
        new(CleanupErrors.InvalidInput, "validation", 3),
        new(CleanupErrors.NotFound, "not_found", 4),
        new(CleanupErrors.RetainedForRecovery, "validation", 3),
        new(CleanupErrors.LockHeld, "conflict", 5)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class AbandonOperationHandler(AbandonHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.AbandonBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(AbandonErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(new AbandonCommand(input.BatchId, input.Reason), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.AbandonBatchResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(AbandonErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class CleanupOperationHandler(CleanupHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.CleanupBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(CleanupErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(
                new CleanupCommand(input.BatchId, input.ExpectedTerminalStatus),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.CleanupBatchResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(CleanupErrors.InvalidInput);
        }
    }
}
