using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Preview;

// Bound for GATE-INT-PUBLIC-CONTRACT; not registered globally here.
[SupportedOSPlatform("linux")]
public sealed class PreviewOperationModule(PreviewHandler handler)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Preview,
            "tally ingest preview",
            "command",
            false,
            IngestJsonContext.Default.PreviewImportInput,
            IngestJsonContext.Default.PreviewImportResult,
            "PreviewOperationModule.Preview",
            (_, _) => new PreviewOperationHandler(handler),
            "tally ingest preview --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId == IngestOperationIds.Preview
        ? DispatchAsync(handler, request, cancellationToken)
        : Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"));

    private static async Task<CommandResult<JsonElement>> DispatchAsync(
        PreviewHandler previewHandler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.PreviewImportInput);
            if (input is null || request.Actor is null)
            {
                return CommandResult<JsonElement>.Failure(PreviewErrors.InvalidInput);
            }

            var result = await previewHandler.HandleAsync(
                new PreviewCommand(input.ContractVersion, input.SourcePath, input.AccountId, request.Actor),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.PreviewImportResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(PreviewErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(PreviewErrors.InvalidInput, "validation", 3),
        new(PreviewErrors.AccountNotFound, "not_found", 4),
        new(PreviewErrors.AccountInactive, "validation", 3),
        new(PreviewErrors.AccountCurrency, "validation", 3),
        new(CallerOwnedSourceReader.PathInvalid, "validation", 3),
        new(CallerOwnedSourceReader.SourceUnreadable, "unsafe_source", 5),
        new(CallerOwnedSourceReader.SourceChanged, "unsafe_source", 5),
        new(CallerOwnedSourceReader.SourceTooLarge, "resource", 6),
        new(PreviewErrors.Unsupported, "unsupported", 5),
        new(PreviewErrors.AmbiguousAdapter, "unsupported", 5),
        new(PreviewErrors.OverlapBlocked, "overlap", 5),
        new(PreviewErrors.ReconciliationBlocked, "reconciliation", 5),
        new(PreviewErrors.Unexpected, "unexpected", 10)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class PreviewOperationHandler(PreviewHandler handler) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        new PreviewOperationModule(handler).HandleAsync(IngestOperationIds.Preview, request, cancellationToken);
}
