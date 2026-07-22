using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Xunit;

namespace Tally.Tests.Ledger;

public sealed class ReconciliationPolicyMatrixTests
{
    [Fact]
    public void OQ_LEDGER_13_exact_unique_candidate_is_the_only_automatic_branch()
    {
        var source = Source([Transaction("target")]);
        var projection = Projection(exact: [Candidate("target")]);

        var decision = ReconciliationPolicyV1.Evaluate(source, projection);

        Assert.Equal(AutomaticReconciliationOutcome.ApplyExactMatch, decision.Outcome);
        Assert.Equal("target", decision.TargetTransactionId);
        Assert.Equal(ReconciliationPolicyV1.ExactUniqueCandidateReason, decision.Reason);
        Assert.Equal(ReconciliationPolicyV1.PolicyId, decision.PolicyId);
        Assert.Equal(ReconciliationPolicyV1.PolicyVersion, decision.PolicyVersion);
    }

    [Theory]
    [InlineData("account", "authoritative_fact_difference")]
    [InlineData("currency", "authoritative_fact_difference")]
    [InlineData("amount", "authoritative_fact_difference")]
    [InlineData("date", "effective_date_mismatch")]
    [InlineData("inactive", "already_reconciled_candidate")]
    [InlineData("reconciled", "already_reconciled_candidate")]
    [InlineData("evidence_conflict", "conflicting_confirmation")]
    [InlineData("multiple", "multiple_compatible_candidates")]
    [InlineData("guard", "guard_candidate_present")]
    [InlineData("guard_date", "effective_date_mismatch")]
    [InlineData("zero", "no_candidate")]
    [InlineData("unsupported_policy", "unsupported_or_stale_policy")]
    public void OQ_LEDGER_13_every_unapproved_matrix_row_requires_review(string scenario, string reason)
    {
        var transaction = Transaction("target") with
        {
            AccountId = scenario == "account" ? "other-account" : "account",
            SignedAmountMinor = scenario == "amount" ? -1200 : -1234,
            CurrencyCode = scenario == "currency" ? "USD" : "ZAR",
            EffectiveDate = scenario == "date" ? "2026-07-02" : "2026-07-01",
            IsActive = scenario != "inactive",
            HasCurrentReconciliationDecision = scenario == "reconciled"
        };
        var source = Source(
            scenario == "multiple" ? [transaction, Transaction("second")] : [transaction],
            evidenceConflict: scenario == "evidence_conflict");
        IReadOnlyList<ReconciliationProjectionCandidate> exact = scenario switch
        {
            "zero" or "guard_date" => [],
            "multiple" => [Candidate("second"), Candidate("target")],
            _ => new[] { Candidate("target") }
        };
        IReadOnlyList<ReconciliationProjectionCandidate> guards = scenario switch
        {
            "guard" => [Candidate("guard", ReconciliationCandidateKind.Guard, ReconciliationCandidateReason.SignedAmountDiffers)],
            "guard_date" => [Candidate("target", ReconciliationCandidateKind.Guard, ReconciliationCandidateReason.EffectiveDateDiffers)],
            _ => []
        };
        var projection = Projection(
            exact,
            guards,
            policyId: scenario == "unsupported_policy" ? "unsupported" : ManualReviewProjectionV1.PolicyId);

        var decision = ReconciliationPolicyV1.Evaluate(source, projection);

        Assert.Equal(AutomaticReconciliationOutcome.ReviewRequired, decision.Outcome);
        Assert.Null(decision.TargetTransactionId);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void OQ_LEDGER_13_candidate_order_cannot_change_the_review_outcome()
    {
        var source = Source([Transaction("a"), Transaction("b")]);
        var first = ReconciliationPolicyV1.Evaluate(source, Projection(exact: [Candidate("a"), Candidate("b")]));
        var second = ReconciliationPolicyV1.Evaluate(source, Projection(exact: [Candidate("b"), Candidate("a")]));

        Assert.Equal(first, second);
        Assert.Equal(ReconciliationPolicyV1.MultipleCompatibleCandidatesReason, first.Reason);
    }

    [Theory]
    [InlineData("account", "authoritative_fact_difference")]
    [InlineData("currency", "authoritative_fact_difference")]
    [InlineData("amount", "authoritative_fact_difference")]
    [InlineData("date", "effective_date_mismatch")]
    [InlineData("reconciled", "already_reconciled_candidate")]
    public void OQ_LEDGER_13_live_projection_preserves_the_stable_excluded_reason(string scenario, string reason)
    {
        var transaction = Transaction("target") with
        {
            AccountId = scenario == "account" ? "other-account" : "account",
            CurrencyCode = scenario == "currency" ? "USD" : "ZAR",
            SignedAmountMinor = scenario == "amount" ? -1200 : -1234,
            EffectiveDate = scenario == "date" ? "2026-07-02" : "2026-07-01",
            HasCurrentReconciliationDecision = scenario == "reconciled"
        };
        var source = Source([transaction]);

        var decision = ReconciliationPolicyV1.Evaluate(source, ManualReviewProjectionV1.Project(source));

        Assert.Equal(AutomaticReconciliationOutcome.ReviewRequired, decision.Outcome);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void OQ_LEDGER_13_unrelated_inactive_transaction_does_not_reclassify_zero_candidate()
    {
        var source = Source([Transaction("unrelated") with { SignedAmountMinor = -9999, IsActive = false }]);

        var decision = ReconciliationPolicyV1.Evaluate(source, ManualReviewProjectionV1.Project(source));

        Assert.Equal(AutomaticReconciliationOutcome.ReviewRequired, decision.Outcome);
        Assert.Equal(ReconciliationPolicyV1.NoCandidateReason, decision.Reason);
    }

    [Fact]
    public void OQ_LEDGER_13_automatic_correction_subset_is_empty() =>
        Assert.False(ReconciliationPolicyV1.SupportsAutomaticCorrection);

    private static ReconciliationProjectionSource Source(
        IReadOnlyList<ReconciliationProjectionTransaction> transactions,
        bool evidenceConflict = false) => new(
            new("evidence", Fingerprint(), "account", -1234, "ZAR", "2026-07-01", evidenceConflict, evidenceConflict),
            new("scope", "account", "2026-07-01", "2026-07-31"),
            transactions);

    private static ReconciliationProjectionResult Projection(
        IReadOnlyList<ReconciliationProjectionCandidate>? exact = null,
        IReadOnlyList<ReconciliationProjectionCandidate>? guards = null,
        string policyId = ManualReviewProjectionV1.PolicyId) => new(
            "evidence",
            Fingerprint(),
            "scope",
            policyId,
            ManualReviewProjectionV1.PolicyVersion,
            exact?.Count == 1 && (guards?.Count ?? 0) == 0
                ? ReconciliationProjectionOutcome.UniqueCandidate
                : exact?.Count > 1 || (exact?.Count > 0 && guards?.Count > 0)
                    ? ReconciliationProjectionOutcome.Ambiguous
                    : guards?.Count > 0
                        ? ReconciliationProjectionOutcome.GuardOnly
                        : ReconciliationProjectionOutcome.NoCandidate,
            exact ?? [],
            guards ?? [],
            [],
            [],
            Fingerprint(),
            true,
            false);

    private static ReconciliationProjectionCandidate Candidate(
        string id,
        ReconciliationCandidateKind kind = ReconciliationCandidateKind.Exact,
        params ReconciliationCandidateReason[] reasons) => new(
            id,
            kind,
            kind == ReconciliationCandidateKind.Exact
                ? new(true, true, true, true)
                : new(true, true, false, reasons.Contains(ReconciliationCandidateReason.EffectiveDateDiffers) ? false : true),
            reasons);

    private static ReconciliationProjectionTransaction Transaction(string id) => new(
        id,
        "account",
        -1234,
        "ZAR",
        "2026-07-01",
        true,
        false,
        false);

    private static string Fingerprint() => new('a', 64);
}
