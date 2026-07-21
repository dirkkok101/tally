using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Categories;
using Tally.Infrastructure.Storage.Categories;

namespace Tally.Features.Ledger.Categories;

public static class CategoryErrors
{
    public const string NotFound = "LEDGER-CATEGORY-NOT-FOUND";
    public const string DuplicateSibling = "LEDGER-CATEGORY-DUPLICATE-SIBLING";
    public const string ParentNotFound = "LEDGER-CATEGORY-PARENT-NOT-FOUND";
    public const string ParentArchived = "LEDGER-CATEGORY-PARENT-ARCHIVED";
    public const string Archived = "LEDGER-CATEGORY-ARCHIVED";
    public const string SelfParent = "LEDGER-CATEGORY-SELF-PARENT";
    public const string Cycle = "LEDGER-CATEGORY-CYCLE";
    public const string ActiveChildren = "LEDGER-CATEGORY-ACTIVE-CHILDREN";
    public const string AlreadyArchived = "LEDGER-CATEGORY-ALREADY-ARCHIVED";
    public const string AlreadyActive = "LEDGER-CATEGORY-ALREADY-ACTIVE";
    public const string AncestorArchived = "LEDGER-CATEGORY-ANCESTOR-ARCHIVED";
    public const string ScopeInvalid = "LEDGER-CATEGORY-SCOPE-INVALID";
}

public sealed class CreateCategoryHandler(LedgerMutationExecutor executor, CategoryStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(CreateCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        CategoryHandlerPolicy.RequireLinux();
        if (!CategoryHandlerPolicy.ValidMutation(actor, key) || !SpendCategory.TryName(input.Name, out var name) || !CategoryHandlerPolicy.ValidOptionalId(input.ParentCategoryId)) return CategoryHandlerPolicy.Failure(SpendCategory.InvalidError);
        var request = CategoryHandlerPolicy.Request("ledger.category.create", key!, actor!, input, LedgerJsonContext.Default.CreateCategoryInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (input.ParentCategoryId is not null)
            {
                var parent = await store.FindCurrentAsync(connection, transaction, input.ParentCategoryId, token);
                if (parent is null) return CategoryHandlerPolicy.Failure(CategoryErrors.ParentNotFound);
                if (parent.Status == CategoryStatus.Archived) return CategoryHandlerPolicy.Failure(CategoryErrors.ParentArchived);
            }
            if (await store.SiblingNameExistsAsync(connection, transaction, name, input.ParentCategoryId, null, token)) return CategoryHandlerPolicy.Failure(CategoryErrors.DuplicateSibling);
            var id = LedgerId.New().ToString(); var parentEventId = LedgerId.New().ToString(); var lifecycleEventId = LedgerId.New().ToString();
            await store.InsertAsync(connection, transaction, id, parentEventId, lifecycleEventId, name, input.ParentCategoryId, CategoryHandlerPolicy.Actor(actor!), CategoryHandlerPolicy.Now(), token);
            return CategoryHandlerPolicy.Success((await store.GetAsync(connection, transaction, id, true, token))!, LedgerJsonContext.Default.CategoryDetail);
        }, cancellationToken);
    }
}

public sealed class GetCategoryHandler(CategoryStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetCategoryInput input, CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.CategoryId, out _, out _)) return CategoryHandlerPolicy.Failure(SpendCategory.InvalidError);
        var detail = await store.GetAsync(input.CategoryId, input.IncludeHistory, cancellationToken);
        return detail is null ? CategoryHandlerPolicy.Failure(CategoryErrors.NotFound) : CategoryHandlerPolicy.Success(detail, LedgerJsonContext.Default.CategoryDetail);
    }
}

public sealed class ListCategoriesHandler(CategoryStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ListCategoriesInput input, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(input.Scope) || input.Status is not null && !Enum.IsDefined(input.Status.Value) || !CategoryHandlerPolicy.ValidOptionalId(input.ParentCategoryId) || input.Scope == CategoryListScope.All && input.ParentCategoryId is not null || input.Scope == CategoryListScope.Subtree && input.ParentCategoryId is null) return CategoryHandlerPolicy.Failure(CategoryErrors.ScopeInvalid);
        if (input.ParentCategoryId is not null && await store.GetAsync(input.ParentCategoryId, false, cancellationToken) is null) return CategoryHandlerPolicy.Failure(CategoryErrors.ParentNotFound);
        return CategoryHandlerPolicy.Success(new CategoryListResult(await store.ListAsync(input.Status, input.ParentCategoryId, input.Scope, cancellationToken)), LedgerJsonContext.Default.CategoryListResult);
    }
}

public sealed class RenameCategoryHandler(LedgerMutationExecutor executor, CategoryStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(RenameCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryHandlerPolicy.LifecycleAsync(executor, store, "ledger.category.rename", input.CategoryId, input.NewName, input.Reason, CategoryLifecycleAction.Rename, actor, key, input, LedgerJsonContext.Default.RenameCategoryInput, cancellationToken);
}

public sealed class ArchiveCategoryHandler(LedgerMutationExecutor executor, CategoryStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ArchiveCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryHandlerPolicy.LifecycleAsync(executor, store, "ledger.category.archive", input.CategoryId, null, input.Reason, CategoryLifecycleAction.Archive, actor, key, input, LedgerJsonContext.Default.ArchiveCategoryInput, cancellationToken);
}

public sealed class ReactivateCategoryHandler(LedgerMutationExecutor executor, CategoryStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ReactivateCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        CategoryHandlerPolicy.LifecycleAsync(executor, store, "ledger.category.reactivate", input.CategoryId, null, input.Reason, CategoryLifecycleAction.Reactivate, actor, key, input, LedgerJsonContext.Default.ReactivateCategoryInput, cancellationToken);
}

public sealed class ReparentCategoryHandler(LedgerMutationExecutor executor, CategoryStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ReparentCategoryInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        CategoryHandlerPolicy.RequireLinux();
        if (!CategoryHandlerPolicy.ValidMutation(actor, key) || !CategoryHandlerPolicy.ValidId(input.CategoryId) || !CategoryHandlerPolicy.ValidOptionalId(input.ParentCategoryId) || !SpendCategory.TryReason(input.Reason, out var reason)) return CategoryHandlerPolicy.Failure(SpendCategory.InvalidError);
        if (input.CategoryId == input.ParentCategoryId) return CategoryHandlerPolicy.Failure(CategoryErrors.SelfParent);
        var request = CategoryHandlerPolicy.Request("ledger.category.reparent", key!, actor!, input, LedgerJsonContext.Default.ReparentCategoryInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, input.CategoryId, token);
            if (current is null) return CategoryHandlerPolicy.Failure(CategoryErrors.NotFound);
            if (current.Status == CategoryStatus.Archived) return CategoryHandlerPolicy.Failure(CategoryErrors.Archived);
            if (current.ParentCategoryId == input.ParentCategoryId) return CategoryHandlerPolicy.Failure(CategoryErrors.DuplicateSibling);
            if (input.ParentCategoryId is not null)
            {
                var parent = await store.FindCurrentAsync(connection, transaction, input.ParentCategoryId, token);
                if (parent is null) return CategoryHandlerPolicy.Failure(CategoryErrors.ParentNotFound);
                if (parent.Status == CategoryStatus.Archived) return CategoryHandlerPolicy.Failure(CategoryErrors.ParentArchived);
                if (await store.WouldCreateCycleAsync(connection, transaction, input.CategoryId, input.ParentCategoryId, token)) return CategoryHandlerPolicy.Failure(CategoryErrors.Cycle);
            }
            if (await store.SiblingNameExistsAsync(connection, transaction, current.Name, input.ParentCategoryId, input.CategoryId, token)) return CategoryHandlerPolicy.Failure(CategoryErrors.DuplicateSibling);
            var eventId = LedgerId.New().ToString();
            await store.AppendParentAsync(connection, transaction, eventId, current, input.ParentCategoryId, reason, CategoryHandlerPolicy.Actor(actor!), CategoryHandlerPolicy.Now(), token);
            return CategoryHandlerPolicy.Success(new CategoryReparentResult((await store.GetAsync(connection, transaction, input.CategoryId, true, token))!, eventId), LedgerJsonContext.Default.CategoryReparentResult);
        }, cancellationToken);
    }
}

internal static class CategoryHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> LifecycleAsync<T>(LedgerMutationExecutor executor, CategoryStore store, string operationId, string categoryId, string? requestedName, string requestedReason, CategoryLifecycleAction action, SafeActor? actor, string? key, T input, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> inputType, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        RequireLinux();
        if (!ValidMutation(actor, key) || !ValidId(categoryId) || !SpendCategory.TryReason(requestedReason, out var reason) || action == CategoryLifecycleAction.Rename && !SpendCategory.TryName(requestedName, out _)) return Failure(SpendCategory.InvalidError);
        var name = requestedName?.Trim(); var request = Request(operationId, key!, actor!, input, inputType);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, categoryId, token);
            if (current is null) return Failure(CategoryErrors.NotFound);
            if (action == CategoryLifecycleAction.Reactivate)
            {
                if (current.Status == CategoryStatus.Active) return Failure(CategoryErrors.AlreadyActive);
                if (current.ParentCategoryId is not null && (await store.FindCurrentAsync(connection, transaction, current.ParentCategoryId, token))?.Status != CategoryStatus.Active) return Failure(CategoryErrors.AncestorArchived);
                if (await store.SiblingNameExistsAsync(connection, transaction, current.Name, current.ParentCategoryId, current.CategoryId, token)) return Failure(CategoryErrors.DuplicateSibling);
            }
            else
            {
                if (current.Status == CategoryStatus.Archived) return Failure(action == CategoryLifecycleAction.Archive ? CategoryErrors.AlreadyArchived : CategoryErrors.Archived);
                if (action == CategoryLifecycleAction.Archive && await store.HasActiveChildrenAsync(connection, transaction, categoryId, token)) return Failure(CategoryErrors.ActiveChildren);
                if (action == CategoryLifecycleAction.Rename && (string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase) || await store.SiblingNameExistsAsync(connection, transaction, name!, current.ParentCategoryId, current.CategoryId, token))) return Failure(CategoryErrors.DuplicateSibling);
            }
            var eventId = LedgerId.New().ToString(); await store.AppendLifecycleAsync(connection, transaction, eventId, current, action, name, reason, Actor(actor!), Now(), token);
            return Success(new CategoryLifecycleResult((await store.GetAsync(connection, transaction, categoryId, true, token))!, eventId), LedgerJsonContext.Default.CategoryLifecycleResult);
        }, cancellationToken);
    }

    public static bool ValidMutation(SafeActor? actor, string? key) => actor is not null && !string.IsNullOrWhiteSpace(key);
    public static bool ValidId(string? value) => LedgerId.TryParse(value, out _, out _);
    public static bool ValidOptionalId(string? value) => value is null || ValidId(value);
    public static void RequireLinux() { if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections."); }
    public static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    public static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    public static IdempotencyRequest Request<T>(string operation, string key, SafeActor actor, T input, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => new("1.0", operation, key, Actor(actor), JsonSerializer.SerializeToElement(input, type), null);
    public static CommandResult<JsonElement> Success<T>(T value, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, type));
    public static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
