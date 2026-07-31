using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Transactions;

public static class CategoryAllocationErrors
{
    public const string TransactionInactive = "LEDGER-TRANSACTION-INACTIVE";
    public const string Cardinality = "LEDGER-CATEGORY-ALLOCATION-CARDINALITY";
    public const string NotAssigned = "LEDGER-CATEGORY-ALLOCATION-NOT-ASSIGNED";
    public const string Unchanged = "LEDGER-CATEGORY-ALLOCATION-UNCHANGED";
    public const string StalePrecondition = CategoryMutationPreconditionCodes.StalePrecondition;
    public const string ContractMismatch = CategoryMutationPreconditionCodes.ContractMismatch;
}

public sealed class AssignCategoryHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    CategoryStore categoryStore,
    CategoryAllocationStore allocationStore,
    RelationshipStore? relationshipStore = null)
{
    public Task<CommandResult<JsonElement>> HandleAsync(AssignCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryAllocationHandlerPolicy.ExecuteAsync(
            executor, transactionStore, categoryStore, allocationStore, relationshipStore,
            "ledger.transaction.category.assign",
            input.TransactionId, input.CategoryId, input.Reason, actor, key,
            input, LedgerJsonContext.Default.AssignCategoryInput, correct: false,
            input.ExpectedTransactionRevision, input.ExpectedRelationshipRevision,
            input.ExpectedAllocationRevision, input.ExpectedActiveAllocationId,
            input.MutationContractVersion,
            cancellationToken);
}

public sealed class CorrectCategoryHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    CategoryStore categoryStore,
    CategoryAllocationStore allocationStore,
    RelationshipStore? relationshipStore = null)
{
    public Task<CommandResult<JsonElement>> HandleAsync(CorrectCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryAllocationHandlerPolicy.ExecuteAsync(
            executor, transactionStore, categoryStore, allocationStore, relationshipStore,
            "ledger.transaction.category.correct",
            input.TransactionId, input.CategoryId, input.Reason, actor, key,
            input, LedgerJsonContext.Default.CorrectCategoryInput, correct: true,
            input.ExpectedTransactionRevision, input.ExpectedRelationshipRevision,
            input.ExpectedAllocationRevision, input.ExpectedActiveAllocationId,
            input.MutationContractVersion,
            cancellationToken);
}

internal static class CategoryAllocationHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> ExecuteAsync<T>(
        LedgerMutationExecutor executor,
        TransactionStore transactionStore,
        CategoryStore categoryStore,
        CategoryAllocationStore allocationStore,
        RelationshipStore? relationshipStore,
        string operationId,
        string transactionId,
        string categoryId,
        string requestedReason,
        SafeActor? actor,
        string? key,
        T input,
        global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> inputType,
        bool correct,
        string? expectedTransactionRevision,
        string? expectedRelationshipRevision,
        string? expectedAllocationRevision,
        string? expectedActiveAllocationId,
        string? mutationContractVersion,
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
            var transaction = await transactionStore.GetAsync(connection, databaseTransaction, allocation!.TransactionId, includeHistory: true, token);
            if (transaction is null) return Failure(TransactionErrors.NotFound);
            if (transaction.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(CategoryAllocationErrors.TransactionInactive);

            var category = await categoryStore.FindCurrentAsync(connection, databaseTransaction, allocation.CategoryId, token);
            if (category is null) return Failure(global::Tally.Features.Ledger.Categories.CategoryErrors.NotFound);
            if (category.Status != CategoryStatus.Active) return Failure(global::Tally.Features.Ledger.Categories.CategoryErrors.Archived);

            var current = await allocationStore.FindCurrentAsync(connection, databaseTransaction, allocation.TransactionId, token);

            // Classification-precondition path: any supplied expectation, or any correction (required by failure criterion).
            var usesClassificationPreconditions = correct
                || mutationContractVersion is not null
                || expectedTransactionRevision is not null
                || expectedRelationshipRevision is not null
                || expectedAllocationRevision is not null
                || expectedActiveAllocationId is not null;

            if (usesClassificationPreconditions)
            {
                var stale = await AssertClassificationPreconditionsAsync(
                    connection,
                    databaseTransaction,
                    transaction,
                    current,
                    allocation.TransactionId,
                    correct,
                    expectedTransactionRevision,
                    expectedRelationshipRevision,
                    expectedAllocationRevision,
                    expectedActiveAllocationId,
                    mutationContractVersion is not null,
                    relationshipStore,
                    token);
                if (stale is not null) return Failure(stale);
            }

            // Legacy cardinality / not-assigned / unchanged only after classification preconditions pass
            // (or when the request carries no classification expectations at all).
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

    /// <summary>
    /// Drift-safe CLASSIFY preconditions (DM-CLASSIFY-LEDGER-PROJECTION-CONTRACT).
    /// Evaluated before cardinality so ExpectedAllocationRevision=none races return StalePrecondition.
    /// </summary>
    private static async Task<string?> AssertClassificationPreconditionsAsync(
        SqliteConnection connection,
        SqliteTransaction databaseTransaction,
        TransactionDetail transaction,
        CategoryAllocationCurrent? current,
        string transactionId,
        bool correct,
        string? expectedTransactionRevision,
        string? expectedRelationshipRevision,
        string? expectedAllocationRevision,
        string? expectedActiveAllocationId,
        bool requiresCompleteClassificationPreconditions,
        RelationshipStore? relationshipStore,
        CancellationToken cancellationToken)
    {
        if (correct || requiresCompleteClassificationPreconditions)
        {
            // A released classification_v1 mutation must carry every projection revision. Correction
            // additionally requires the exact active allocation identity; legacy assign remains available
            // only when MutationContractVersion and all expectations are omitted.
            if ((correct && string.IsNullOrWhiteSpace(expectedActiveAllocationId))
                || string.IsNullOrWhiteSpace(expectedAllocationRevision)
                || string.IsNullOrWhiteSpace(expectedTransactionRevision)
                || string.IsNullOrWhiteSpace(expectedRelationshipRevision))
            {
                return CategoryAllocationErrors.StalePrecondition;
            }
        }
        else if (expectedActiveAllocationId is not null)
        {
            // Assign must not carry an active allocation identity expectation.
            return CategoryAllocationErrors.StalePrecondition;
        }

        if (expectedActiveAllocationId is not null)
        {
            if (current is null
                || !string.Equals(expectedActiveAllocationId, current.AllocationEventId, StringComparison.Ordinal))
            {
                return CategoryAllocationErrors.StalePrecondition;
            }
        }

        if (expectedAllocationRevision is not null)
        {
            var actualAllocationRevision = current?.AllocationEventId ?? "none";
            if (!string.Equals(expectedAllocationRevision, actualAllocationRevision, StringComparison.Ordinal))
            {
                return CategoryAllocationErrors.StalePrecondition;
            }
        }

        if (expectedTransactionRevision is not null)
        {
            var latestLifecycle = transaction.History?.Lifecycle.LastOrDefault()?.LifecycleEventId;
            var actualTransactionRevision = latestLifecycle ?? ("genesis:" + transaction.TransactionId);
            if (!string.Equals(expectedTransactionRevision, actualTransactionRevision, StringComparison.Ordinal))
            {
                return CategoryAllocationErrors.StalePrecondition;
            }
        }

        if (expectedRelationshipRevision is not null)
        {
            // Fail closed: relationship assertion must run on the mutation connection/transaction.
            if (relationshipStore is null)
            {
                return CategoryAllocationErrors.StalePrecondition;
            }

            var actualRelationshipRevision = await relationshipStore.ActiveRevisionAsync(
                connection, databaseTransaction, transactionId, cancellationToken);
            if (!string.Equals(expectedRelationshipRevision, actualRelationshipRevision, StringComparison.Ordinal))
            {
                return CategoryAllocationErrors.StalePrecondition;
            }
        }

        return null;
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Success(CategoryAllocationResult value) =>
        CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, LedgerJsonContext.Default.CategoryAllocationResult));
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
