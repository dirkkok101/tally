using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Dimensions;

public static class PoolAssignmentErrors
{
    public const string TransactionInactive = "LEDGER-POOL-ASSIGNMENT-TRANSACTION-INACTIVE";
    public const string Stale = "LEDGER-POOL-ASSIGNMENT-STALE";
    public const string AlreadyAssigned = "LEDGER-POOL-ASSIGNMENT-ALREADY-ASSIGNED";
    public const string Unchanged = "LEDGER-POOL-ASSIGNMENT-UNCHANGED";
}

public sealed class AssignPoolHandler(LedgerMutationExecutor executor, TransactionStore transactionStore, SpendPoolStore poolStore, PoolAssignmentStore assignmentStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(AssignPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) => PoolAssignmentHandlerPolicy.ExecuteAsync(executor, transactionStore, poolStore, assignmentStore, "ledger.transaction.pool.assign", input.TransactionId, input.ExpectedPoolAssignmentEventId, input.Assignment, input.Reason, actor, key, input, LedgerJsonContext.Default.AssignPoolInput, false, cancellationToken);
}
public sealed class CorrectPoolHandler(LedgerMutationExecutor executor, TransactionStore transactionStore, SpendPoolStore poolStore, PoolAssignmentStore assignmentStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(CorrectPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) => PoolAssignmentHandlerPolicy.ExecuteAsync(executor, transactionStore, poolStore, assignmentStore, "ledger.transaction.pool.correct", input.TransactionId, input.ExpectedPoolAssignmentEventId, input.Assignment, input.Reason, actor, key, input, LedgerJsonContext.Default.CorrectPoolInput, true, cancellationToken);
}

internal static class PoolAssignmentHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> ExecuteAsync<T>(LedgerMutationExecutor executor, TransactionStore transactionStore, SpendPoolStore poolStore, PoolAssignmentStore assignmentStore, string operationId, string transactionId, string expectedEventId, PoolAssignmentInput assignment, string reason, SafeActor? actor, string? key, T input, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> inputType, bool correct, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key) || !PoolAssignmentPolicy.TryCreate(transactionId, expectedEventId, assignment, reason, out var command)) return Failure(PoolAssignmentPolicy.InvalidError);
        var request = new IdempotencyRequest("1.0", operationId, key, Actor(actor), JsonSerializer.SerializeToElement(input, inputType), null);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var detail = await transactionStore.GetAsync(connection, transaction, command!.TransactionId, false, token);
            if (detail is null) return Failure(TransactionErrors.NotFound);
            if (detail.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(PoolAssignmentErrors.TransactionInactive);
            var current = await assignmentStore.FindCurrentAsync(connection, transaction, command.TransactionId, token) ?? throw new InvalidOperationException("Pool assignment is missing.");
            if (current.EventId != command.ExpectedEventId) return Failure(PoolAssignmentErrors.Stale);
            if (!correct && current.State == TransactionPoolState.Assigned) return Failure(PoolAssignmentErrors.AlreadyAssigned);
            if (current.State == command.State && current.PoolId == command.PoolId) return Failure(PoolAssignmentErrors.Unchanged);
            if (command.State == TransactionPoolState.Assigned && await poolStore.ActiveAssignmentErrorAsync(connection, transaction, command.PoolId!, token) is { } poolError) return Failure(poolError);
            var eventId = LedgerId.New().ToString();
            await assignmentStore.AppendAsync(connection, transaction, eventId, command.TransactionId, command.State, command.PoolId, correct ? TransactionAssignmentAction.Correct : TransactionAssignmentAction.Assign, current.EventId, null, null, command.Reason, Actor(actor), Now(), token);
            detail = await transactionStore.GetAsync(connection, transaction, command.TransactionId, true, token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new PoolAssignmentResult(detail!, eventId), LedgerJsonContext.Default.PoolAssignmentResult));
        }, cancellationToken);
    }
    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
