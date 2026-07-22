using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Transactions;

public static class CategoryAllocationErrors
{
    public const string TransactionInactive = "LEDGER-TRANSACTION-INACTIVE";
    public const string Cardinality = "LEDGER-CATEGORY-ALLOCATION-CARDINALITY";
    public const string NotAssigned = "LEDGER-CATEGORY-ALLOCATION-NOT-ASSIGNED";
    public const string Unchanged = "LEDGER-CATEGORY-ALLOCATION-UNCHANGED";
}

public sealed class AssignCategoryHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    CategoryStore categoryStore,
    CategoryAllocationStore allocationStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(AssignCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryAllocationHandlerPolicy.ExecuteAsync(
            executor, transactionStore, categoryStore, allocationStore, "ledger.transaction.category.assign",
            input.TransactionId, input.CategoryId, input.Reason, actor, key,
            input, LedgerJsonContext.Default.AssignCategoryInput, correct: false, cancellationToken);
}

public sealed class CorrectCategoryHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    CategoryStore categoryStore,
    CategoryAllocationStore allocationStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(CorrectCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryAllocationHandlerPolicy.ExecuteAsync(
            executor, transactionStore, categoryStore, allocationStore, "ledger.transaction.category.correct",
            input.TransactionId, input.CategoryId, input.Reason, actor, key,
            input, LedgerJsonContext.Default.CorrectCategoryInput, correct: true, cancellationToken);
}

internal static class CategoryAllocationHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> ExecuteAsync<T>(
        LedgerMutationExecutor executor,
        TransactionStore transactionStore,
        CategoryStore categoryStore,
        CategoryAllocationStore allocationStore,
        string operationId,
        string transactionId,
        string categoryId,
        string requestedReason,
        SafeActor? actor,
        string? key,
        T input,
        global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> inputType,
        bool correct,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key)
            || !CategoryAllocation.TryCreate(transactionId, categoryId, requestedReason, out var allocation))
        {
            return Failure(CategoryAllocation.InvalidError);
        }

        var canonicalInput = JsonSerializer.SerializeToElement(input, inputType);
        var request = new IdempotencyRequest("1.0", operationId, key, Actor(actor), canonicalInput, null);
        return await executor.ExecuteAsync(request, async (connection, databaseTransaction, token) =>
        {
            var transaction = await transactionStore.GetAsync(connection, databaseTransaction, allocation!.TransactionId, false, token);
            if (transaction is null) return Failure(TransactionErrors.NotFound);
            if (transaction.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(CategoryAllocationErrors.TransactionInactive);

            var category = await categoryStore.FindCurrentAsync(connection, databaseTransaction, allocation.CategoryId, token);
            if (category is null) return Failure(global::Tally.Features.Ledger.Categories.CategoryErrors.NotFound);
            if (category.Status != CategoryStatus.Active) return Failure(global::Tally.Features.Ledger.Categories.CategoryErrors.Archived);

            var current = await allocationStore.FindCurrentAsync(connection, databaseTransaction, allocation.TransactionId, token);
            if (!correct && current is not null) return Failure(CategoryAllocationErrors.Cardinality);
            if (correct && current is null) return Failure(CategoryAllocationErrors.NotAssigned);
            if (correct && current!.CategoryId == allocation.CategoryId) return Failure(CategoryAllocationErrors.Unchanged);

            var eventId = LedgerId.New().ToString();
            await allocationStore.AppendAsync(
                connection, databaseTransaction, eventId, allocation.TransactionId, allocation.CategoryId,
                correct ? TransactionCategoryAction.Correct : TransactionCategoryAction.Assign,
                current?.AllocationEventId, null, null, allocation.Reason, Actor(actor), Now(), token);
            var detail = await transactionStore.GetAsync(connection, databaseTransaction, allocation.TransactionId, true, token);
            return Success(new CategoryAllocationResult(detail!, eventId));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Success(CategoryAllocationResult value) =>
        CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, LedgerJsonContext.Default.CategoryAllocationResult));
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
