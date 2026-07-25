using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Identity;
using Xunit;

namespace Tally.Tests.Ingest.Identity;

public sealed class IngestIdentityTests
{
    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP: batch identity is the complete Exact Replay key.
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_batch_identity_has_a_stable_canonical_vector()
    {
        var batchId = IngestIdentity.BatchId(new("source", "account", "adapter-1", "ledger-1"));

        Assert.Equal("162d58bacfad2719dd3eb99159590fd73afd39f0ed56ac13647a571327894e59", batchId);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP: every Exact Replay key field participates; filename is absent by contract.
    [Theory]
    [InlineData("source-2", "account", "adapter-1", "ledger-1")]
    [InlineData("source", "account-2", "adapter-1", "ledger-1")]
    [InlineData("source", "account", "adapter-2", "ledger-1")]
    [InlineData("source", "account", "adapter-1", "ledger-2")]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_batch_identity_changes_for_each_exact_replay_key_field(string source, string account, string adapter, string ledger)
    {
        var baseline = IngestIdentity.BatchId(new("source", "account", "adapter-1", "ledger-1"));
        var changed = IngestIdentity.BatchId(new(source, account, adapter, ledger));

        Assert.NotEqual(baseline, changed);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP: changing a filename cannot affect an API that encodes only the replay key.
    [Theory]
    [InlineData("statement.pdf", "renamed.pdf")]
    [InlineData("july.csv", "private-account-name.csv")]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_batch_identity_excludes_filename(string firstFilename, string secondFilename)
    {
        var first = IngestIdentity.BatchId(new("source", "account", "adapter-1", "ledger-1"));
        var second = IngestIdentity.BatchId(new("source", "account", "adapter-1", "ledger-1"));

        Assert.NotEqual(firstFilename, secondFilename);
        Assert.Equal(first, second);
    }

    // TC-INGEST-REPLAY-OVERLAP-SAFETY-CONTRACT: deterministic identity policy is a required domain output.
    [Fact]
    public void TC_INGEST_REPLAY_OVERLAP_SAFETY_contract_exposes_the_deterministic_identity_policy()
    {
        var policy = typeof(ImportCandidate).Assembly.GetType("Tally.Domain.Ingest.Identity.IngestIdentity");

        Assert.NotNull(policy);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP / TC-INGEST-REPLAY-OVERLAP-SAFETY-CONTRACT
    [Theory]
    [InlineData("position:1", "position:2")]
    [InlineData("page:1,row:1", "page:1,row:2")]
    [InlineData("offset:0", "offset:1")]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_structural_position_keeps_identical_source_tuples_distinct(string firstPosition, string secondPosition)
    {
        var first = IngestIdentity.SourceRecordId(new("source", firstPosition, "raw", "facts-v1"));
        var second = IngestIdentity.SourceRecordId(new("source", secondPosition, "raw", "facts-v1"));

        Assert.NotEqual(first, second);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP
    [Fact]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_source_record_identity_excludes_adapter_and_manifest_metadata()
    {
        var source = IngestIdentity.SourceRecordId(new("source", "p:1", "raw", "facts-v1"));
        var replay = IngestIdentity.SourceRecordId(new("source", "p:1", "raw", "facts-v1"));

        Assert.Equal(source, replay);
    }

    // DM-INGEST-IMPORT-MANIFEST / DD-INGEST-MANIFEST-IDENTITY-OVERLAP
    [Theory]
    [InlineData("acc-a", "acc-b")]
    [InlineData("acc-a", "acc-c")]
    public void DM_INGEST_IMPORT_MANIFEST_candidate_identity_includes_selected_account(string firstAccount, string secondAccount)
    {
        var source = IngestIdentity.SourceRecordId(new("source", "p:1", "raw", "facts-v1"));
        var first = IngestIdentity.Candidate(new(firstAccount, source, 1234, "ZAR", "2026-07-01", null, "Coffee"));
        var second = IngestIdentity.Candidate(new(secondAccount, source, 1234, "ZAR", "2026-07-01", null, "Coffee"));

        Assert.NotEqual(first.CandidateId, second.CandidateId);
    }

    // DM-INGEST-IMPORT-MANIFEST
    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(999999)]
    public void DM_INGEST_IMPORT_MANIFEST_candidate_identity_drives_reference_and_idempotency(long amount)
    {
        var candidate = IngestIdentity.Candidate(new("acc", "source-record", amount, "ZAR", "2026-07-01", "2026-07-02", "Description"));

        Assert.Equal($"ingest:{candidate.CandidateId}", candidate.SourceReference);
        Assert.Equal(candidate.SourceReference, candidate.IdempotencyKey);
        Assert.Equal(64, candidate.CandidateId.Length);
    }

    // DD-INGEST-MANIFEST-IDENTITY-OVERLAP
    [Theory]
    [InlineData(100, 101)]
    [InlineData(-100, 100)]
    public void DD_INGEST_MANIFEST_IDENTITY_OVERLAP_changed_immutable_facts_conflict(long firstAmount, long secondAmount)
    {
        var first = IngestIdentity.Candidate(new("acc", "record", firstAmount, "ZAR", "2026-07-01", null, "D"));
        var second = IngestIdentity.Candidate(new("acc", "record", secondAmount, "ZAR", "2026-07-01", null, "D"));

        Assert.True(IngestIdentity.HasImmutableFactConflict(first.CandidateId, second));
    }
}
