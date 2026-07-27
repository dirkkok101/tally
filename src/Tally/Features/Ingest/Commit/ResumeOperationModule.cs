using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Commit;

[SupportedOSPlatform("linux")]
public sealed class ResumeOperationModule(ResumeHandler handler)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Resume,
            "tally ingest resume",
            "command",
            false,
            IngestJsonContext.Default.ResumeBatchInput,
            IngestJsonContext.Default.ImportReceipt,
            "ResumeOperationModule.Resume",
            (_, _) => new ResumeOperationHandler(handler),
            "tally ingest resume --input -",
            ResumeErrorsList)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId switch
    {
        IngestOperationIds.Resume => DispatchAsync(handler, request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };

    private static async Task<CommandResult<JsonElement>> DispatchAsync(
        ResumeHandler resumeHandler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.ResumeBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(ResumeErrors.InvalidInput);
            }

            var result = await resumeHandler.HandleAsync(new ResumeCommand(input.BatchId), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ImportReceipt))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ResumeErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> ResumeErrorsList =
    [
        new(ResumeErrors.InvalidInput, "validation", 3),
        new(ResumeErrors.NotFound, "not_found", 4),
        new(ResumeErrors.NotResumable, "validation", 3),
        new(CommitErrors.LockHeld, "conflict", 5),
        new(CommitErrors.Interrupted, "interrupted", 6),
        new(CommitErrors.LedgerConflict, "conflict", 5),
        new(CommitErrors.VerificationFailed, "ledger", 6)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class ResumeOperationHandler(ResumeHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.ResumeBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(ResumeErrors.InvalidInput);
            }

            var result = await handler.HandleAsync(new ResumeCommand(input.BatchId), cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ImportReceipt))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ResumeErrors.InvalidInput);
        }
    }
}
