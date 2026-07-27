using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Review;

[SupportedOSPlatform("linux")]
public sealed class ReviewOperationModule(InspectHandler inspectHandler, ApproveHandler approveHandler)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Inspect,
            "tally ingest inspect",
            "query",
            false,
            IngestJsonContext.Default.InspectManifestInput,
            IngestJsonContext.Default.InspectManifestResult,
            "ReviewOperationModule.Inspect",
            (_, _) => new InspectOperationHandler(inspectHandler),
            "tally ingest inspect --input -",
            InspectErrorsList),
        new(
            IngestOperationIds.Approve,
            "tally ingest approve",
            "command",
            false,
            IngestJsonContext.Default.ApproveManifestInput,
            IngestJsonContext.Default.ApproveManifestResult,
            "ReviewOperationModule.Approve",
            (_, _) => new ApproveOperationHandler(approveHandler),
            "tally ingest approve --input -",
            ApproveErrorsList)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId switch
    {
        IngestOperationIds.Inspect => DispatchInspectAsync(inspectHandler, request, cancellationToken),
        IngestOperationIds.Approve => DispatchApproveAsync(approveHandler, request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };

    private static async Task<CommandResult<JsonElement>> DispatchInspectAsync(
        InspectHandler handler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.InspectManifestInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(InspectErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(new InspectQuery(input.BatchId, input.ManifestRevisionId), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.InspectManifestResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(InspectErrors.InvalidInput);
        }
    }

    private static async Task<CommandResult<JsonElement>> DispatchApproveAsync(
        ApproveHandler handler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.ApproveManifestInput);
            if (input is null || request.Actor is null)
            {
                return CommandResult<JsonElement>.Failure(ApproveErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(
                new ApproveCommand(input.BatchId, input.ManifestRevisionId, input.ManifestDigest, request.Actor),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ApproveManifestResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ApproveErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> InspectErrorsList =
    [
        new(InspectErrors.InvalidInput, "validation", 3),
        new(InspectErrors.NotFound, "not_found", 4)
    ];

    private static readonly IReadOnlyList<ErrorSchema> ApproveErrorsList =
    [
        new(ApproveErrors.InvalidInput, "validation", 3),
        new(ApproveErrors.NotFound, "not_found", 4),
        new(ApproveErrors.DigestMismatch, "conflict", 5),
        new(ApproveErrors.NotCommittable, "validation", 3),
        new(ApproveErrors.Blocked, "validation", 3)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class InspectOperationHandler(InspectHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.InspectManifestInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(InspectErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(new InspectQuery(input.BatchId, input.ManifestRevisionId), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.InspectManifestResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(InspectErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class ApproveOperationHandler(ApproveHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.ApproveManifestInput);
            if (input is null || request.Actor is null)
            {
                return CommandResult<JsonElement>.Failure(ApproveErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(
                new ApproveCommand(input.BatchId, input.ManifestRevisionId, input.ManifestDigest, request.Actor),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ApproveManifestResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ApproveErrors.InvalidInput);
        }
    }
}
