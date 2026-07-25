using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Manifests;
using Tally.Domain.Ingest.Overlap;
using Tally.Domain.Ingest.Reconciliation;
using Xunit;

namespace Tally.Tests.Ingest.Preview;

public sealed class ReconciliationAndManifestTests
{
    // TC-INGEST-SOURCE-RECONCILIATION-CONTRACT / FR-INGEST-SOURCE-RECONCILIATION
    [Fact]
    public void FR_INGEST_SOURCE_RECONCILIATION_accounts_each_record_and_controls_in_minor_units()
    {
        var result = StatementReconciler.Reconcile(1000, 900, [new("a", -50, 950, -50), new("b", -50, 900, -50)]);

        Assert.True(result.FullyReconciled);
        Assert.All(result.Controls, control => Assert.Equal(ReconciliationControlState.Satisfied, control.State));
    }

    // FR-INGEST-SOURCE-RECONCILIATION
    [Theory]
    [InlineData(901)]
    [InlineData(899)]
    public void FR_INGEST_SOURCE_RECONCILIATION_opening_plus_movements_must_equal_closing(long closing)
    {
        var result = StatementReconciler.Reconcile(1000, closing, [new("a", -100, null, null)]);

        Assert.False(result.FullyReconciled);
        Assert.Contains(result.Controls, control => control.Name == "opening_to_closing" && control.State == ReconciliationControlState.Mismatched);
    }

    // FR-INGEST-SOURCE-RECONCILIATION
    [Fact]
    public void FR_INGEST_SOURCE_RECONCILIATION_missing_controls_remain_unavailable()
    {
        var result = StatementReconciler.Reconcile(null, null, [new("a", 100, null, null)]);

        Assert.True(result.FullyReconciled);
        Assert.Contains(result.Controls, control => control.Name == "opening_to_closing" && control.State == ReconciliationControlState.Unavailable);
        Assert.Contains(result.Controls, control => control.Name == "running:a" && control.State == ReconciliationControlState.Unavailable);
        Assert.Contains(result.Controls, control => control.Name == "source:a" && control.State == ReconciliationControlState.Unavailable);
    }

    // FR-INGEST-SOURCE-RECONCILIATION: a first running balance can anchor later exact transitions when opening is unavailable.
    [Fact]
    public void FR_INGEST_SOURCE_RECONCILIATION_consecutive_running_controls_reconcile_from_the_first_available_anchor()
    {
        var result = StatementReconciler.Reconcile(null, null, [new("a", -100, 900, null), new("b", -50, 850, null)]);

        Assert.True(result.FullyReconciled);
        Assert.Contains(result.Controls, control => control.Name == "running:a" && control.State == ReconciliationControlState.Unavailable);
        Assert.Contains(result.Controls, control => control.Name == "running:b" && control.State == ReconciliationControlState.Satisfied);
    }

    // FR-INGEST-SOURCE-RECONCILIATION
    [Theory]
    [InlineData("a", "a")]
    [InlineData("", "b")]
    public void FR_INGEST_SOURCE_RECONCILIATION_every_record_is_accounted_for_exactly_once(string first, string second)
    {
        var result = StatementReconciler.Reconcile(0, 2, [new(first, 1, null, null), new(second, 1, null, null)]);

        Assert.False(result.FullyReconciled);
        Assert.Contains(result.Controls, control => control.Name == "record_accounting" && control.State == ReconciliationControlState.Mismatched);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_canonical_manifest_is_stable_and_versioned()
    {
        var input = ManifestInput();
        var first = ManifestCanonicalizer.Canonicalize(input);
        var second = ManifestCanonicalizer.Canonicalize(input);

        Assert.Equal(first, second);
        Assert.Contains("manifest-canonical-v1", first.CanonicalJson);
        Assert.Equal(64, first.CanonicalDigest.Length);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP: the manifest is the source-generated canonical UTF-8 representation.
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_canonical_manifest_has_an_exact_vector()
    {
        var manifest = ManifestCanonicalizer.Canonicalize(ManifestInput());

        Assert.Equal("{\"schema\":\"manifest-canonical-v1\",\"sourceFingerprint\":\"source\",\"selectedAccountId\":\"account\",\"adapterVersion\":\"adapter-1\",\"ledgerContractVersion\":\"ledger-1\",\"manifestSchemaVersion\":\"schema-1\",\"statementPeriod\":{\"startDate\":\"2026-07-01\",\"endDate\":\"2026-07-31\"},\"recordOutcomes\":[{\"sourceRecordId\":\"record-a\",\"order\":0,\"disposition\":\"accepted_candidate\",\"reasonCode\":\"accepted\",\"candidateId\":\"candidate-a\",\"priorCanonicalRef\":null}],\"candidates\":[],\"controls\":[{\"name\":\"opening_to_closing\",\"satisfied\":true,\"detail\":null}]}", manifest.CanonicalJson);
        Assert.Equal(manifest.CanonicalDigest, manifest.ManifestRevisionId);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP / DM-INGEST-IMPORT-MANIFEST: approval binds dispositions and reasons, not only record IDs.
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_manifest_digest_binds_record_outcome_meaning()
    {
        var accepted = ManifestCanonicalizer.Canonicalize(ManifestInput());
        var blocked = ManifestCanonicalizer.Canonicalize(ManifestInput() with
        {
            OrderedRecordOutcomes = [new("record-a", 0, SourceRecordDisposition.Blocked, "amount_ambiguous", null, null)]
        });

        Assert.NotEqual(accepted.CanonicalDigest, blocked.CanonicalDigest);
        Assert.NotEqual(accepted.ManifestRevisionId, blocked.ManifestRevisionId);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP / DM-INGEST-IMPORT-MANIFEST: immutable candidate facts are part of the approved manifest.
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_manifest_digest_binds_candidate_immutable_facts()
    {
        var first = ManifestCanonicalizer.Canonicalize(ManifestInput() with { OrderedCandidates = [Candidate(100)] });
        var changed = ManifestCanonicalizer.Canonicalize(ManifestInput() with { OrderedCandidates = [Candidate(101)] });

        Assert.NotEqual(first.CanonicalDigest, changed.CanonicalDigest);
        Assert.NotEqual(first.ManifestRevisionId, changed.ManifestRevisionId);
    }

    // TC-INGEST-REPLAY-OVERLAP-SAFETY-CONTRACT
    [Theory]
    [InlineData("source", "account", "adapter-1", "ledger-1", OverlapDecision.ExactReplay)]
    [InlineData("source", "other-account", "adapter-1", "ledger-1", OverlapDecision.NewPreview)]
    [InlineData("source", "account", "adapter-2", "ledger-1", OverlapDecision.NewPreview)]
    [InlineData("source", "account", "adapter-1", "ledger-2", OverlapDecision.NewPreview)]
    public void TC_INGEST_REPLAY_OVERLAP_SAFETY_complete_exact_replay_key_controls_replay(string source, string account, string adapter, string ledger, OverlapDecision expected)
    {
        var prior = new PreviewWindow(new("source", "account", "adapter-1", "ledger-1"), "revision", new(2026, 7, 1), new(2026, 7, 31));
        var result = OverlapPolicy.Evaluate(new(source, account, adapter, ledger), new(2026, 8, 1), new(2026, 8, 31), [prior]);

        Assert.Equal(expected, result.Decision);
    }

    // FR-INGEST-REPLAY-OVERLAP-SAFETY
    [Fact]
    public void FR_INGEST_REPLAY_OVERLAP_SAFETY_different_source_with_overlapping_window_blocks()
    {
        var prior = new PreviewWindow(new("source-a", "account", "adapter", "ledger"), "revision", new(2026, 7, 1), new(2026, 7, 31));
        var result = OverlapPolicy.Evaluate(new("source-b", "account", "adapter", "ledger"), new(2026, 7, 15), new(2026, 8, 15), [prior]);

        Assert.Equal(OverlapDecision.BlockedOverlap, result.Decision);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP: overlap safety is scoped to the selected account.
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_different_account_overlap_starts_a_new_preview()
    {
        var prior = new PreviewWindow(new("source-a", "account-a", "adapter", "ledger"), "revision", new(2026, 7, 1), new(2026, 7, 31));
        var result = OverlapPolicy.Evaluate(new("source-b", "account-b", "adapter", "ledger"), new(2026, 7, 15), new(2026, 8, 15), [prior]);

        Assert.Equal(OverlapDecision.NewPreview, result.Decision);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP
    [Theory]
    [InlineData("same", "same", OverlapDecision.ExactReplay)]
    [InlineData("first", "changed", OverlapDecision.Conflict)]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_changed_immutable_facts_are_conflicts(string existing, string current, OverlapDecision expected) =>
        Assert.Equal(expected, OverlapPolicy.EvaluateImmutableFacts(existing, current));

    private static CanonicalManifestInput ManifestInput() => new(
        "source",
        "account",
        "adapter-1",
        "ledger-1",
        "schema-1",
        new("2026-07-01", "2026-07-31"),
        [new("record-a", 0, SourceRecordDisposition.AcceptedCandidate, "accepted", "candidate-a", null)],
        [],
        [new("opening_to_closing", true, null)]);

    private static ImportCandidate Candidate(long amount) => new(
        "candidate-a",
        "record-a",
        "account",
        amount,
        "ZAR",
        "2026-07-01",
        null,
        "Description",
        "ingest:candidate-a",
        new(ImportProvenanceKind.StatementImport, "ingest:candidate-a"),
        "ingest:candidate-a",
        new(
            "ledger-1",
            "ledger.transaction.record",
            "ingest:candidate-a",
            new SafeActor("owner", "owner"),
            new("account", (amount / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "ZAR", "2026-07-01", null, "Description", "ingest:candidate-a", new(ImportProvenanceKind.StatementImport, "ingest:candidate-a"))),
        null);
}
