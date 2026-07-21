using Tally.Contracts.Ledger.Evidence;

namespace Tally.Domain.Ledger.Evidence;

public readonly record struct EvidenceIdentity
{
    public const string InvalidEvidenceError = "validation.invalid_input";

    private EvidenceIdentity(string logicalIdentityDigest) => LogicalIdentityDigest = logicalIdentityDigest;

    public string LogicalIdentityDigest { get; }

    public static bool TryCreate(RegisterEvidenceInput input, out EvidenceIdentity identity, out string? error)
    {
        identity = default;
        error = InvalidEvidenceError;
        if (!Enum.IsDefined(input.Kind)
            || !IsFingerprint(input.LogicalIdentityDigest)
            || input.ContentFingerprint is not null && !IsFingerprint(input.ContentFingerprint)
            || input.OpaqueExternalReference is not null && !IsSafeOpaqueReference(input.OpaqueExternalReference)
            || !ValidObservation(input.Observation))
        {
            return false;
        }

        identity = new EvidenceIdentity(input.LogicalIdentityDigest);
        error = null;
        return true;
    }

    private static bool ValidObservation(EvidenceObservation? observation)
    {
        if (observation is null) return true;
        if (observation is { AccountId: null, SignedAmountMinor: null, CurrencyCode: null, TransactionDate: null, PostingDate: null, InstrumentId: null, CardholderId: null, DescriptionFingerprint: null }) return false;
        if (observation.AccountId is not null && !LedgerId.TryParse(observation.AccountId, out _, out _)) return false;
        if (observation.InstrumentId is not null && !LedgerId.TryParse(observation.InstrumentId, out _, out _)) return false;
        if (observation.CardholderId is not null && !LedgerId.TryParse(observation.CardholderId, out _, out _)) return false;
        if (observation.SignedAmountMinor == 0) return false;
        if (observation.CurrencyCode is not null && !LedgerCurrency.TryParse(observation.CurrencyCode, out _, out _)) return false;
        if (observation.TransactionDate is not null && !EffectiveDate.TryParse(observation.TransactionDate, out _, out _)) return false;
        if (observation.PostingDate is not null && !EffectiveDate.TryParse(observation.PostingDate, out _, out _)) return false;
        return observation.DescriptionFingerprint is null || IsFingerprint(observation.DescriptionFingerprint);
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeOpaqueReference(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':'))) return false;
        if (ContainsLongDigitRun(value)) return false;
        var normalized = value.ToLowerInvariant();
        return !new[] { "password", "passwd", "secret", "bearer", "credential", "token" }.Any(normalized.Contains);
    }

    private static bool ContainsLongDigitRun(string value)
    {
        var run = 0;
        foreach (var character in value)
        {
            run = char.IsAsciiDigit(character) ? run + 1 : 0;
            if (run >= 9) return true;
        }

        return false;
    }
}
