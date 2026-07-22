using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Infrastructure.Storage.Dimensions;

namespace Tally.Features.Ledger.Dimensions;

public static class SpendPoolErrors
{
    public const string NotFound = "LEDGER-SPEND-POOL-NOT-FOUND";
    public const string Duplicate = "LEDGER-SPEND-POOL-DUPLICATE";
    public const string Archived = "LEDGER-SPEND-POOL-ARCHIVED";
    public const string AlreadyArchived = "LEDGER-SPEND-POOL-ALREADY-ARCHIVED";
    public const string AlreadyActive = "LEDGER-SPEND-POOL-ALREADY-ACTIVE";
}

public sealed class CreateSpendPoolHandler(LedgerMutationExecutor executor, SpendPoolStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(CreateSpendPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!SpendPoolHandlerPolicy.ValidMutation(actor, key) || !SpendPool.TryName(input.Name, out var name))
        {
            return SpendPoolHandlerPolicy.Failure(SpendPool.InvalidError);
        }

        var request = SpendPoolHandlerPolicy.Request("ledger.pool.create", key!, actor!, input, LedgerJsonContext.Default.CreateSpendPoolInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (await store.ActiveNameExistsAsync(connection, transaction, name, null, token))
            {
                return SpendPoolHandlerPolicy.Failure(SpendPoolErrors.Duplicate);
            }

            var poolId = LedgerId.New().ToString();
            var lifecycleEventId = LedgerId.New().ToString();
            await store.InsertAsync(connection, transaction, poolId, lifecycleEventId, name, SpendPoolHandlerPolicy.Actor(actor!), SpendPoolHandlerPolicy.Now(), token);
            return SpendPoolHandlerPolicy.Success((await store.GetAsync(connection, transaction, poolId, true, token))!, LedgerJsonContext.Default.SpendPoolDetail);
        }, cancellationToken);
    }
}

public sealed class GetSpendPoolHandler(SpendPoolStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetSpendPoolInput input, CancellationToken cancellationToken)
    {
        if (!SpendPoolHandlerPolicy.ValidId(input.PoolId)) return SpendPoolHandlerPolicy.Failure(SpendPool.InvalidError);
        var pool = await store.GetAsync(input.PoolId, input.IncludeHistory, cancellationToken);
        return pool is null
            ? SpendPoolHandlerPolicy.Failure(SpendPoolErrors.NotFound)
            : SpendPoolHandlerPolicy.Success(pool, LedgerJsonContext.Default.SpendPoolDetail);
    }
}

public sealed class ListSpendPoolsHandler(SpendPoolStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ListSpendPoolsInput input, CancellationToken cancellationToken)
    {
        if (input.Status is not null && !Enum.IsDefined(input.Status.Value)) return SpendPoolHandlerPolicy.Failure(SpendPool.InvalidError);
        return SpendPoolHandlerPolicy.Success(new SpendPoolListResult(await store.ListAsync(input.Status, cancellationToken)), LedgerJsonContext.Default.SpendPoolListResult);
    }
}

public sealed class RenameSpendPoolHandler(LedgerMutationExecutor executor, SpendPoolStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(RenameSpendPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        SpendPoolHandlerPolicy.LifecycleAsync(executor, store, "ledger.pool.rename", input.PoolId, input.NewName, input.Reason, SpendPoolLifecycleAction.Rename, actor, key, input, LedgerJsonContext.Default.RenameSpendPoolInput, cancellationToken);
}

public sealed class ArchiveSpendPoolHandler(LedgerMutationExecutor executor, SpendPoolStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ArchiveSpendPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        SpendPoolHandlerPolicy.LifecycleAsync(executor, store, "ledger.pool.archive", input.PoolId, null, input.Reason, SpendPoolLifecycleAction.Archive, actor, key, input, LedgerJsonContext.Default.ArchiveSpendPoolInput, cancellationToken);
}

public sealed class ReactivateSpendPoolHandler(LedgerMutationExecutor executor, SpendPoolStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ReactivateSpendPoolInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        SpendPoolHandlerPolicy.LifecycleAsync(executor, store, "ledger.pool.reactivate", input.PoolId, null, input.Reason, SpendPoolLifecycleAction.Reactivate, actor, key, input, LedgerJsonContext.Default.ReactivateSpendPoolInput, cancellationToken);
}

internal static class SpendPoolHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> LifecycleAsync<TInput>(
        LedgerMutationExecutor executor,
        SpendPoolStore store,
        string operationId,
        string poolId,
        string? requestedName,
        string requestedReason,
        SpendPoolLifecycleAction action,
        SafeActor? actor,
        string? key,
        TInput input,
        JsonTypeInfo<TInput> inputType,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!ValidMutation(actor, key)
            || !ValidId(poolId)
            || !SpendPool.TryReason(requestedReason, out var reason)
            || action == SpendPoolLifecycleAction.Rename && !SpendPool.TryName(requestedName, out _))
        {
            return Failure(SpendPool.InvalidError);
        }

        var name = requestedName?.Trim();
        var request = Request(operationId, key!, actor!, input, inputType);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, poolId, token);
            if (current is null) return Failure(SpendPoolErrors.NotFound);
            if (action == SpendPoolLifecycleAction.Reactivate)
            {
                if (current.Status == SpendPoolStatus.Active) return Failure(SpendPoolErrors.AlreadyActive);
                if (await store.ActiveNameExistsAsync(connection, transaction, current.Name, current.PoolId, token)) return Failure(SpendPoolErrors.Duplicate);
            }
            else
            {
                if (current.Status == SpendPoolStatus.Archived)
                {
                    return Failure(action == SpendPoolLifecycleAction.Archive ? SpendPoolErrors.AlreadyArchived : SpendPoolErrors.Archived);
                }

                if (action == SpendPoolLifecycleAction.Rename
                    && (string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)
                        || await store.ActiveNameExistsAsync(connection, transaction, name!, current.PoolId, token)))
                {
                    return Failure(SpendPoolErrors.Duplicate);
                }
            }

            var lifecycleEventId = LedgerId.New().ToString();
            await store.AppendLifecycleAsync(connection, transaction, lifecycleEventId, current, action, name, reason, Actor(actor!), Now(), token);
            return Success(new SpendPoolLifecycleResult((await store.GetAsync(connection, transaction, poolId, true, token))!, lifecycleEventId), LedgerJsonContext.Default.SpendPoolLifecycleResult);
        }, cancellationToken);
    }

    public static bool ValidMutation(SafeActor? actor, string? key) => actor is not null && !string.IsNullOrWhiteSpace(key);
    public static bool ValidId(string? value) => LedgerId.TryParse(value, out _, out _);
    public static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    public static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    public static IdempotencyRequest Request<T>(string operation, string key, SafeActor actor, T input, JsonTypeInfo<T> type) => new("1.0", operation, key, Actor(actor), JsonSerializer.SerializeToElement(input, type), null);
    public static CommandResult<JsonElement> Success<T>(T value, JsonTypeInfo<T> type) => CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, type));
    public static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
