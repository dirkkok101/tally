using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ingest.Identity;
using Tally.Domain.Ingest.Manifests;
using Tally.Domain.Ingest.Normalization;
using Tally.Domain.Ingest.Reconciliation;
using Tally.Infrastructure.Ingest.Pdf;

namespace Tally.Features.Ingest.Preview;

// DD-INGEST-SOURCE-DESCRIPTION-ABSENCE / DD-INGEST-FORMAT-ADAPTERS
public static class PreviewManifestMapper
{
    public const string SourceDescriptionUnavailableMarker = "Description unavailable in source statement";

    public sealed record MappedPreview(
        CanonicalManifest Manifest,
        IngestOutcomeCounts Counts,
        ReconciliationSummary Reconciliation,
        IReadOnlyList<ImportCandidate> Candidates,
        IReadOnlyList<SourceRecordOutcome> Outcomes,
        string AdapterVersion,
        string AdapterVariantId,
        bool Committable);

    public static MappedPreview Map(
        string sourceFingerprint,
        AccountDetail account,
        ExtractedStatement statement,
        string ledgerContractVersion,
        SafeActor actor)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(actor);

        var accountKind = account.AccountClass == AccountClass.Asset
            ? SourceAccountKind.Asset
            : SourceAccountKind.Liability;
        var outcomes = new List<CanonicalManifestOutcome>(statement.OrderedRecords.Count);
        var candidates = new List<ImportCandidate>();
        var sourceOutcomes = new List<SourceRecordOutcome>();
        var reconciliationRecords = new List<ReconciliationRecord>();
        var accepted = 0;
        var excluded = 0;
        var blocked = 0;
        var duplicates = 0;

        foreach (var record in statement.OrderedRecords.OrderBy(record => record.RecordOrdinal))
        {
            var description = ResolveDescription(record);
            var evidence = record.FinancialEvidence with { Description = description };
            var normalized = FinancialNormalizer.Normalize(accountKind, evidence);
            var disposition = normalized.Disposition;
            string? candidateId = null;
            string reason = normalized.ReasonCode;

            if (normalized.Facts is { } facts && disposition == SourceRecordDisposition.AcceptedCandidate)
            {
                var candidateInput = new CandidateIdentityInput(
                    account.AccountId,
                    record.SourceRecordId,
                    facts.SignedAmountMinor,
                    facts.CurrencyCode,
                    facts.TransactionDate,
                    facts.PostingDate,
                    facts.OriginalDescription);
                var identity = IngestIdentity.Candidate(candidateInput);
                var registerEvidence = IngestIdentity.StatementEvidence(candidateInput);
                candidateId = identity.CandidateId;
                var signedAmount = FormatSignedAmount(facts.SignedAmountMinor);
                candidates.Add(new ImportCandidate(
                    identity.CandidateId,
                    record.SourceRecordId,
                    account.AccountId,
                    facts.SignedAmountMinor,
                    facts.CurrencyCode,
                    facts.TransactionDate,
                    facts.PostingDate,
                    facts.OriginalDescription,
                    identity.OpaqueExternalReference,
                    new ImportProvenance(ImportProvenanceKind.StatementImport, identity.OpaqueExternalReference),
                    identity.IdempotencyKey,
                    new FrozenLedgerRecordRequest(
                        ledgerContractVersion,
                        "ledger.transaction.record",
                        identity.IdempotencyKey,
                        actor,
                        new RecordTransactionInput(
                            account.AccountId,
                            signedAmount,
                            facts.CurrencyCode,
                            facts.TransactionDate,
                            facts.PostingDate,
                            facts.OriginalDescription,
                            null,
                            null,
                            registerEvidence)),
                    null));
                accepted++;
            }
            else if (disposition == SourceRecordDisposition.ExcludedNonTransaction)
            {
                excluded++;
            }
            else if (disposition == SourceRecordDisposition.ExactDuplicate)
            {
                duplicates++;
            }
            else
            {
                disposition = SourceRecordDisposition.Blocked;
                blocked++;
            }

            outcomes.Add(new CanonicalManifestOutcome(
                record.SourceRecordId,
                record.RecordOrdinal,
                disposition,
                reason,
                candidateId,
                null));
            sourceOutcomes.Add(new SourceRecordOutcome(
                string.Empty,
                record.SourceRecordId,
                record.RecordOrdinal,
                disposition,
                reason,
                candidateId,
                null));
            reconciliationRecords.Add(new ReconciliationRecord(
                record.SourceRecordId,
                normalized.Facts?.SignedAmountMinor,
                record.RunningBalanceMinor,
                record.SourceControlMinor));
        }

        var reconciliation = StatementReconciler.Reconcile(
            statement.OpeningEconomicBalanceMinor,
            statement.ClosingEconomicBalanceMinor,
            reconciliationRecords);
        var controls = reconciliation.Controls
            .Select(control => new Contracts.Ingest.ReconciliationControl(
                control.Name,
                control.State == ReconciliationControlState.Satisfied,
                control.State.ToString()))
            .ToArray();
        var fullyReconciled = reconciliation.FullyReconciled && blocked == 0;
        var canonical = ManifestCanonicalizer.Canonicalize(new CanonicalManifestInput(
            sourceFingerprint,
            account.AccountId,
            statement.Variant.AdapterVersion,
            ledgerContractVersion,
            statement.Variant.ManifestSchemaVersion.ToString(CultureInfo.InvariantCulture),
            statement.StatementPeriod,
            outcomes,
            candidates,
            controls));

        sourceOutcomes = sourceOutcomes
            .Select(outcome => outcome with { ManifestRevisionId = canonical.ManifestRevisionId })
            .ToList();

        return new MappedPreview(
            canonical,
            new IngestOutcomeCounts(accepted, duplicates, excluded, blocked),
            new ReconciliationSummary(fullyReconciled, controls),
            candidates,
            sourceOutcomes,
            statement.Variant.AdapterVersion,
            statement.Variant.VariantId,
            fullyReconciled && accepted > 0 && blocked == 0);
    }

    public static string ResolveDescription(SourceRecordEvidence record) =>
        record.DescriptionEvidenceKind == DescriptionEvidenceKind.SourceAbsentMarker
            ? SourceDescriptionUnavailableMarker
            : record.FinancialEvidence.Description ?? SourceDescriptionUnavailableMarker;

    private static string FormatSignedAmount(long signedMinor)
    {
        var absolute = Math.Abs(signedMinor) / 100m;
        var formatted = absolute.ToString("0.00", CultureInfo.InvariantCulture);
        return signedMinor < 0 ? "-" + formatted : formatted;
    }
}
