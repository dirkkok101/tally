using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ledger.Recovery;
using Tally.Contracts.System;
using Tally.Infrastructure.Recovery;

namespace Tally.Features.Ledger.Recovery;

[SupportedOSPlatform("linux")]
public sealed class RestoreOperationModule(RestoreService service)
{
    public const string PrepareOperationId = "ledger.restore.prepare";
    public const string ActivateOperationId = "ledger.restore.activate";

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            PrepareOperationId,
            "tally ledger restore prepare",
            "mutation",
            true,
            RestoreJsonContext.Default.PrepareRestoreInput,
            RestoreJsonContext.Default.RestorePrepareResult,
            "RestoreOperationModule.Prepare",
            (_, _) => new RestoreOperationHandler(service, PrepareOperationId),
            "tally ledger restore prepare --input -",
            Errors),
        new(
            ActivateOperationId,
            "tally ledger restore activate",
            "mutation",
            true,
            RestoreJsonContext.Default.ActivateRestoreInput,
            RestoreJsonContext.Default.RestoreActivationResult,
            "RestoreOperationModule.Activate",
            (_, _) => new RestoreOperationHandler(service, ActivateOperationId),
            "tally ledger restore activate --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => DispatchAsync(service, operationId, request, cancellationToken);

    internal static async Task<CommandResult<JsonElement>> DispatchAsync(
        RestoreService restoreService,
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return operationId switch
            {
                PrepareOperationId => JsonSerializer.Deserialize(request.Input, RestoreJsonContext.Default.PrepareRestoreInput) is { } input
                    ? await restoreService.PrepareAsync(input, request.Actor, request.IdempotencyKey, cancellationToken)
                    : CommandResult<JsonElement>.Failure(RestoreErrors.Invalid),
                ActivateOperationId => JsonSerializer.Deserialize(request.Input, RestoreJsonContext.Default.ActivateRestoreInput) is { } input
                    ? await restoreService.ActivateAsync(input, request.Actor, request.IdempotencyKey, cancellationToken)
                    : CommandResult<JsonElement>.Failure(RestoreErrors.Invalid),
                _ => CommandResult<JsonElement>.Failure("operation.not_found")
            };
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(RestoreErrors.Invalid);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(RestoreErrors.Invalid, "validation", 3),
        new(RestoreErrors.NotAuthorized, "validation", 3),
        new(BackupErrors.NotFound, "not_found", 4),
        new(RestoreErrors.CandidateConflict, "conflict", 5),
        new(RestoreErrors.ActivationConflict, "conflict", 5),
        new(LedgerMutationExecutor.ConflictCode, "conflict", 5),
        new(RestoreErrors.Busy, "conflict", 5),
        new(RestoreErrors.StaleCurrent, "lifecycle", 6),
        new(RestoreErrors.StaleCandidate, "lifecycle", 6),
        new(RestoreErrors.Incompatible, "compatibility", 7),
        new(RestoreErrors.Integrity, "integrity", 8),
        new(RestoreErrors.HostProtection, "host", 9),
        new(RestoreErrors.Permission, "host", 9),
        new(RestoreErrors.Disk, "host", 9)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class RestoreOperationHandler(RestoreService service, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        RestoreOperationModule.DispatchAsync(service, operationId, request, cancellationToken);
}
