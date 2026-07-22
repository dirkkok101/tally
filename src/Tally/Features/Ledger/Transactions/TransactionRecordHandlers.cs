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
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Transactions;

public static class TransactionErrors
{
    public const string NotFound = "LEDGER-TRANSACTION-NOT-FOUND";
    public const string AttributionIncompatible = "LEDGER-TRANSACTION-ATTRIBUTION-INCOMPATIBLE";
    public const string EvidenceConflict = "LEDGER-TRANSACTION-EVIDENCE-CONFLICT";
}

public sealed class RecordTransactionHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    PaymentIdentityStore paymentIdentityStore,
    EvidenceStore evidenceStore,
    TransactionStore transactionStore)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(RecordTransactionInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key)) return CommandResult<JsonElement>.Failure(TransactionFact.InvalidError);
        if (!TransactionFact.TryCreate(input, out var fact, out var error)) return CommandResult<JsonElement>.Failure(error!);

        var actorIdentity = Actor(actor);
        var canonicalInput = fact!.CanonicalInput();
        var request = new IdempotencyRequest(
            "1.0",
            "ledger.transaction.record",
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, LedgerJsonContext.Default.RecordTransactionInput),
            new LogicalEffectIdentity("transaction-evidence:" + fact.EvidenceIdentity.LogicalIdentityDigest, "transaction_record"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var accountError = await accountStore.ActiveWriteErrorAsync(connection, transaction, fact.AccountId, token);
            if (accountError is not null) return CommandResult<JsonElement>.Failure(accountError);

            if (fact.InstrumentId is not null)
            {
                var instrumentError = await paymentIdentityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Instrument, fact.InstrumentId, token);
                if (instrumentError is not null) return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
                var identity = await paymentIdentityStore.GetInstrumentIdentityAsync(connection, transaction, fact.InstrumentId, token);
                if (identity?.AccountId is not null && identity.AccountId != fact.AccountId) return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
            }

            if (fact.CardholderId is not null
                && await paymentIdentityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Cardholder, fact.CardholderId, token) is not null)
            {
                return CommandResult<JsonElement>.Failure(TransactionErrors.AttributionIncompatible);
            }

            if (!await evidenceStore.ObservationReferencesExistAsync(connection, transaction, fact.InitialEvidence.Observation, token))
            {
                return CommandResult<JsonElement>.Failure(TransactionFact.EvidenceIncompatibleError);
            }

            if (await transactionStore.EvidenceIdentityExistsAsync(connection, transaction, fact.EvidenceIdentity.LogicalIdentityDigest, token))
            {
                return CommandResult<JsonElement>.Failure(TransactionErrors.EvidenceConflict);
            }

            var transactionId = LedgerId.New().ToString();
            var initialAttributionEventId = LedgerId.New().ToString();
            var assignedAttributionEventId = fact.InstrumentId is null && fact.CardholderId is null ? null : LedgerId.New().ToString();
            var poolAssignmentEventId = LedgerId.New().ToString();
            var evidenceLinkEventId = LedgerId.New().ToString();
            var recordedAt = Now();
            await transactionStore.InsertFactAndDefaultsAsync(
                connection, transaction, transactionId, initialAttributionEventId, assignedAttributionEventId,
                poolAssignmentEventId, fact, recordedAt, Environment.UserName, actorIdentity, token);
            var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, fact.EvidenceIdentity, fact.InitialEvidence, actorIdentity, recordedAt, token);
            await transactionStore.InsertInitialEvidenceLinkAsync(connection, transaction, evidenceLinkEventId, evidence.EvidenceId, transactionId, actorIdentity, recordedAt, token);
            var detail = await transactionStore.GetAsync(connection, transaction, transactionId, true, token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail!, LedgerJsonContext.Default.TransactionDetail));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

public sealed class GetTransactionHandler(TransactionStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetTransactionInput input, CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.TransactionId, out _, out _)) return CommandResult<JsonElement>.Failure(TransactionFact.InvalidError);
        var detail = await store.GetAsync(input.TransactionId, input.IncludeHistory, cancellationToken);
        return detail is null
            ? CommandResult<JsonElement>.Failure(TransactionErrors.NotFound)
            : CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail, LedgerJsonContext.Default.TransactionDetail));
    }
}
