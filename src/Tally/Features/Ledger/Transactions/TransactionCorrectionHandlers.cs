using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Transactions;

public sealed class TransactionCorrectionHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    PaymentIdentityStore paymentIdentityStore,
    EvidenceStore evidenceStore,
    TransactionStore transactionStore,
    RelationshipStore relationshipStore)
{
    public Task<CommandResult<JsonElement>> VoidAsync(
        VoidTransactionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.TransactionId, out _, out _)
            || !TransactionLifecycle.TryReason(input.Reason, out var reason)
            || actor is null
            || string.IsNullOrWhiteSpace(key))
        {
            return Invalid();
        }

        var canonical = new VoidTransactionInput(input.TransactionId, reason);
        return ExecuteAsync(
            input.TransactionId,
            TransactionLifecycleAction.Void,
            canonical,
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            replacementFact: null,
            reason,
            actor,
            key,
            cancellationToken);
    }

    public Task<CommandResult<JsonElement>> SupersedeAsync(
        SupersedeTransactionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.TransactionId, out _, out _)
            || !TransactionLifecycle.TryReason(input.Reason, out var reason)
            || actor is null
            || string.IsNullOrWhiteSpace(key))
        {
            return Invalid();
        }

        if (!TransactionFact.TryCreate(input.Replacement, out var replacementFact, out var replacementError))
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(replacementError!));
        }

        var canonical = new SupersedeTransactionInput(input.TransactionId, replacementFact!.CanonicalInput(), reason);
        return ExecuteAsync(
            input.TransactionId,
            TransactionLifecycleAction.Superseded,
            canonical,
            TransactionCorrectionJsonContext.Default.SupersedeTransactionInput,
            replacementFact,
            reason,
            actor,
            key,
            cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> ExecuteAsync<TInput>(
        string transactionId,
        TransactionLifecycleAction action,
        TInput canonicalInput,
        global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        TransactionFact? replacementFact,
        string reason,
        SafeActor actor,
        string key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            action == TransactionLifecycleAction.Void ? "ledger.transaction.void" : "ledger.transaction.supersede",
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, inputType),
            new LogicalEffectIdentity("transaction-correction:" + transactionId, "transaction_correction"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var original = await transactionStore.GetAsync(connection, transaction, transactionId, includeHistory: false, token);
            if (TransactionLifecycle.ValidateActive(original) is { } lifecycleError)
                return CommandResult<JsonElement>.Failure(lifecycleError);

            var occurredAt = Now();
            var lifecycleEventId = LedgerId.New().ToString();
            TransactionDetail? replacement = null;
            if (action == TransactionLifecycleAction.Void)
            {
                await transactionStore.VoidAsync(
                    connection,
                    transaction,
                    lifecycleEventId,
                    transactionId,
                    reason,
                    actorIdentity,
                    occurredAt,
                    token);
            }
            else
            {
                var accountError = await accountStore.ActiveWriteErrorAsync(connection, transaction, replacementFact!.AccountId, token);
                if (accountError is not null) return CommandResult<JsonElement>.Failure(accountError);
                if (replacementFact.InstrumentId is not null)
                {
                    if (await paymentIdentityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Instrument, replacementFact.InstrumentId, token) is not null)
                        return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
                    var identity = await paymentIdentityStore.GetInstrumentIdentityAsync(connection, transaction, replacementFact.InstrumentId, token);
                    if (identity?.AccountId is not null && identity.AccountId != replacementFact.AccountId)
                        return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
                }
                if (replacementFact.CardholderId is not null
                    && await paymentIdentityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Cardholder, replacementFact.CardholderId, token) is not null)
                    return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
                if (!await evidenceStore.ObservationReferencesExistAsync(connection, transaction, replacementFact.InitialEvidence.Observation, token))
                    return CommandResult<JsonElement>.Failure(TransactionFact.EvidenceIncompatibleError);
                if (await transactionStore.EvidenceIdentityExistsAsync(connection, transaction, replacementFact.EvidenceIdentity.LogicalIdentityDigest, token))
                    return CommandResult<JsonElement>.Failure(TransactionErrors.EvidenceConflict);

                var replacementId = LedgerId.New().ToString();
                await transactionStore.SupersedeAsync(
                    connection,
                    transaction,
                    lifecycleEventId,
                    transactionId,
                    replacementId,
                    LedgerId.New().ToString(),
                    replacementFact.InstrumentId is null && replacementFact.CardholderId is null ? null : LedgerId.New().ToString(),
                    LedgerId.New().ToString(),
                    replacementFact,
                    reason,
                    actorIdentity,
                    occurredAt,
                    token);
                var evidence = await evidenceStore.RegisterInitialAsync(
                    connection,
                    transaction,
                    replacementFact.EvidenceIdentity,
                    replacementFact.InitialEvidence,
                    actorIdentity,
                    occurredAt,
                    token);
                await transactionStore.InsertInitialEvidenceLinkAsync(
                    connection,
                    transaction,
                    LedgerId.New().ToString(),
                    evidence.EvidenceId,
                    replacementId,
                    actorIdentity,
                    occurredAt,
                    token);
                replacement = await transactionStore.GetAsync(connection, transaction, replacementId, includeHistory: true, token);
            }

            var retiredRelationship = await relationshipStore.RetireForTransactionAsync(
                connection,
                transaction,
                transactionId,
                LedgerId.New().ToString(),
                replacementRelationshipId: null,
                reconciliationDecisionId: null,
                reason,
                actorIdentity,
                occurredAt,
                token);
            var updatedOriginal = await transactionStore.GetAsync(connection, transaction, transactionId, includeHistory: true, token)
                ?? throw new InvalidOperationException("Corrected transaction disappeared inside the writer transaction.");
            var result = new TransactionCorrectionResult(
                action,
                updatedOriginal,
                replacement,
                retiredRelationship is null ? [] : [retiredRelationship]);
            return CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(result, TransactionCorrectionJsonContext.Default.TransactionCorrectionResult));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static Task<CommandResult<JsonElement>> Invalid() =>
        Task.FromResult(CommandResult<JsonElement>.Failure(TransactionLifecycle.InvalidError));
}
