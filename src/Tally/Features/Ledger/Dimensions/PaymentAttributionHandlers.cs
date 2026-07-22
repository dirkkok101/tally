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

public static class PaymentAttributionErrors
{
    public const string TransactionInactive = "LEDGER-PAYMENT-ATTRIBUTION-TRANSACTION-INACTIVE";
    public const string AccountIncompatible = "LEDGER-PAYMENT-ATTRIBUTION-ACCOUNT-INCOMPATIBLE";
    public const string Stale = "LEDGER-PAYMENT-ATTRIBUTION-STALE";
    public const string AlreadyAssigned = "LEDGER-PAYMENT-ATTRIBUTION-ALREADY-ASSIGNED";
    public const string Unchanged = "LEDGER-PAYMENT-ATTRIBUTION-UNCHANGED";
}

public sealed class AssignPaymentAttributionHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    PaymentIdentityStore identityStore,
    PaymentAttributionStore attributionStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(AssignPaymentAttributionInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentAttributionHandlerPolicy.ExecuteAsync(
            executor, transactionStore, identityStore, attributionStore, "ledger.transaction.attribution.assign",
            input.TransactionId, input.ExpectedAttributionEventId, input.Instrument, input.Cardholder, input.Reason,
            actor, key, input, LedgerJsonContext.Default.AssignPaymentAttributionInput, correct: false, cancellationToken);
}

public sealed class CorrectPaymentAttributionHandler(
    LedgerMutationExecutor executor,
    TransactionStore transactionStore,
    PaymentIdentityStore identityStore,
    PaymentAttributionStore attributionStore)
{
    public Task<CommandResult<JsonElement>> HandleAsync(CorrectPaymentAttributionInput input, SafeActor? actor, string? key, CancellationToken cancellationToken) =>
        PaymentAttributionHandlerPolicy.ExecuteAsync(
            executor, transactionStore, identityStore, attributionStore, "ledger.transaction.attribution.correct",
            input.TransactionId, input.ExpectedAttributionEventId, input.Instrument, input.Cardholder, input.Reason,
            actor, key, input, LedgerJsonContext.Default.CorrectPaymentAttributionInput, correct: true, cancellationToken);
}

internal static class PaymentAttributionHandlerPolicy
{
    public static async Task<CommandResult<JsonElement>> ExecuteAsync<T>(
        LedgerMutationExecutor executor,
        TransactionStore transactionStore,
        PaymentIdentityStore identityStore,
        PaymentAttributionStore attributionStore,
        string operationId,
        string transactionId,
        string expectedEventId,
        InstrumentAttributionInput? instrument,
        CardholderAttributionInput? cardholder,
        string reason,
        SafeActor? actor,
        string? key,
        T input,
        global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> inputType,
        bool correct,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key)
            || !PaymentAttributionPolicy.TryCreate(transactionId, expectedEventId, instrument, cardholder, reason, out var command))
        {
            return Failure(PaymentAttributionPolicy.InvalidError);
        }

        var request = new IdempotencyRequest("1.0", operationId, key, Actor(actor), JsonSerializer.SerializeToElement(input, inputType), null);
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var detail = await transactionStore.GetAsync(connection, transaction, command!.TransactionId, false, token);
            if (detail is null) return Failure(TransactionErrors.NotFound);
            if (detail.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(PaymentAttributionErrors.TransactionInactive);

            var current = await attributionStore.FindCurrentAsync(connection, transaction, command.TransactionId, token)
                ?? throw new InvalidOperationException("Active transaction attribution is missing.");
            if (current.AttributionEventId != command.ExpectedAttributionEventId) return Failure(PaymentAttributionErrors.Stale);
            var currentState = new PaymentAttributionState(current.InstrumentState, current.InstrumentId, current.CardholderState, current.CardholderId);
            if (!correct && PaymentAttributionPolicy.AssignsKnownDimension(currentState, command)) return Failure(PaymentAttributionErrors.AlreadyAssigned);
            var resulting = PaymentAttributionPolicy.Apply(currentState, command);
            if (!PaymentAttributionPolicy.IsChanged(currentState, resulting)) return Failure(PaymentAttributionErrors.Unchanged);

            var identityError = await ValidateResultingIdentitiesAsync(connection, transaction, identityStore, detail.AccountId, resulting, token);
            if (identityError is not null) return Failure(identityError);

            var eventId = LedgerId.New().ToString();
            await attributionStore.AppendAsync(
                connection, transaction, eventId, command.TransactionId,
                resulting.InstrumentState, resulting.InstrumentId, resulting.CardholderState, resulting.CardholderId,
                correct ? TransactionAssignmentAction.Correct : TransactionAssignmentAction.Assign,
                current.AttributionEventId, null, null, command.Reason, Actor(actor), Now(), token);
            detail = await transactionStore.GetAsync(connection, transaction, command.TransactionId, true, token);
            return Success(new PaymentAttributionResult(detail!, eventId));
        }, cancellationToken);
    }

    private static async Task<string?> ValidateResultingIdentitiesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        PaymentIdentityStore identityStore,
        string transactionAccountId,
        PaymentAttributionState resulting,
        CancellationToken cancellationToken)
    {
        if (resulting is { InstrumentState: TransactionKnowledgeState.Known, InstrumentId: { } instrumentId })
        {
            var error = await identityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Instrument, instrumentId, cancellationToken);
            if (error is not null) return error;
            var identity = await identityStore.GetInstrumentIdentityAsync(connection, transaction, instrumentId, cancellationToken);
            if (identity?.AccountId is not null && identity.AccountId != transactionAccountId) return PaymentAttributionErrors.AccountIncompatible;
        }
        if (resulting is { CardholderState: TransactionKnowledgeState.Known, CardholderId: { } cardholderId })
        {
            return await identityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Cardholder, cardholderId, cancellationToken);
        }
        return null;
    }

    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Success(PaymentAttributionResult value) => CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(value, LedgerJsonContext.Default.PaymentAttributionResult));
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
