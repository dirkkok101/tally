using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;

namespace Tally.Domain.Ledger.Reconciliation;

public static class StatementAuthorityPolicy
{
    public static bool TryNormalize(
        ReconciliationApplyInput input,
        out NormalizedStatementCorrection? normalized,
        out string? error)
    {
        normalized = null;
        error = ReconciliationApplyErrors.InvalidInput;
        if (input.Disposition != ReconciliationApplyDisposition.CorrectExistingFromStatement
            || !Enum.IsDefined(input.AuthorityKind))
        {
            return false;
        }

        if (input.AuthorityKind == ReconciliationAuthorityKind.DeterministicPolicy)
        {
            error = ReconciliationPolicyV1.SupportsAutomaticCorrection
                ? ReconciliationApplyErrors.UnsupportedAutomaticAuthority
                : ReconciliationApplyErrors.ReviewRequired;
            return false;
        }

        if (!LedgerId.TryParse(input.EvidenceId, out _, out _)
            || !LedgerId.TryParse(input.ScopeId, out _, out _)
            || !IsFingerprint(input.EvidenceFingerprint)
            || !IsFingerprint(input.ExpectedProjectionToken)
            || !TryText(input.Reason, 512, out var reason)
            || input.ReviewedCandidateIds is null
            || input.ReviewedCandidateIds.Count is 0 or > 256
            || !LedgerId.TryParse(input.TargetTransactionId, out _, out _)
            || input.StatementFact is null
            || input.ExceptionCode is not null
            || !TryStatementFact(input.StatementFact, out var fact))
        {
            return false;
        }

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in input.ReviewedCandidateIds)
        {
            if (!LedgerId.TryParse(candidate, out _, out _) || !candidates.Add(candidate)) return false;
        }

        if (!candidates.Contains(input.TargetTransactionId!)) return false;
        normalized = new(
            input.EvidenceId,
            input.EvidenceFingerprint,
            input.ScopeId,
            input.ExpectedProjectionToken,
            input.AuthorityKind,
            candidates.ToArray(),
            input.TargetTransactionId!,
            fact!,
            reason);
        error = null;
        return true;
    }

    public static string? ValidateProjection(
        NormalizedStatementCorrection input,
        ReconciliationProjectionResult projection)
    {
        if (!string.Equals(input.EvidenceFingerprint, projection.EvidenceFingerprint, StringComparison.Ordinal))
            return ReconciliationApplyErrors.EvidenceFingerprintChanged;
        if (!string.Equals(input.EvidenceId, projection.EvidenceId, StringComparison.Ordinal)
            || !string.Equals(input.ScopeId, projection.ScopeId, StringComparison.Ordinal)
            || !string.Equals(input.ExpectedProjectionToken, projection.AdvisoryToken, StringComparison.Ordinal)
            || !ManualReviewProjectionV1.Supports(projection.PolicyId, projection.PolicyVersion))
        {
            return ReconciliationApplyErrors.ProjectionChanged;
        }

        var currentCandidates = projection.ExactCandidates
            .Concat(projection.GuardCandidates)
            .Select(candidate => candidate.TransactionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!currentCandidates.SequenceEqual(input.ReviewedCandidateIds, StringComparer.Ordinal))
            return ReconciliationApplyErrors.CandidateSetChanged;
        if (projection.Conflicts.Count > 0) return ReconciliationApplyErrors.ProjectionConflict;
        return currentCandidates.Contains(input.TargetTransactionId, StringComparer.Ordinal)
            ? null
            : ReconciliationApplyErrors.TargetNotCandidate;
    }

    public static bool TryCreateStatementTransactionFact(
        AuthoritativeStatementFact input,
        EvidenceRecordDetail evidence,
        out TransactionFact? fact) =>
        ReconciliationDispositionPolicy.TryCreateStatementTransactionFact(input, evidence, out fact);

    public static string MatchBasis(
        NormalizedStatementCorrection input,
        ReconciliationProjectionResult projection) =>
        $"policy={projection.PolicyId}:{projection.PolicyVersion};token={projection.AdvisoryToken};candidates={string.Join(',', input.ReviewedCandidateIds)};target={input.TargetTransactionId};correction=statement_authoritative";

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

        normalized = new(
            input.AccountId,
            amount.ToString(),
            currency.Code,
            transactionDate.ToString(),
            postingDate,
            description);
        return true;
    }

    private static bool TryText(string? value, int maximum, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximum
            && normalized.All(character => !char.IsControl(character));
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record NormalizedStatementCorrection(
    string EvidenceId,
    string EvidenceFingerprint,
    string ScopeId,
    string ExpectedProjectionToken,
    ReconciliationAuthorityKind AuthorityKind,
    IReadOnlyList<string> ReviewedCandidateIds,
    string TargetTransactionId,
    AuthoritativeStatementFact StatementFact,
    string Reason)
{
    public ReconciliationApplyInput CanonicalInput() => new(
        EvidenceId,
        EvidenceFingerprint,
        ScopeId,
        ExpectedProjectionToken,
        ReconciliationApplyDisposition.CorrectExistingFromStatement,
        AuthorityKind,
        ReviewedCandidateIds,
        TargetTransactionId,
        StatementFact,
        null,
        Reason);
}
