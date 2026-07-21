using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Infrastructure.Storage.Accounts;

namespace Tally.Features.Ledger.Accounts;

public sealed class CreateAccountHandler(LedgerMutationExecutor executor, AccountStore store)
{
    public const string DuplicateError = "LEDGER-ACCOUNT-DUPLICATE";

    public async Task<CommandResult<JsonElement>> HandleAsync(CreateAccountInput input, SafeActor? actor, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!AccountHandlerPolicy.ValidMutation(actor, idempotencyKey)) return CommandResult<JsonElement>.Failure(AccountDefinition.InvalidError);
        if (!AccountDefinition.TryCreate(input, out var account, out var error)) return CommandResult<JsonElement>.Failure(error!);
        var request = AccountHandlerPolicy.Request("ledger.account.create", idempotencyKey!, actor!, input, LedgerJsonContext.Default.CreateAccountInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (await store.ActiveIdentityExistsAsync(connection, transaction, account!, token)
                || await store.ActiveNameExistsAsync(connection, transaction, account!.DisplayName, null, token))
            {
                return CommandResult<JsonElement>.Failure(DuplicateError);
            }

            var accountId = LedgerId.New().ToString();
            var eventId = LedgerId.New().ToString();
            await store.InsertAsync(connection, transaction, accountId, eventId, account, AccountHandlerPolicy.ActorIdentity(actor!), AccountHandlerPolicy.TrustedNow(), token);
            var detail = await store.GetAsync(connection, transaction, accountId, true, token);
            return AccountHandlerPolicy.Success(detail!, LedgerJsonContext.Default.AccountDetail);
        }, cancellationToken);
    }
}

public sealed class GetAccountHandler(AccountStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetAccountInput input, CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.AccountId, out _, out _)) return CommandResult<JsonElement>.Failure(AccountDefinition.InvalidError);
        var detail = await store.GetAsync(input.AccountId, input.IncludeHistory, cancellationToken);
        return detail is null
            ? CommandResult<JsonElement>.Failure(AccountStore.NotFoundError)
            : AccountHandlerPolicy.Success(detail, LedgerJsonContext.Default.AccountDetail);
    }
}

public sealed class ListAccountsHandler(AccountStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ListAccountsInput input, CancellationToken cancellationToken)
    {
        if (input.Status is not null && !Enum.IsDefined(input.Status.Value)
            || !AccountDefinition.TryInstitutionFilter(input.InstitutionName, out var institution))
        {
            return CommandResult<JsonElement>.Failure(AccountDefinition.InvalidError);
        }

        var items = await store.ListAsync(input.Status, institution, cancellationToken);
        return AccountHandlerPolicy.Success(new AccountListResult(items), LedgerJsonContext.Default.AccountListResult);
    }
}

public sealed class RenameAccountHandler(LedgerMutationExecutor executor, AccountStore store)
{
    public const string NameConflictError = "LEDGER-ACCOUNT-NAME-CONFLICT";

    public async Task<CommandResult<JsonElement>> HandleAsync(RenameAccountInput input, SafeActor? actor, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!AccountHandlerPolicy.ValidMutation(actor, idempotencyKey)
            || !LedgerId.TryParse(input.AccountId, out _, out _)
            || !AccountDefinition.TryDisplayName(input.NewDisplayName, out var displayName)
            || !AccountDefinition.TryReason(input.Reason, out var reason))
        {
            return CommandResult<JsonElement>.Failure(AccountDefinition.InvalidError);
        }

        var request = AccountHandlerPolicy.Request("ledger.account.rename", idempotencyKey!, actor!, input, LedgerJsonContext.Default.RenameAccountInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, input.AccountId, token);
            if (current is null) return CommandResult<JsonElement>.Failure(AccountStore.NotFoundError);
            if (current.Status == AccountStatus.Archived) return CommandResult<JsonElement>.Failure(AccountStore.ArchivedError);
            if (string.Equals(current.DisplayName.Trim(), displayName, StringComparison.OrdinalIgnoreCase)
                || await store.ActiveNameExistsAsync(connection, transaction, displayName, input.AccountId, token))
            {
                return CommandResult<JsonElement>.Failure(NameConflictError);
            }

            var eventId = LedgerId.New().ToString();
            await store.AppendLifecycleAsync(connection, transaction, eventId, current, AccountLifecycleAction.Rename, displayName, reason, AccountHandlerPolicy.ActorIdentity(actor!), AccountHandlerPolicy.TrustedNow(), token);
            var detail = await store.GetAsync(connection, transaction, input.AccountId, true, token);
            return AccountHandlerPolicy.Success(new AccountLifecycleResult(detail!, eventId), LedgerJsonContext.Default.AccountLifecycleResult);
        }, cancellationToken);
    }
}

public sealed class ArchiveAccountHandler(LedgerMutationExecutor executor, AccountStore store)
{
    public const string AlreadyArchivedError = "LEDGER-ACCOUNT-ALREADY-ARCHIVED";

    public async Task<CommandResult<JsonElement>> HandleAsync(ArchiveAccountInput input, SafeActor? actor, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!AccountHandlerPolicy.ValidMutation(actor, idempotencyKey)
            || !LedgerId.TryParse(input.AccountId, out _, out _)
            || !AccountDefinition.TryReason(input.Reason, out var reason))
        {
            return CommandResult<JsonElement>.Failure(AccountDefinition.InvalidError);
        }

        var request = AccountHandlerPolicy.Request("ledger.account.archive", idempotencyKey!, actor!, input, LedgerJsonContext.Default.ArchiveAccountInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, input.AccountId, token);
            if (current is null) return CommandResult<JsonElement>.Failure(AccountStore.NotFoundError);
            if (current.Status == AccountStatus.Archived) return CommandResult<JsonElement>.Failure(AlreadyArchivedError);
            var eventId = LedgerId.New().ToString();
            await store.AppendLifecycleAsync(connection, transaction, eventId, current, AccountLifecycleAction.Archive, null, reason, AccountHandlerPolicy.ActorIdentity(actor!), AccountHandlerPolicy.TrustedNow(), token);
            var detail = await store.GetAsync(connection, transaction, input.AccountId, true, token);
            return AccountHandlerPolicy.Success(new AccountLifecycleResult(detail!, eventId), LedgerJsonContext.Default.AccountLifecycleResult);
        }, cancellationToken);
    }
}

internal static class AccountHandlerPolicy
{
    public static bool ValidMutation(SafeActor? actor, string? idempotencyKey) => OperatingSystem.IsLinux() && actor is not null && !string.IsNullOrWhiteSpace(idempotencyKey);

    public static string ActorIdentity(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    public static string TrustedNow() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static IdempotencyRequest Request<T>(string operationId, string idempotencyKey, SafeActor actor, T input, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        new("1.0", operationId, idempotencyKey, ActorIdentity(actor), JsonSerializer.SerializeToElement(input, typeInfo), null);

    public static CommandResult<JsonElement> Success<T>(T value, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, typeInfo));
}
