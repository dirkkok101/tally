using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger.Evidence;

namespace Tally.Domain.Ledger.Transactions;

public sealed record TransactionFact(
    string AccountId,
    Money SignedAmount,
    LedgerCurrency Currency,
    EffectiveDate TransactionDate,
    EffectiveDate? PostingDate,
    string OriginalDescription,
    string? InstrumentId,
    string? CardholderId,
    EvidenceIdentity EvidenceIdentity,
    RegisterEvidenceInput InitialEvidence)
{
    public const string InvalidError = "LEDGER-TRANSACTION-INVALID";
    public const string EvidenceIncompatibleError = "LEDGER-TRANSACTION-EVIDENCE-INCOMPATIBLE";

    public static bool TryCreate(RecordTransactionInput input, out TransactionFact? fact, out string? error)
    {
        fact = null;
        if (!LedgerId.TryParse(input.AccountId, out _, out _)
            || input.InstrumentId is not null && !LedgerId.TryParse(input.InstrumentId, out _, out _)
            || input.CardholderId is not null && !LedgerId.TryParse(input.CardholderId, out _, out _)
            || !TryDescription(input.OriginalDescription, out var description))
        {
            error = InvalidError;
            return false;
        }

        if (!Money.TryParseTransactionAmount(input.SignedAmount, out var money, out error)) return false;
        if (!LedgerCurrency.TryParse(input.CurrencyCode, out var currency, out error)) return false;
        if (!EffectiveDate.TryParse(input.TransactionDate, out var transactionDate, out error)) return false;
        EffectiveDate? postingDate = null;
        if (input.PostingDate is not null)
        {
            if (!EffectiveDate.TryParse(input.PostingDate, out var parsedPostingDate, out error)) return false;
            postingDate = parsedPostingDate;
        }

        if (!EvidenceIdentity.TryCreate(input.InitialEvidence, out var evidenceIdentity, out error)) return false;
        fact = new(input.AccountId, money, currency, transactionDate, postingDate, description, input.InstrumentId, input.CardholderId, evidenceIdentity, input.InitialEvidence);
        if (!EvidenceMatches(fact, input.InitialEvidence.Observation))
        {
            fact = null;
            error = EvidenceIncompatibleError;
            return false;
        }

        error = null;
        return true;
    }

    public RecordTransactionInput CanonicalInput() => new(
        AccountId,
        SignedAmount.ToString(),
        Currency.Code,
        TransactionDate.ToString(),
        PostingDate?.ToString(),
        OriginalDescription,
        InstrumentId,
        CardholderId,
        InitialEvidence);

    private static bool EvidenceMatches(TransactionFact fact, EvidenceObservation? observation) => observation is null
        || (observation.AccountId is null || observation.AccountId == fact.AccountId)
        && (observation.SignedAmountMinor is null || observation.SignedAmountMinor == fact.SignedAmount.MinorUnits)
        && (observation.CurrencyCode is null || observation.CurrencyCode == fact.Currency.Code)
        && (observation.TransactionDate is null || observation.TransactionDate == fact.TransactionDate.ToString())
        && (observation.PostingDate is null || observation.PostingDate == fact.PostingDate?.ToString())
        && (observation.InstrumentId is null || observation.InstrumentId == fact.InstrumentId)
        && (observation.CardholderId is null || observation.CardholderId == fact.CardholderId);

    private static bool TryDescription(string? value, out string description)
    {
        description = value?.Trim() ?? string.Empty;
        return description.Length is > 0 and <= 512 && description.All(character => !char.IsControl(character));
    }
}
