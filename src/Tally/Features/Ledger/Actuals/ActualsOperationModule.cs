using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.System;

namespace Tally.Features.Ledger.Actuals;

public sealed class ActualsOperationModule(ActualsQueryHandler handler)
{
    public const string OperationId = "ledger.actuals.query";

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            OperationId,
            "tally ledger actuals query",
            "query",
            false,
            ActualsJsonContext.Default.QueryActualsInput,
            ActualsJsonContext.Default.ActualsQueryResult,
            "ActualsOperationModule.Query",
            (_, _) => new ActualsOperationHandler(handler),
            "tally ledger actuals query --input -",
            Errors)
    ];

    public Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken) => operationId == OperationId
        ? DispatchAsync(handler, request, cancellationToken)
        : Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"));

    private static async Task<CommandResult<JsonElement>> DispatchAsync(
        ActualsQueryHandler queryHandler,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, ActualsJsonContext.Default.QueryActualsInput);
            return input is null
                ? CommandResult<JsonElement>.Failure(ActualsErrors.InvalidFilter)
                : await queryHandler.HandleAsync(input, cancellationToken);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ActualsErrors.InvalidFilter);
        }
    }

    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new(ActualsErrors.InvalidFilter, "validation", 3),
        new(ActualsErrors.SnapshotNotFound, "not_found", 4),
        new(ActualsErrors.SnapshotBusy, "conflict", 5),
        new(ActualsErrors.SnapshotExpired, "lifecycle", 6),
        new(ActualsErrors.CursorInvalid, "compatibility", 7),
        new(ActualsErrors.ContractMismatch, "compatibility", 7),
        new(ActualsErrors.CursorFilterMismatch, "compatibility", 7),
        new(ActualsErrors.GenerationMismatch, "compatibility", 7),
        new(ActualsErrors.HierarchyMismatch, "compatibility", 7),
        new(ActualsErrors.Invariant, "integrity", 8)
    ];
}

internal sealed class ActualsOperationHandler(ActualsQueryHandler handler) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, ActualsJsonContext.Default.QueryActualsInput);
            return input is null
                ? CommandResult<JsonElement>.Failure(ActualsErrors.InvalidFilter)
                : await handler.HandleAsync(input, cancellationToken);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ActualsErrors.InvalidFilter);
        }
    }
}
