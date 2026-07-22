using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Transactions;

namespace Tally.Domain.Ledger.Reconciliation;

public static class ReconciliationDispositionPolicy
{
    public static bool IsBaseDisposition(ReconciliationApplyDisposition disposition) => disposition is
        ReconciliationApplyDisposition.MatchExisting or
        ReconciliationApplyDisposition.CreateStatementOnly or
        ReconciliationApplyDisposition.RecordAmbiguous or
        ReconciliationApplyDisposition.RecordException;

    public static bool TryNormalize(
        ReconciliationApplyInput input,
        out NormalizedReconciliationApply? normalized,
        out string? error)
    {
        normalized = null;
        error = ReconciliationApplyErrors.InvalidInput;
        if (!Enum.IsDefined(input.Disposition)
            || !Enum.IsDefined(input.AuthorityKind)
            || !LedgerId.TryParse(input.EvidenceId, out _, out _)
            || !LedgerId.TryParse(input.ScopeId, out _, out _)
            || !IsFingerprint(input.EvidenceFingerprint)
            || !IsFingerprint(input.ExpectedProjectionToken)
            || !TryText(input.Reason, 512, out var reason)
            || input.ReviewedCandidateIds is null
            || input.ReviewedCandidateIds.Count > 256)
        {
            return false;
        }

        if (input.AuthorityKind == ReconciliationAuthorityKind.DeterministicPolicy)
        {
            error = ReconciliationApplyErrors.UnsupportedAutomaticAuthority;
            return false;
        }

        if (input.Disposition == ReconciliationApplyDisposition.CorrectExistingFromStatement)
        {
            error = ReconciliationApplyErrors.UnsupportedStatementCorrection;
            return false;
        }

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in input.ReviewedCandidateIds)
        {
            if (!LedgerId.TryParse(candidate, out _, out _) || !candidates.Add(candidate)) return false;
        }

        if (input.TargetTransactionId is not null && !LedgerId.TryParse(input.TargetTransactionId, out _, out _)) return false;
        var valid = input.Disposition switch
        {
            ReconciliationApplyDisposition.MatchExisting => input.TargetTransactionId is not null
                && input.StatementFact is null && input.ExceptionCode is null,
            ReconciliationApplyDisposition.CreateStatementOnly => input.TargetTransactionId is null
                && input.StatementFact is not null && input.ExceptionCode is null,
            ReconciliationApplyDisposition.RecordAmbiguous => input.TargetTransactionId is null
                && input.StatementFact is null && input.ExceptionCode is null && candidates.Count > 0,
            ReconciliationApplyDisposition.RecordException => input.TargetTransactionId is null
                && input.StatementFact is null && TryCode(input.ExceptionCode, out _),
            _ => false
        };
        if (!valid) return false;

        AuthoritativeStatementFact? fact = null;
        if (input.StatementFact is not null)
        {
            if (!TryStatementFact(input.StatementFact, out fact)) return false;
        }

        normalized = new(
            input.EvidenceId,
            input.EvidenceFingerprint,
            input.ScopeId,
            input.ExpectedProjectionToken,
            input.Disposition,
            input.AuthorityKind,
            candidates.ToArray(),
            input.TargetTransactionId,
            fact,
            input.ExceptionCode?.Trim(),
            reason);
        error = null;
        return true;
    }

    public static string? ValidateProjection(NormalizedReconciliationApply input, ReconciliationProjectionResult projection)
    {
        if (!string.Equals(input.EvidenceFingerprint, projection.EvidenceFingerprint, StringComparison.Ordinal))
            return ReconciliationApplyErrors.EvidenceFingerprintChanged;
        if (!string.Equals(input.EvidenceId, projection.EvidenceId, StringComparison.Ordinal)
            || !string.Equals(input.ScopeId, projection.ScopeId, StringComparison.Ordinal)
            || !string.Equals(input.ExpectedProjectionToken, projection.AdvisoryToken, StringComparison.Ordinal)
            || !ManualReviewProjectionV1.Supports(projection.PolicyId, projection.PolicyVersion))
            return ReconciliationApplyErrors.ProjectionChanged;

        var currentCandidates = projection.ExactCandidates
            .Concat(projection.GuardCandidates)
            .Select(candidate => candidate.TransactionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!currentCandidates.SequenceEqual(input.ReviewedCandidateIds, StringComparer.Ordinal))
            return ReconciliationApplyErrors.CandidateSetChanged;

        return input.Disposition switch
        {
            ReconciliationApplyDisposition.MatchExisting when projection.Conflicts.Count > 0 => ReconciliationApplyErrors.ProjectionConflict,
            ReconciliationApplyDisposition.MatchExisting when !currentCandidates.Contains(input.TargetTransactionId!, StringComparer.Ordinal) => ReconciliationApplyErrors.TargetNotCandidate,
            ReconciliationApplyDisposition.CreateStatementOnly when currentCandidates.Length != 0 || projection.Conflicts.Count != 0
                => ReconciliationApplyErrors.DispositionIncompatible,
            ReconciliationApplyDisposition.RecordAmbiguous when projection.Conflicts.Count != 0
                || projection.Outcome is not (ReconciliationProjectionOutcome.Ambiguous or ReconciliationProjectionOutcome.GuardOnly)
                => ReconciliationApplyErrors.DispositionIncompatible,
            _ => null
        };
    }

    public static bool TryCreateStatementTransactionFact(
        AuthoritativeStatementFact input,
        EvidenceRecordDetail evidence,
        out TransactionFact? fact)
    {
        fact = null;
        if (evidence is not { Kind: EvidenceKind.StatementRow, Observation: not null }
            || !Money.TryParseTransactionAmount(input.SignedAmount, out var amount, out _)
            || !LedgerCurrency.TryParse(input.CurrencyCode, out var currency, out _)
            || !EffectiveDate.TryParse(input.TransactionDate, out var transactionDate, out _))
        {
            return false;
        }

        EffectiveDate? postingDate = null;
        if (input.PostingDate is not null)
        {
            if (!EffectiveDate.TryParse(input.PostingDate, out var parsedPostingDate, out _)) return false;
            postingDate = parsedPostingDate;
        }

        var observation = evidence.Observation;
        if (!string.Equals(input.AccountId, observation.AccountId, StringComparison.Ordinal)
            || amount.MinorUnits != observation.SignedAmountMinor
            || !string.Equals(currency.Code, observation.CurrencyCode, StringComparison.Ordinal)
            || !string.Equals(transactionDate.ToString(), observation.TransactionDate, StringComparison.Ordinal)
            || !string.Equals(postingDate?.ToString(), observation.PostingDate, StringComparison.Ordinal))
        {
            return false;
        }

        var registeredEvidence = new RegisterEvidenceInput(
            evidence.Kind,
            evidence.LogicalIdentityDigest,
            evidence.OpaqueExternalReference,
            evidence.ContentFingerprint,
            evidence.Observation);
        if (!EvidenceIdentity.TryCreate(registeredEvidence, out var identity, out _)) return false;
        fact = new(
            input.AccountId,
            amount,
            currency,
            transactionDate,
            postingDate,
            input.OriginalDescription,
            null,
            null,
            identity,
            registeredEvidence);
        return true;
    }

    private static bool TryStatementFact(AuthoritativeStatementFact input, out AuthoritativeStatementFact? normalized)
    {
        normalized = null;
        if (!LedgerId.TryParse(input.AccountId, out _, out _)
            || !Money.TryParseTransactionAmount(input.SignedAmount, out var amount, out _)
            || !LedgerCurrency.TryParse(input.CurrencyCode, out var currency, out _)
            || !EffectiveDate.TryParse(input.TransactionDate, out var transactionDate, out _)
            || !TryText(input.OriginalDescription, 512, out var description))
        {
            return false;
        }

        string? postingDate = null;
        if (input.PostingDate is not null)
        {
            if (!EffectiveDate.TryParse(input.PostingDate, out var parsed, out _)) return false;
            postingDate = parsed.ToString();
        }

        normalized = new(input.AccountId, amount.ToString(), currency.Code, transactionDate.ToString(), postingDate, description);
        return true;
    }

    private static bool TryText(string? value, int maximum, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximum && normalized.All(character => !char.IsControl(character));
    }

    private static bool TryCode(string? value, out string code)
    {
        code = value?.Trim() ?? string.Empty;
        return code.Length is > 0 and <= 64
            && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record NormalizedReconciliationApply(
    string EvidenceId,
    string EvidenceFingerprint,
    string ScopeId,
    string ExpectedProjectionToken,
    ReconciliationApplyDisposition Disposition,
    ReconciliationAuthorityKind AuthorityKind,
    IReadOnlyList<string> ReviewedCandidateIds,
    string? TargetTransactionId,
    AuthoritativeStatementFact? StatementFact,
    string? ExceptionCode,
    string Reason)
{
    public ReconciliationApplyInput CanonicalInput() => new(
        EvidenceId,
        EvidenceFingerprint,
        ScopeId,
        ExpectedProjectionToken,
        Disposition,
        AuthorityKind,
        ReviewedCandidateIds,
        TargetTransactionId,
        StatementFact,
        ExceptionCode,
        Reason);
}
