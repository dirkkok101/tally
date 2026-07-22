using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Evidence;

public static class EvidenceLinkErrors
{
    public const string Invalid = "LEDGER-EVIDENCE-LINK-INVALID";
    public const string EvidenceNotFound = "LEDGER-EVIDENCE-LINK-EVIDENCE-NOT-FOUND";
    public const string TransactionInactive = "LEDGER-EVIDENCE-LINK-TRANSACTION-INACTIVE";
    public const string Conflict = "LEDGER-EVIDENCE-LINK-CONFLICT";
}

public sealed class LinkSupportingEvidenceHandler(
    LedgerMutationExecutor executor,
    EvidenceStore evidenceStore,
    TransactionStore transactionStore)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        LinkSupportingEvidenceInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        var reason = input.Reason?.Trim() ?? string.Empty;
        if (actor is null || string.IsNullOrWhiteSpace(key)
            || !LedgerId.TryParse(input.TransactionId, out _, out _)
            || !LedgerId.TryParse(input.EvidenceId, out _, out _)
            || reason.Length is 0 or > 512 || reason.Any(char.IsControl))
        {
            return Failure(EvidenceLinkErrors.Invalid);
        }

        var canonicalInput = new LinkSupportingEvidenceInput(input.TransactionId, input.EvidenceId, reason);
        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            "ledger.evidence.link-supporting",
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, LedgerJsonContext.Default.LinkSupportingEvidenceInput),
            new LogicalEffectIdentity("evidence-link-supporting:" + input.EvidenceId, "evidence_supporting_link"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var detail = await transactionStore.GetAsync(connection, transaction, input.TransactionId, false, token);
            if (detail is null) return Failure(TransactionErrors.NotFound);
            if (detail.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(EvidenceLinkErrors.TransactionInactive);

            var evidence = await evidenceStore.GetAsync(connection, transaction, input.EvidenceId, false, token);
            if (evidence is null) return Failure(EvidenceLinkErrors.EvidenceNotFound);
            if (!Compatible(detail, evidence.Observation)) return Failure(TransactionFact.EvidenceIncompatibleError);

            var currentLinks = await evidenceStore.CurrentLinksAsync(connection, transaction, input.EvidenceId, token);
            if (currentLinks.Count > 0)
            {
                if (currentLinks is [var current]
                    && current.TransactionId == input.TransactionId
                    && current.Role == EvidenceLinkRole.Supporting
                    && current.Action == EvidenceLinkAction.Link)
                {
                    return await ResultAsync(current.LinkEventId, token);
                }

                return Failure(EvidenceLinkErrors.Conflict);
            }

            var linkEventId = LedgerId.New().ToString();
            await evidenceStore.AppendSupportingLinkAsync(
                connection, transaction, linkEventId, input.EvidenceId, input.TransactionId,
                reason, actorIdentity, Now(), token);
            return await ResultAsync(linkEventId, token);

            async Task<CommandResult<JsonElement>> ResultAsync(string linkId, CancellationToken resultToken)
            {
                var resultTransaction = await transactionStore.GetAsync(connection, transaction, input.TransactionId, true, resultToken);
                var resultEvidence = await evidenceStore.GetAsync(connection, transaction, input.EvidenceId, true, resultToken);
                return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(
                    new EvidenceLinkResult(resultTransaction!, resultEvidence!, linkId),
                    LedgerJsonContext.Default.EvidenceLinkResult));
            }
        }, cancellationToken);
    }

    private static bool Compatible(TransactionDetail transaction, EvidenceObservation? observation)
    {
        if (observation is null) return true;
        if (!Money.TryParseTransactionAmount(transaction.SignedAmount, out var amount, out _)) return false;
        return (observation.AccountId is null || observation.AccountId == transaction.AccountId)
            && (observation.SignedAmountMinor is null || observation.SignedAmountMinor == amount.MinorUnits)
            && (observation.CurrencyCode is null || observation.CurrencyCode == transaction.CurrencyCode)
            && (observation.TransactionDate is null || observation.TransactionDate == transaction.TransactionDate)
            && (observation.PostingDate is null || observation.PostingDate == transaction.PostingDate)
            && (observation.InstrumentId is null || observation.InstrumentId == transaction.PaymentAttribution.InstrumentId)
            && (observation.CardholderId is null || observation.CardholderId == transaction.PaymentAttribution.CardholderId);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
