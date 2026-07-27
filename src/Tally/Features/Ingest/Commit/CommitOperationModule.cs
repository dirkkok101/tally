using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ingest;
using Tally.Contracts.System;
using Tally.Features.Ingest.Contract;

namespace Tally.Features.Ingest.Commit;

[SupportedOSPlatform("linux")]
public sealed class CommitOperationModule(CandidateCommitSaga saga)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            IngestOperationIds.Commit,
            "tally ingest commit",
            "command",
            false,
            IngestJsonContext.Default.CommitBatchInput,
            IngestJsonContext.Default.ImportReceipt,
            "CommitOperationModule.Commit",
            (_, _) => new CommitOperationHandler(saga),
            "tally ingest commit --input -",
            CommitErrorsList)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId switch
    {
        IngestOperationIds.Commit => DispatchAsync(saga, request, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };

    private static async Task<CommandResult<JsonElement>> DispatchAsync(
        CandidateCommitSaga saga,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.CommitBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(CommitErrors.InvalidInput);
            }

            var result = await saga.ExecuteAsync(
                new CommitCommand(input.BatchId, input.ManifestRevisionId, input.ManifestDigest),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ImportReceipt))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(CommitErrors.InvalidInput);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> CommitErrorsList =
    [
        new(CommitErrors.InvalidInput, "validation", 3),
        new(CommitErrors.NotFound, "not_found", 4),
        new(CommitErrors.DigestMismatch, "conflict", 5),
        new(CommitErrors.NotApproved, "validation", 3),
        new(CommitErrors.NotCommittable, "validation", 3),
        new(CommitErrors.AccountInactive, "validation", 3),
        new(CommitErrors.VersionIncompatible, "compatibility", 7),
        new(CommitErrors.LockHeld, "conflict", 5),
        new(CommitErrors.LedgerConflict, "conflict", 5),
        new(CommitErrors.LedgerRejected, "validation", 3),
        new(CommitErrors.VerificationFailed, "ledger", 6),
        new(CommitErrors.Interrupted, "interrupted", 6)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class CommitOperationHandler(CandidateCommitSaga saga) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, IngestJsonContext.Default.CommitBatchInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(CommitErrors.InvalidInput);
            }

            var result = await saga.ExecuteAsync(
                new CommitCommand(input.BatchId, input.ManifestRevisionId, input.ManifestDigest),
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result.Value!, IngestJsonContext.Default.ImportReceipt))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(CommitErrors.InvalidInput);
        }
    }
}
