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

public static class PaymentIdentityErrors
{
    public const string InstrumentNotFound = "LEDGER-PAYMENT-INSTRUMENT-NOT-FOUND";
    public const string CardholderNotFound = "LEDGER-CARDHOLDER-NOT-FOUND";
    public const string InstrumentDuplicate = "LEDGER-PAYMENT-INSTRUMENT-DUPLICATE";
    public const string CardholderDuplicate = "LEDGER-CARDHOLDER-DUPLICATE";
    public const string InstrumentAccountNotActive = "LEDGER-PAYMENT-INSTRUMENT-ACCOUNT-NOT-ACTIVE";
    public const string InstrumentArchived = "LEDGER-PAYMENT-INSTRUMENT-ARCHIVED";
    public const string CardholderArchived = "LEDGER-CARDHOLDER-ARCHIVED";
    public const string InstrumentAlreadyArchived = "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ARCHIVED";
    public const string CardholderAlreadyArchived = "LEDGER-CARDHOLDER-ALREADY-ARCHIVED";
    public const string InstrumentAlreadyActive = "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ACTIVE";
    public const string CardholderAlreadyActive = "LEDGER-CARDHOLDER-ALREADY-ACTIVE";
}

public sealed class CreatePaymentInstrumentHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(CreatePaymentInstrumentInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!PaymentIdentityHandlerPolicy.ValidMutation(actor, key)
            || !PaymentIdentity.TryLabel(input.Label, out var label)
            || !PaymentIdentity.TryMaskedSuffix(input.MaskedSuffix, out var suffix)
            || !PaymentIdentityHandlerPolicy.ValidOptionalId(input.AccountId))
        {
            return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        }

        var request = PaymentIdentityHandlerPolicy.Request("ledger.instrument.create", key!, actor!, input, LedgerJsonContext.Default.CreatePaymentInstrumentInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (input.AccountId is not null && !await store.AccountIsActiveAsync(connection, transaction, input.AccountId, token))
            {
                return PaymentIdentityHandlerPolicy.Failure(PaymentIdentityErrors.InstrumentAccountNotActive);
            }

            if (await store.ActiveLabelExistsAsync(connection, transaction, PaymentIdentityKind.Instrument, label, null, token)
                || await store.ActiveInstrumentIdentityExistsAsync(connection, transaction, input.AccountId, suffix, null, token))
            {
                return PaymentIdentityHandlerPolicy.Failure(PaymentIdentityErrors.InstrumentDuplicate);
            }

            var instrumentId = LedgerId.New().ToString();
            var lifecycleEventId = LedgerId.New().ToString();
            await store.InsertInstrumentAsync(connection, transaction, instrumentId, lifecycleEventId, label, input.AccountId, suffix, PaymentIdentityHandlerPolicy.Actor(actor!), PaymentIdentityHandlerPolicy.Now(), token);
            return PaymentIdentityHandlerPolicy.Success((await store.GetInstrumentAsync(connection, transaction, instrumentId, true, token))!, LedgerJsonContext.Default.PaymentInstrumentDetail);
        }, cancellationToken);
    }
}

public sealed class GetPaymentInstrumentHandler(PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetPaymentInstrumentInput input, CancellationToken cancellationToken)
    {
        if (!PaymentIdentityHandlerPolicy.ValidId(input.InstrumentId)) return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        var detail = await store.GetInstrumentAsync(input.InstrumentId, input.IncludeHistory, cancellationToken);
        return detail is null
            ? PaymentIdentityHandlerPolicy.Failure(PaymentIdentityErrors.InstrumentNotFound)
            : PaymentIdentityHandlerPolicy.Success(detail, LedgerJsonContext.Default.PaymentInstrumentDetail);
    }
}

public sealed class ListPaymentInstrumentsHandler(PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ListPaymentInstrumentsInput input, CancellationToken cancellationToken)
    {
        if (input.Status is not null && !Enum.IsDefined(input.Status.Value)
            || !PaymentIdentityHandlerPolicy.ValidOptionalId(input.AccountId))
        {
            return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        }

        var items = await store.ListInstrumentsAsync(input.Status, input.AccountId, cancellationToken);
        return PaymentIdentityHandlerPolicy.Success(new PaymentInstrumentListResult(items), LedgerJsonContext.Default.PaymentInstrumentListResult);
    }
}

public sealed class RenamePaymentInstrumentHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(RenamePaymentInstrumentInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Instrument, "ledger.instrument.rename", input.InstrumentId, input.NewLabel, input.Reason, PaymentIdentityLifecycleAction.Rename, actor, key, input, LedgerJsonContext.Default.RenamePaymentInstrumentInput, cancellationToken);
}

public sealed class ArchivePaymentInstrumentHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ArchivePaymentInstrumentInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Instrument, "ledger.instrument.archive", input.InstrumentId, null, input.Reason, PaymentIdentityLifecycleAction.Archive, actor, key, input, LedgerJsonContext.Default.ArchivePaymentInstrumentInput, cancellationToken);
}

public sealed class ReactivatePaymentInstrumentHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ReactivatePaymentInstrumentInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Instrument, "ledger.instrument.reactivate", input.InstrumentId, null, input.Reason, PaymentIdentityLifecycleAction.Reactivate, actor, key, input, LedgerJsonContext.Default.ReactivatePaymentInstrumentInput, cancellationToken);
}

public sealed class CreateCardholderHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(CreateCardholderInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!PaymentIdentityHandlerPolicy.ValidMutation(actor, key) || !PaymentIdentity.TryLabel(input.Label, out var label))
        {
            return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        }

        var request = PaymentIdentityHandlerPolicy.Request("ledger.cardholder.create", key!, actor!, input, LedgerJsonContext.Default.CreateCardholderInput);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (await store.ActiveLabelExistsAsync(connection, transaction, PaymentIdentityKind.Cardholder, label, null, token))
            {
                return PaymentIdentityHandlerPolicy.Failure(PaymentIdentityErrors.CardholderDuplicate);
            }

            var cardholderId = LedgerId.New().ToString();
            var lifecycleEventId = LedgerId.New().ToString();
            await store.InsertCardholderAsync(connection, transaction, cardholderId, lifecycleEventId, label, PaymentIdentityHandlerPolicy.Actor(actor!), PaymentIdentityHandlerPolicy.Now(), token);
            return PaymentIdentityHandlerPolicy.Success((await store.GetCardholderAsync(connection, transaction, cardholderId, true, token))!, LedgerJsonContext.Default.CardholderDetail);
        }, cancellationToken);
    }
}

public sealed class GetCardholderHandler(PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetCardholderInput input, CancellationToken cancellationToken)
    {
        if (!PaymentIdentityHandlerPolicy.ValidId(input.CardholderId)) return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        var detail = await store.GetCardholderAsync(input.CardholderId, input.IncludeHistory, cancellationToken);
        return detail is null
            ? PaymentIdentityHandlerPolicy.Failure(PaymentIdentityErrors.CardholderNotFound)
            : PaymentIdentityHandlerPolicy.Success(detail, LedgerJsonContext.Default.CardholderDetail);
    }
}

public sealed class ListCardholdersHandler(PaymentIdentityStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ListCardholdersInput input, CancellationToken cancellationToken)
    {
        if (input.Status is not null && !Enum.IsDefined(input.Status.Value)) return PaymentIdentityHandlerPolicy.Failure(PaymentIdentity.InvalidError);
        var items = await store.ListCardholdersAsync(input.Status, cancellationToken);
        return PaymentIdentityHandlerPolicy.Success(new CardholderListResult(items), LedgerJsonContext.Default.CardholderListResult);
    }
}

public sealed class RenameCardholderHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(RenameCardholderInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Cardholder, "ledger.cardholder.rename", input.CardholderId, input.NewLabel, input.Reason, PaymentIdentityLifecycleAction.Rename, actor, key, input, LedgerJsonContext.Default.RenameCardholderInput, cancellationToken);
}

public sealed class ArchiveCardholderHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ArchiveCardholderInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Cardholder, "ledger.cardholder.archive", input.CardholderId, null, input.Reason, PaymentIdentityLifecycleAction.Archive, actor, key, input, LedgerJsonContext.Default.ArchiveCardholderInput, cancellationToken);
}

public sealed class ReactivateCardholderHandler(LedgerMutationExecutor executor, PaymentIdentityStore store)
{
    public Task<CommandResult<JsonElement>> HandleAsync(ReactivateCardholderInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentIdentityHandlerPolicy.LifecycleAsync(executor, store, PaymentIdentityKind.Cardholder, "ledger.cardholder.reactivate", input.CardholderId, null, input.Reason, PaymentIdentityLifecycleAction.Reactivate, actor, key, input, LedgerJsonContext.Default.ReactivateCardholderInput, cancellationToken);
}

internal static class PaymentIdentityHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> LifecycleAsync<TInput>(
        LedgerMutationExecutor executor,
        PaymentIdentityStore store,
        PaymentIdentityKind kind,
        string operationId,
        string id,
        string? requestedLabel,
        string requestedReason,
        PaymentIdentityLifecycleAction action,
        SafeActor? actor,
        string? key,
        TInput input,
        JsonTypeInfo<TInput> inputType,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!ValidMutation(actor, key)
            || !ValidId(id)
            || !PaymentIdentity.TryReason(requestedReason, out var reason)
            || action == PaymentIdentityLifecycleAction.Rename && !PaymentIdentity.TryLabel(requestedLabel, out _))
        {
            return Failure(PaymentIdentity.InvalidError);
        }

        var label = requestedLabel?.Trim();
        var request = Request(operationId, key!, actor!, input, inputType);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await store.FindCurrentAsync(connection, transaction, kind, id, token);
            if (current is null) return Failure(NotFound(kind));

            if (action == PaymentIdentityLifecycleAction.Reactivate)
            {
                if (current.Status == PaymentIdentityStatus.Active) return Failure(AlreadyActive(kind));
                if (kind == PaymentIdentityKind.Instrument && !await store.InstrumentAssociationIsActiveAsync(connection, transaction, id, token))
                {
                    return Failure(PaymentIdentityErrors.InstrumentAccountNotActive);
                }

                if (await store.ActiveLabelExistsAsync(connection, transaction, kind, current.Label, current.Id, token)) return Failure(Duplicate(kind));
                if (kind == PaymentIdentityKind.Instrument)
                {
                    var identity = await store.GetInstrumentIdentityAsync(connection, transaction, id, token);
                    if (identity is { MaskedSuffix: not null }
                        && await store.ActiveInstrumentIdentityExistsAsync(connection, transaction, identity.AccountId, identity.MaskedSuffix, id, token))
                    {
                        return Failure(PaymentIdentityErrors.InstrumentDuplicate);
                    }
                }
            }
            else
            {
                if (current.Status == PaymentIdentityStatus.Archived)
                {
                    return Failure(action == PaymentIdentityLifecycleAction.Archive ? AlreadyArchived(kind) : Archived(kind));
                }

                if (action == PaymentIdentityLifecycleAction.Rename
                    && (string.Equals(current.Label, label, StringComparison.OrdinalIgnoreCase)
                        || await store.ActiveLabelExistsAsync(connection, transaction, kind, label!, id, token)))
                {
                    return Failure(Duplicate(kind));
                }
            }

            var eventId = LedgerId.New().ToString();
            await store.AppendLifecycleAsync(connection, transaction, kind, eventId, current, action, label, reason, Actor(actor!), Now(), token);
            if (kind == PaymentIdentityKind.Instrument)
            {
                var instrument = (await store.GetInstrumentAsync(connection, transaction, id, true, token))!;
                return Success(new PaymentInstrumentLifecycleResult(instrument, eventId), LedgerJsonContext.Default.PaymentInstrumentLifecycleResult);
            }

            var cardholder = (await store.GetCardholderAsync(connection, transaction, id, true, token))!;
            return Success(new CardholderLifecycleResult(cardholder, eventId), LedgerJsonContext.Default.CardholderLifecycleResult);
        }, cancellationToken);
    }

    public static bool ValidMutation(SafeActor? actor, string? key) => actor is not null && !string.IsNullOrWhiteSpace(key);
    public static bool ValidId(string? value) => LedgerId.TryParse(value, out _, out _);
    public static bool ValidOptionalId(string? value) => value is null || ValidId(value);
    public static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    public static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    public static IdempotencyRequest Request<T>(string operation, string key, SafeActor actor, T input, JsonTypeInfo<T> type) => new("1.0", operation, key, Actor(actor), JsonSerializer.SerializeToElement(input, type), null);
    public static CommandResult<JsonElement> Success<T>(T value, JsonTypeInfo<T> type) => CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, type));
    public static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
    private static string NotFound(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? PaymentIdentityErrors.InstrumentNotFound : PaymentIdentityErrors.CardholderNotFound;
    private static string Duplicate(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? PaymentIdentityErrors.InstrumentDuplicate : PaymentIdentityErrors.CardholderDuplicate;
    private static string Archived(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? PaymentIdentityErrors.InstrumentArchived : PaymentIdentityErrors.CardholderArchived;
    private static string AlreadyArchived(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? PaymentIdentityErrors.InstrumentAlreadyArchived : PaymentIdentityErrors.CardholderAlreadyArchived;
    private static string AlreadyActive(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? PaymentIdentityErrors.InstrumentAlreadyActive : PaymentIdentityErrors.CardholderAlreadyActive;
}
