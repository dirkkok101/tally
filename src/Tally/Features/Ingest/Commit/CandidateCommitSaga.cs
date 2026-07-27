using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ingest.Commit;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Integration.Ledger;

namespace Tally.Features.Ingest.Commit;

public static class CommitErrors
{
    public const string InvalidInput = "INGEST-COMMIT-INPUT-INVALID";
    public const string NotFound = "INGEST-COMMIT-NOT-FOUND";
    public const string DigestMismatch = "INGEST-COMMIT-DIGEST-MISMATCH";
    public const string NotApproved = "INGEST-COMMIT-NOT-APPROVED";
    public const string NotCommittable = "INGEST-COMMIT-NOT-COMMITTABLE";
    public const string AccountInactive = "INGEST-COMMIT-ACCOUNT-INACTIVE";
    public const string VersionIncompatible = "INGEST-COMMIT-VERSION-INCOMPATIBLE";
    public const string LockHeld = "INGEST-COMMIT-LOCK-HELD";
    public const string LedgerConflict = "INGEST-COMMIT-LEDGER-CONFLICT";
    public const string LedgerRejected = "INGEST-COMMIT-LEDGER-REJECTED";
    public const string VerificationFailed = "INGEST-COMMIT-VERIFICATION-FAILED";
    public const string Interrupted = "INGEST-COMMIT-INTERRUPTED";
}

[SupportedOSPlatform("linux")]
public sealed class CandidateCommitSaga(
    ReviewStateStore reviewStore,
    CommitStateStore commitStore,
    BatchCommitLock batchLock,
    LedgerContractClient ledgerClient,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<ImportReceipt>> ExecuteAsync(
        CommitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.BatchId) ||
            string.IsNullOrWhiteSpace(command.ManifestRevisionId) ||
            string.IsNullOrWhiteSpace(command.ManifestDigest))
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.InvalidInput);
        }

        var stored = await reviewStore.LoadAsync(command.BatchId, command.ManifestRevisionId, cancellationToken);
        if (stored is null)
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.NotFound);
        }

        if (!string.Equals(stored.CanonicalDigest, command.ManifestDigest, StringComparison.Ordinal))
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.DigestMismatch);
        }

        if (!stored.Approval.Approved)
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.NotApproved);
        }

        if (!stored.Committable ||
            stored.Outcomes.Any(outcome => outcome.Disposition == SourceRecordDisposition.Blocked) ||
            stored.Controls.Any(control => string.Equals(control.Detail, "Mismatched", StringComparison.Ordinal)))
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.NotCommittable);
        }

        if (stored.Candidates.Count == 0)
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.NotCommittable);
        }

        var firstRequest = stored.Candidates[0].FrozenLedgerRequest;
        if (!string.Equals(firstRequest.LedgerContractVersion, stored.LedgerContractVersion, StringComparison.Ordinal) ||
            !string.Equals(stored.LedgerContractVersion, "1.0", StringComparison.Ordinal))
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.VersionIncompatible);
        }

        var account = await ledgerClient.GetAccountAsync(
            stored.SelectedAccountId,
            stored.LedgerContractVersion,
            firstRequest.Actor,
            cancellationToken);
        if (!account.IsSuccess ||
            account.Value is null ||
            account.Value.Status != Contracts.Ledger.Accounts.AccountStatus.Active)
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.AccountInactive);
        }

        await using var held = await batchLock.TryAcquireAsync(command.BatchId, cancellationToken);
        if (held is null)
        {
            return CommandResult<ImportReceipt>.Failure(CommitErrors.LockHeld);
        }

        var now = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var receiptHeader = await commitStore.EnsureReceiptAsync(
            command.BatchId,
            command.ManifestRevisionId,
            now,
            cancellationToken);

        if (receiptHeader.Status is ImportReceiptStatus.Completed)
        {
            var completed = await commitStore.BuildReceiptAsync(
                receiptHeader.ReceiptId,
                command.BatchId,
                command.ManifestRevisionId,
                ImportReceiptStatus.Completed,
                receiptHeader.CreatedAt,
                receiptHeader.UpdatedAt,
                receiptHeader.CompletedAt,
                cancellationToken);
            return CommandResult<ImportReceipt>.Success(completed);
        }

        var workItems = await commitStore.LoadWorkItemsAsync(
            command.BatchId,
            command.ManifestRevisionId,
            cancellationToken);

        string? stopCode = null;
        string? stopCandidate = null;
        IngestErrorCategory stopCategory = IngestErrorCategory.Interrupted;
        MutationPossibility stopMutation = MutationPossibility.Possible;
        IngestRetryAction stopRetry = IngestRetryAction.Resume;
        string stopMessage = "Commit stopped at a durable frontier.";
        string stopDurable = "commit_interrupted";

        foreach (var item in workItems)
        {
            if (CandidateCommitStates.IsTerminal(item.CommitState))
            {
                // Reference-bearing terminals still require structural validity on resume.
                if (command.ResumeMode &&
                    CandidateCommitStates.IsReferenceBearing(item.CommitState) &&
                    !string.IsNullOrWhiteSpace(item.LedgerTransactionId))
                {
                    var recheck = await ledgerClient.GetTransactionAsync(
                        item.LedgerTransactionId,
                        item.FrozenRequest.LedgerContractVersion,
                        item.FrozenRequest.Actor,
                        cancellationToken);
                    if (!recheck.IsSuccess ||
                        recheck.Value is null ||
                        !LedgerImmutableFactsMatch(recheck.Value, item.FrozenRequest, item.LedgerTransactionId))
                    {
                        stopCode = CommitErrors.VerificationFailed;
                        stopCandidate = item.CandidateId;
                        stopCategory = IngestErrorCategory.Ledger;
                        stopMessage = "A terminal candidate failed immutable re-verification.";
                        stopDurable = "commit_verification_failed";
                        stopRetry = IngestRetryAction.Abandon;
                        break;
                    }
                }

                continue;
            }

            // Exact-duplicate outcomes with a prior canonical reference: verify only, no new write.
            if (item.Disposition == SourceRecordDisposition.ExactDuplicate &&
                !string.IsNullOrWhiteSpace(item.PriorCanonicalRef))
            {
                await commitStore.MarkAttemptingAsync(receiptHeader.ReceiptId, item.CandidateId, now, cancellationToken);
                var prior = await ledgerClient.GetTransactionAsync(
                    item.PriorCanonicalRef,
                    item.FrozenRequest.LedgerContractVersion,
                    item.FrozenRequest.Actor,
                    cancellationToken);
                if (!prior.IsSuccess ||
                    prior.Value is null ||
                    !LedgerImmutableFactsMatch(prior.Value, item.FrozenRequest, item.PriorCanonicalRef))
                {
                    await commitStore.MarkTerminalAsync(
                        receiptHeader.ReceiptId,
                        item.CandidateId,
                        CandidateReceiptState.Unresolved,
                        null,
                        CommitErrors.VerificationFailed,
                        now,
                        cancellationToken);
                    stopCode = CommitErrors.VerificationFailed;
                    stopCandidate = item.CandidateId;
                    stopCategory = IngestErrorCategory.Ledger;
                    stopMessage = "Exact-duplicate verification failed.";
                    stopDurable = "commit_verification_failed";
                    break;
                }

                await commitStore.MarkTerminalAsync(
                    receiptHeader.ReceiptId,
                    item.CandidateId,
                    CandidateReceiptState.ExactDuplicate,
                    item.PriorCanonicalRef,
                    null,
                    now,
                    cancellationToken);
                continue;
            }

            if (!CandidateCommitStates.MayAttemptLedgerWrite(item.CommitState) &&
                item.CommitState != CandidateReceiptState.Pending)
            {
                continue;
            }

            // Durable attempting state + frozen request already stored — never hold SQLite across Ledger.
            await commitStore.MarkAttemptingAsync(receiptHeader.ReceiptId, item.CandidateId, now, cancellationToken);

            var recorded = await ledgerClient.RecordTransactionAsync(item.FrozenRequest, cancellationToken);
            if (!recorded.IsSuccess || recorded.Value is null)
            {
                var errorCode = recorded.Error?.Code ?? CommitErrors.LedgerRejected;
                var isConflict = string.Equals(recorded.Error?.Category, "conflict", StringComparison.OrdinalIgnoreCase)
                    || (recorded.Error?.Code?.Contains("IDEMPOTENCY", StringComparison.OrdinalIgnoreCase) ?? false)
                    || recorded.ExitCode == 5;
                var terminalState = isConflict
                    ? CandidateReceiptState.Conflicted
                    : CandidateReceiptState.Rejected;
                await commitStore.MarkTerminalAsync(
                    receiptHeader.ReceiptId,
                    item.CandidateId,
                    terminalState,
                    null,
                    errorCode,
                    now,
                    cancellationToken);

                stopCode = isConflict ? CommitErrors.LedgerConflict : CommitErrors.LedgerRejected;
                stopCandidate = item.CandidateId;
                stopCategory = isConflict ? IngestErrorCategory.Conflict : IngestErrorCategory.Ledger;
                stopMessage = isConflict
                    ? "Ledger reported a durable conflict for the candidate."
                    : "Ledger rejected the candidate.";
                stopDurable = isConflict ? "commit_conflicted" : "commit_rejected";
                stopMutation = MutationPossibility.None;
                stopRetry = isConflict ? IngestRetryAction.Abandon : IngestRetryAction.CorrectSource;
                break;
            }

            var fetched = await ledgerClient.GetTransactionAsync(
                recorded.Value.TransactionId,
                item.FrozenRequest.LedgerContractVersion,
                item.FrozenRequest.Actor,
                cancellationToken);
            if (!fetched.IsSuccess ||
                fetched.Value is null ||
                !LedgerImmutableFactsMatch(fetched.Value, item.FrozenRequest, recorded.Value.TransactionId))
            {
                await commitStore.MarkTerminalAsync(
                    receiptHeader.ReceiptId,
                    item.CandidateId,
                    CandidateReceiptState.Unresolved,
                    recorded.Value.TransactionId,
                    CommitErrors.VerificationFailed,
                    now,
                    cancellationToken);
                stopCode = CommitErrors.VerificationFailed;
                stopCandidate = item.CandidateId;
                stopCategory = IngestErrorCategory.Ledger;
                stopMessage = "Ledger result failed immutable verification.";
                stopDurable = "commit_verification_failed";
                stopMutation = MutationPossibility.Possible;
                stopRetry = IngestRetryAction.Resume;
                break;
            }

            await commitStore.MarkTerminalAsync(
                receiptHeader.ReceiptId,
                item.CandidateId,
                CandidateReceiptState.Accepted,
                recorded.Value.TransactionId,
                null,
                now,
                cancellationToken);
        }

        var finalNow = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (stopCode is not null)
        {
            await commitStore.AppendStopErrorAsync(
                command.BatchId,
                stopCandidate,
                stopCode,
                stopCategory,
                stopMessage,
                stopDurable,
                stopRetry,
                stopMutation,
                finalNow,
                cancellationToken);

            var interrupted = await commitStore.BuildReceiptAsync(
                receiptHeader.ReceiptId,
                command.BatchId,
                command.ManifestRevisionId,
                ImportReceiptStatus.Interrupted,
                receiptHeader.CreatedAt,
                finalNow,
                null,
                cancellationToken);
            await commitStore.InterruptReceiptAsync(
                receiptHeader.ReceiptId,
                command.BatchId,
                interrupted,
                finalNow,
                cancellationToken);

            // Return the durable receipt with success envelope only when fully completed.
            // Stop-frontier failures surface as domain failures with the receipt still persisted.
            return CommandResult<ImportReceipt>.Failure(stopCode);
        }

        var complete = await commitStore.BuildReceiptAsync(
            receiptHeader.ReceiptId,
            command.BatchId,
            command.ManifestRevisionId,
            ImportReceiptStatus.Completed,
            receiptHeader.CreatedAt,
            finalNow,
            finalNow,
            cancellationToken);

        if (complete.UnresolvedCandidateIds.Count > 0 ||
            complete.Counts.Pending > 0 ||
            complete.Counts.Attempting > 0 ||
            complete.Counts.Unresolved > 0)
        {
            await commitStore.AppendStopErrorAsync(
                command.BatchId,
                complete.UnresolvedCandidateIds.FirstOrDefault(),
                CommitErrors.Interrupted,
                IngestErrorCategory.Interrupted,
                "Commit finished with unresolved candidates.",
                "commit_interrupted",
                IngestRetryAction.Resume,
                MutationPossibility.Possible,
                finalNow,
                cancellationToken);
            await commitStore.InterruptReceiptAsync(
                receiptHeader.ReceiptId,
                command.BatchId,
                complete with { Status = ImportReceiptStatus.Interrupted, CompletedAt = null },
                finalNow,
                cancellationToken);
            return CommandResult<ImportReceipt>.Failure(CommitErrors.Interrupted);
        }

        await commitStore.CompleteReceiptAsync(
            receiptHeader.ReceiptId,
            command.BatchId,
            complete,
            finalNow,
            cancellationToken);
        return CommandResult<ImportReceipt>.Success(complete);
    }

    /// <summary>
    /// Terminal equality is restricted to immutable request/evidence facts (LedgerImmutableVerification).
    /// History, lifecycle, category, pool, reconciliation, actor, and recorded time are ignored.
    /// </summary>
    public static bool LedgerImmutableFactsMatch(
        TransactionDetail detail,
        FrozenLedgerRecordRequest request,
        string expectedTransactionId)
    {
        if (!string.Equals(detail.TransactionId, expectedTransactionId, StringComparison.Ordinal) ||
            !string.Equals(detail.AccountId, request.Input.AccountId, StringComparison.Ordinal) ||
            !SignedAmountsEqual(detail.SignedAmount, request.Input.SignedAmount) ||
            !string.Equals(detail.CurrencyCode, request.Input.CurrencyCode, StringComparison.Ordinal) ||
            !string.Equals(detail.TransactionDate, request.Input.TransactionDate, StringComparison.Ordinal) ||
            !NullableStringEquals(detail.PostingDate, request.Input.PostingDate) ||
            !string.Equals(detail.OriginalDescription, request.Input.OriginalDescription, StringComparison.Ordinal) ||
            !NullableStringEquals(detail.PaymentAttribution.InstrumentId, request.Input.InstrumentId) ||
            !NullableStringEquals(detail.PaymentAttribution.CardholderId, request.Input.CardholderId))
        {
            return false;
        }

        if (detail.Evidence.Count != 1)
        {
            return false;
        }

        var evidence = detail.Evidence[0];
        var expected = request.Input.InitialEvidence;
        return evidence.Kind == expected.Kind
            && string.Equals(evidence.LogicalIdentityDigest, expected.LogicalIdentityDigest, StringComparison.Ordinal)
            && string.Equals(evidence.OpaqueExternalReference, expected.OpaqueExternalReference, StringComparison.Ordinal)
            && string.Equals(evidence.ContentFingerprint, expected.ContentFingerprint, StringComparison.Ordinal)
            && EvidenceObservationEquals(evidence.Observation, expected.Observation);
    }

    private static bool EvidenceObservationEquals(
        Contracts.Ledger.Evidence.EvidenceObservation? left,
        Contracts.Ledger.Evidence.EvidenceObservation? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
            && left.SignedAmountMinor == right.SignedAmountMinor
            && string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.Ordinal)
            && string.Equals(left.TransactionDate, right.TransactionDate, StringComparison.Ordinal)
            && NullableStringEquals(left.PostingDate, right.PostingDate)
            && NullableStringEquals(left.InstrumentId, right.InstrumentId)
            && NullableStringEquals(left.CardholderId, right.CardholderId)
            && string.Equals(left.DescriptionFingerprint, right.DescriptionFingerprint, StringComparison.Ordinal);
    }

    private static bool SignedAmountsEqual(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftAmount)
            && decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightAmount)
            && leftAmount == rightAmount;
    }

    private static bool NullableStringEquals(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
}
