using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Domain.Ledger.Reconciliation;

public sealed class ManualReviewProjectionV1
{
    public const string PolicyId = "manual_review_projection";
    public const string PolicyVersion = "1.0";

    public static bool Supports(string policyId, string policyVersion) =>
        string.Equals(policyId, PolicyId, StringComparison.Ordinal)
        && string.Equals(policyVersion, PolicyVersion, StringComparison.Ordinal);

    public static ReconciliationProjectionResult Project(ReconciliationProjectionSource source)
    {
        var exclusions = new Dictionary<ReconciliationExclusionReason, int>();
        var exact = new List<ReconciliationProjectionCandidate>();
        var guards = new List<ReconciliationProjectionCandidate>();

        foreach (var transaction in source.Transactions)
        {
            if (!string.Equals(transaction.AccountId, source.Scope.AccountId, StringComparison.Ordinal))
            {
                Increment(exclusions, ReconciliationExclusionReason.WrongAccount);
                continue;
            }

            if (string.CompareOrdinal(transaction.EffectiveDate, source.Scope.PeriodStart) < 0
                || string.CompareOrdinal(transaction.EffectiveDate, source.Scope.PeriodEnd) > 0)
            {
                Increment(exclusions, ReconciliationExclusionReason.OutsideStatementScope);
                continue;
            }

            if (!transaction.IsActive)
            {
                Increment(exclusions, ReconciliationExclusionReason.InactiveTransaction);
                continue;
            }

            if (transaction.HasActiveStatementConfirmation)
            {
                Increment(exclusions, ReconciliationExclusionReason.ActiveStatementConfirmation);
                continue;
            }

            if (transaction.HasCurrentReconciliationDecision)
            {
                Increment(exclusions, ReconciliationExclusionReason.AlreadyReconciled);
                continue;
            }

            if (!string.Equals(transaction.CurrencyCode, source.Evidence.CurrencyCode, StringComparison.Ordinal))
            {
                Increment(exclusions, ReconciliationExclusionReason.CurrencyConflict);
                continue;
            }

            var amountMatches = transaction.SignedAmountMinor == source.Evidence.SignedAmountMinor;
            var dateMatches = string.Equals(transaction.EffectiveDate, source.Evidence.TransactionDate, StringComparison.Ordinal);
            var basis = new ReconciliationComparisonBasis(true, true, amountMatches, dateMatches);
            if (amountMatches && dateMatches)
            {
                exact.Add(new(transaction.TransactionId, ReconciliationCandidateKind.Exact, basis, [ReconciliationCandidateReason.ExactCompatible]));
            }
            else
            {
                var reasons = new List<ReconciliationCandidateReason>(2);
                if (!amountMatches) reasons.Add(ReconciliationCandidateReason.SignedAmountDiffers);
                if (!dateMatches) reasons.Add(ReconciliationCandidateReason.EffectiveDateDiffers);
                guards.Add(new(transaction.TransactionId, ReconciliationCandidateKind.Guard, basis, reasons.ToArray()));
            }
        }

        exact.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.TransactionId, right.TransactionId));
        guards.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.TransactionId, right.TransactionId));
        var conflicts = Conflicts(source);
        if (conflicts.Count > 0)
        {
            exact.Clear();
            guards.Clear();
        }

        var exactCandidates = exact.ToArray();
        var guardCandidates = guards.ToArray();
        var outcome = Outcome(exactCandidates.Length, guardCandidates.Length, conflicts.Count);
        var orderedExclusions = exclusions
            .OrderBy(item => item.Key)
            .Select(item => new ReconciliationProjectionExclusion(item.Key, item.Value))
            .ToArray();
        var token = Token(source, exactCandidates, guardCandidates, orderedExclusions, conflicts);
        return new(
            source.Evidence.EvidenceId,
            source.Evidence.Fingerprint,
            source.Scope.ScopeId,
            PolicyId,
            PolicyVersion,
            outcome,
            exactCandidates,
            guardCandidates,
            orderedExclusions,
            conflicts,
            token,
            AdvisoryOnly: true,
            GrantsAutomaticAuthority: false);
    }

    private static IReadOnlyList<ReconciliationProjectionConflict> Conflicts(ReconciliationProjectionSource source)
    {
        var conflicts = new List<ReconciliationProjectionConflict>(2);
        if (source.Evidence.HasActiveConfirmingLink)
        {
            conflicts.Add(new(ReconciliationProjectionConflictReason.EvidenceAlreadyConfirmed));
        }

        if (source.Evidence.HasCurrentDecision)
        {
            conflicts.Add(new(ReconciliationProjectionConflictReason.EvidenceHasCurrentDecision));
        }

        return conflicts.ToArray();
    }

    private static ReconciliationProjectionOutcome Outcome(int exactCount, int guardCount, int conflictCount)
    {
        if (conflictCount > 0) return ReconciliationProjectionOutcome.Conflict;
        if (exactCount > 1 || exactCount > 0 && guardCount > 0) return ReconciliationProjectionOutcome.Ambiguous;
        if (exactCount == 1) return ReconciliationProjectionOutcome.UniqueCandidate;
        return guardCount > 0 ? ReconciliationProjectionOutcome.GuardOnly : ReconciliationProjectionOutcome.NoCandidate;
    }

    private static string Token(
        ReconciliationProjectionSource source,
        IEnumerable<ReconciliationProjectionCandidate> exact,
        IEnumerable<ReconciliationProjectionCandidate> guards,
        IEnumerable<ReconciliationProjectionExclusion> exclusions,
        IEnumerable<ReconciliationProjectionConflict> conflicts)
    {
        var parts = new List<string>
        {
            PolicyId,
            PolicyVersion,
            source.Evidence.EvidenceId,
            source.Evidence.Fingerprint,
            source.Scope.ScopeId,
            source.Scope.AccountId,
            source.Scope.PeriodStart,
            source.Scope.PeriodEnd
        };
        parts.AddRange(exact.Select(CandidateToken));
        parts.AddRange(guards.Select(CandidateToken));
        parts.AddRange(exclusions.Select(item => $"excluded:{item.Reason}:{item.Count}"));
        parts.AddRange(conflicts.Select(item => $"conflict:{item.Reason}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant();
    }

    private static string CandidateToken(ReconciliationProjectionCandidate candidate) =>
        $"{candidate.Kind}:{candidate.TransactionId}:{candidate.Basis.AccountMatches}:{candidate.Basis.CurrencyMatches}:{candidate.Basis.SignedAmountMatches}:{candidate.Basis.EffectiveDateMatches}:{string.Join(',', candidate.Reasons)}";

    private static void Increment(IDictionary<ReconciliationExclusionReason, int> counts, ReconciliationExclusionReason reason) =>
        counts[reason] = counts.TryGetValue(reason, out var count) ? count + 1 : 1;
}

public sealed record ReconciliationProjectionSource(
    ReconciliationProjectionEvidence Evidence,
    ReconciliationProjectionScope Scope,
    IReadOnlyList<ReconciliationProjectionTransaction> Transactions);

public sealed record ReconciliationProjectionEvidence(
    string EvidenceId,
    string Fingerprint,
    string AccountId,
    long SignedAmountMinor,
    string CurrencyCode,
    string TransactionDate,
    bool HasActiveConfirmingLink,
    bool HasCurrentDecision);

public sealed record ReconciliationProjectionScope(
    string ScopeId,
    string AccountId,
    string PeriodStart,
    string PeriodEnd);

public sealed record ReconciliationProjectionTransaction(
    string TransactionId,
    string AccountId,
    long SignedAmountMinor,
    string CurrencyCode,
    string EffectiveDate,
    bool IsActive,
    bool HasCurrentReconciliationDecision,
    bool HasActiveStatementConfirmation);
