using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ledger.Recovery;
using Tally.Contracts.System;
using Tally.Infrastructure.Recovery;

namespace Tally.Features.Ledger.Recovery;

[SupportedOSPlatform("linux")]
public sealed class StorageEvolutionOperationModule(StorageEvolutionService service)
{
    public const string StatusOperationId = "ledger.storage.status";
    public const string PrepareOperationId = "ledger.storage.evolution.prepare";
    public const string ActivateOperationId = "ledger.storage.evolution.activate";

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            StatusOperationId,
            "tally ledger storage status",
            "query",
            false,
            StorageEvolutionJsonContext.Default.StorageStatusInput,
            StorageEvolutionJsonContext.Default.StorageStatusResult,
            "StorageEvolutionOperationModule.Status",
            (_, _) => new StorageEvolutionOperationHandler(service, StatusOperationId),
            "tally ledger storage status --input -",
            Errors),
        new(
            PrepareOperationId,
            "tally ledger storage evolution prepare",
            "mutation",
            true,
            StorageEvolutionJsonContext.Default.PrepareStorageEvolutionInput,
            StorageEvolutionJsonContext.Default.StorageEvolutionPrepareResult,
            "StorageEvolutionOperationModule.Prepare",
            (_, _) => new StorageEvolutionOperationHandler(service, PrepareOperationId),
            "tally ledger storage evolution prepare --input -",
            Errors),
        new(
            ActivateOperationId,
            "tally ledger storage evolution activate",
            "mutation",
            true,
            StorageEvolutionJsonContext.Default.ActivateStorageEvolutionInput,
            StorageEvolutionJsonContext.Default.StorageEvolutionActivationResult,
            "StorageEvolutionOperationModule.Activate",
            (_, _) => new StorageEvolutionOperationHandler(service, ActivateOperationId),
            "tally ledger storage evolution activate --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => DispatchAsync(service, operationId, request, cancellationToken);

    internal static async Task<CommandResult<JsonElement>> DispatchAsync(
        StorageEvolutionService storageEvolutionService,
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return operationId switch
            {
                StatusOperationId => JsonSerializer.Deserialize(request.Input, StorageEvolutionJsonContext.Default.StorageStatusInput) is { } input
                    ? await storageEvolutionService.StatusAsync(input, cancellationToken)
                    : CommandResult<JsonElement>.Failure(StorageEvolutionErrors.Invalid),
                PrepareOperationId => JsonSerializer.Deserialize(request.Input, StorageEvolutionJsonContext.Default.PrepareStorageEvolutionInput) is { } input
                    ? await storageEvolutionService.PrepareAsync(input, request.Actor, request.IdempotencyKey, cancellationToken)
                    : CommandResult<JsonElement>.Failure(StorageEvolutionErrors.Invalid),
                ActivateOperationId => JsonSerializer.Deserialize(request.Input, StorageEvolutionJsonContext.Default.ActivateStorageEvolutionInput) is { } input
                    ? await storageEvolutionService.ActivateAsync(input, request.Actor, request.IdempotencyKey, cancellationToken)
                    : CommandResult<JsonElement>.Failure(StorageEvolutionErrors.Invalid),
                _ => CommandResult<JsonElement>.Failure("operation.not_found")
            };
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(StorageEvolutionErrors.Invalid);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(StorageEvolutionErrors.Invalid, "validation", 3),
        new(StorageEvolutionErrors.NotAuthorized, "validation", 3),
        new(StorageEvolutionErrors.AlreadyCurrent, "lifecycle", 6),
        new(StorageEvolutionErrors.Incompatible, "compatibility", 7),
        new(StorageEvolutionErrors.CandidateConflict, "conflict", 5),
        new(StorageEvolutionErrors.ActivationConflict, "conflict", 5),
        new(LedgerMutationExecutor.ConflictCode, "conflict", 5),
        new(StorageEvolutionErrors.Busy, "conflict", 5),
        new(StorageEvolutionErrors.StaleCurrent, "lifecycle", 6),
        new(StorageEvolutionErrors.StaleCandidate, "lifecycle", 6),
        new(StorageEvolutionErrors.Integrity, "integrity", 8),
        new(StorageEvolutionErrors.HostProtection, "host", 9),
        new(StorageEvolutionErrors.Permission, "host", 9),
        new(StorageEvolutionErrors.Disk, "host", 9),
        new(StorageEvolutionErrors.InsufficientSpace, "host", 9)
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class StorageEvolutionOperationHandler(StorageEvolutionService service, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        StorageEvolutionOperationModule.DispatchAsync(service, operationId, request, cancellationToken);
}
