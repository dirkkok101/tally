using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Domain.Ledger.Reconciliation;

public enum AutomaticReconciliationOutcome
{
    ApplyExactMatch,
    ReviewRequired
}

public sealed record AutomaticReconciliationDecision(
    AutomaticReconciliationOutcome Outcome,
    string? TargetTransactionId,
    string Reason,
    string PolicyId,
    string PolicyVersion,
    string MatchBasis);

public static class ReconciliationPolicyV1
{
    public const string PolicyId = "reconciliation-policy-v1";
    public const string PolicyVersion = "1.0";
    public const string ExactUniqueCandidateReason = "exact_unique_candidate";
    public const string AuthoritativeFactDifferenceReason = "authoritative_fact_difference";
    public const string EffectiveDateMismatchReason = "effective_date_mismatch";
    public const string MultipleCompatibleCandidatesReason = "multiple_compatible_candidates";
    public const string GuardCandidatePresentReason = "guard_candidate_present";
    public const string ConflictingConfirmationReason = "conflicting_confirmation";
    public const string AlreadyReconciledCandidateReason = "already_reconciled_candidate";
    public const string NoCandidateReason = "no_candidate";
    public const string UnsupportedOrStalePolicyReason = "unsupported_or_stale_policy";
    public const bool SupportsAutomaticCorrection = false;

    public static AutomaticReconciliationDecision Evaluate(
        ReconciliationProjectionSource source,
        ReconciliationProjectionResult projection)
    {
        if (!ManualReviewProjectionV1.Supports(projection.PolicyId, projection.PolicyVersion))
            return Review(UnsupportedOrStalePolicyReason);
        if (source.Evidence.HasActiveConfirmingLink
            || source.Evidence.HasCurrentDecision
            || projection.Conflicts.Count > 0)
        {
            return Review(ConflictingConfirmationReason);
        }

        var exactFactTransactions = source.Transactions
            .Where(transaction => HasExactAuthoritativeFacts(source.Evidence, transaction))
            .ToArray();
        if (exactFactTransactions.Any(transaction => !IsActiveUnreconciled(transaction)))
            return Review(AlreadyReconciledCandidateReason);

        if (projection.ExactCandidates.Count > 1)
            return Review(MultipleCompatibleCandidatesReason);
        if (projection.ExactCandidates.Count == 1 && projection.GuardCandidates.Count > 0)
            return Review(GuardCandidatePresentReason);
        if (projection.ExactCandidates.Count == 0 && projection.GuardCandidates.Count > 0)
        {
            return Review(projection.GuardCandidates.Any(candidate =>
                    candidate.Reasons.Contains(ReconciliationCandidateReason.EffectiveDateDiffers))
                ? EffectiveDateMismatchReason
                : AuthoritativeFactDifferenceReason);
        }

        if (projection.ExactCandidates.Count == 0)
        {
            return source.Transactions.Any(transaction =>
                    IsActiveUnreconciled(transaction)
                    && transaction.SignedAmountMinor == source.Evidence.SignedAmountMinor
                    && string.Equals(transaction.EffectiveDate, source.Evidence.TransactionDate, StringComparison.Ordinal)
                    && (!string.Equals(transaction.AccountId, source.Evidence.AccountId, StringComparison.Ordinal)
                        || !string.Equals(transaction.CurrencyCode, source.Evidence.CurrencyCode, StringComparison.Ordinal)
                        || !string.Equals(source.Evidence.CurrencyCode, "ZAR", StringComparison.Ordinal)))
                ? Review(AuthoritativeFactDifferenceReason)
                : Review(NoCandidateReason);
        }

        var candidate = projection.ExactCandidates[0];
        var transaction = source.Transactions.SingleOrDefault(item => item.TransactionId == candidate.TransactionId);
        if (transaction is null) return Review(AuthoritativeFactDifferenceReason);
        if (!transaction.IsActive
            || transaction.HasCurrentReconciliationDecision
            || transaction.HasActiveStatementConfirmation)
        {
            return Review(AlreadyReconciledCandidateReason);
        }

        if (!string.Equals(source.Evidence.TransactionDate, transaction.EffectiveDate, StringComparison.Ordinal))
            return Review(EffectiveDateMismatchReason);
        if (!candidate.Basis.AccountMatches
            || !candidate.Basis.CurrencyMatches
            || !candidate.Basis.SignedAmountMatches
            || !candidate.Basis.EffectiveDateMatches
            || !string.Equals(source.Evidence.AccountId, transaction.AccountId, StringComparison.Ordinal)
            || !string.Equals(source.Evidence.CurrencyCode, "ZAR", StringComparison.Ordinal)
            || !string.Equals(transaction.CurrencyCode, "ZAR", StringComparison.Ordinal)
            || source.Evidence.SignedAmountMinor != transaction.SignedAmountMinor)
        {
            return Review(AuthoritativeFactDifferenceReason);
        }

        return new(
            AutomaticReconciliationOutcome.ApplyExactMatch,
            candidate.TransactionId,
            ExactUniqueCandidateReason,
            PolicyId,
            PolicyVersion,
            "account=exact;currency=ZAR;signed_amount_minor=exact;effective_date=exact;tolerance_days=0;exact_candidates=1;guard_candidates=0");
    }

    private static bool HasExactAuthoritativeFacts(
        ReconciliationProjectionEvidence evidence,
        ReconciliationProjectionTransaction transaction) =>
        string.Equals(transaction.AccountId, evidence.AccountId, StringComparison.Ordinal)
        && string.Equals(transaction.CurrencyCode, "ZAR", StringComparison.Ordinal)
        && string.Equals(evidence.CurrencyCode, "ZAR", StringComparison.Ordinal)
        && transaction.SignedAmountMinor == evidence.SignedAmountMinor
        && string.Equals(transaction.EffectiveDate, evidence.TransactionDate, StringComparison.Ordinal);

    private static bool IsActiveUnreconciled(ReconciliationProjectionTransaction transaction) =>
        transaction.IsActive
        && !transaction.HasCurrentReconciliationDecision
        && !transaction.HasActiveStatementConfirmation;

    private static AutomaticReconciliationDecision Review(string reason) => new(
        AutomaticReconciliationOutcome.ReviewRequired,
        null,
        reason,
        PolicyId,
        PolicyVersion,
        "review:" + reason);
}
