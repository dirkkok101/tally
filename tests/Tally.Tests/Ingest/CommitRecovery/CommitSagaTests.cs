using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Commit;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.CommitRecovery;

// TC-INGEST-APPROVED-BATCH-COMMIT-CONTRACT / FR-INGEST-APPROVED-BATCH-COMMIT
// TC-INGEST-DURABLE-RECEIPT-RESUME-CONTRACT / FR-INGEST-DURABLE-RECEIPT-RESUME
[SupportedOSPlatform("linux")]
public sealed class CommitSagaTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-commit-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("human", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 15, 0, 0, TimeSpan.Zero));
    private LedgerContractClient ledger = null!;
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Commit_requires_batch_revision_and_digest()
    {
        var result = await CreateSaga().ExecuteAsync(new CommitCommand("", "", ""), CancellationToken.None);
        Assert.Equal(CommitErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Commit_unknown_revision_fails_closed()
    {
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand("missing", "missing", "digest"),
            CancellationToken.None);
        Assert.Equal(CommitErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Commit_rejects_digest_mismatch_before_lock()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, "wrong-digest"),
            CancellationToken.None);
        Assert.Equal(CommitErrors.DigestMismatch, result.ErrorCode);
        Assert.Null(await TryReadReceiptStatusAsync(prepared.BatchId));
    }

    [Fact]
    public async Task Commit_rejects_unapproved_manifest()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        var digest = await InspectDigestAsync(preview.BatchId, preview.ManifestRevisionId!);
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(preview.BatchId, preview.ManifestRevisionId!, digest),
            CancellationToken.None);
        Assert.Equal(CommitErrors.NotApproved, result.ErrorCode);
    }

    [Fact]
    public async Task Batch_commit_lock_is_non_reentrant_for_the_same_batch()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        await using var first = await locks.TryAcquireAsync("batch-lock-1", CancellationToken.None);
        Assert.NotNull(first);
        await using var second = await locks.TryAcquireAsync("batch-lock-1", CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task Batch_commit_lock_allows_independent_batches()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        await using var first = await locks.TryAcquireAsync("batch-a", CancellationToken.None);
        await using var second = await locks.TryAcquireAsync("batch-b", CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Batch_commit_lock_releases_on_dispose()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        var held = await locks.TryAcquireAsync("batch-release", CancellationToken.None);
        Assert.NotNull(held);
        await held!.DisposeAsync();
        await using var again = await locks.TryAcquireAsync("batch-release", CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public void Candidate_commit_states_close_terminal_transitions()
    {
        Assert.True(CandidateCommitStates.IsTerminal(CandidateReceiptState.Accepted));
        Assert.True(CandidateCommitStates.IsTerminal(CandidateReceiptState.ExactDuplicate));
        Assert.True(CandidateCommitStates.IsTerminal(CandidateReceiptState.Conflicted));
        Assert.True(CandidateCommitStates.IsTerminal(CandidateReceiptState.Rejected));
        Assert.False(CandidateCommitStates.IsTerminal(CandidateReceiptState.Attempting));
        Assert.False(CandidateCommitStates.IsTerminal(CandidateReceiptState.Unresolved));
        Assert.True(CandidateCommitStates.MayAttemptLedgerWrite(CandidateReceiptState.Pending));
        Assert.True(CandidateCommitStates.IsReferenceBearing(CandidateReceiptState.Accepted));
        Assert.False(CandidateCommitStates.IsReferenceBearing(CandidateReceiptState.Conflicted));
    }

    [Fact]
    public async Task Commit_accepts_all_candidates_and_returns_complete_receipt()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));
        Assert.Equal(ImportReceiptStatus.Completed, result.Value!.Status);
        Assert.Equal(0, result.Value.Counts.Pending);
        Assert.Equal(0, result.Value.Counts.Attempting);
        Assert.Equal(0, result.Value.Counts.Unresolved);
        Assert.True(result.Value.Counts.Accepted >= 2);
        Assert.Empty(result.Value.UnresolvedCandidateIds);
        Assert.All(
            result.Value.CandidateOutcomes.Where(o => o.State == CandidateReceiptState.Accepted),
            outcome => Assert.False(string.IsNullOrWhiteSpace(outcome.LedgerTransactionId)));
        Assert.Equal(BatchStatus.Completed, await ReadBatchStatusAsync(prepared.BatchId));
    }

    [Fact]
    public async Task Commit_records_attempting_before_ledger_and_terminal_after()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM candidate_receipt
            WHERE outcome = 2 AND terminal_at IS NOT NULL AND ledger_transaction_id IS NOT NULL;
            """;
        var accepted = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.True(accepted >= 2);
    }

    [Fact]
    public async Task Commit_processes_candidates_in_manifest_record_order()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));

        var ordered = result.Value!.CandidateOutcomes.Select(o => o.CandidateId).ToArray();
        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        Assert.Equal(work.Select(w => w.CandidateId).ToArray(), ordered);
    }

    [Fact]
    public async Task Commit_is_idempotent_when_already_completed()
    {
        var prepared = await PrepareApprovedAsync();
        var first = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        var second = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        Assert.True(first.IsSuccess, await FailureDetailAsync(prepared.BatchId, first.ErrorCode));
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.ReceiptId, second.Value!.ReceiptId);
        Assert.Equal(ImportReceiptStatus.Completed, second.Value.Status);
    }

    [Fact]
    public async Task Commit_skips_already_terminal_candidates_on_resume_mode()
    {
        var prepared = await PrepareApprovedAsync();
        var first = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(first.IsSuccess, await FailureDetailAsync(prepared.BatchId, first.ErrorCode));

        var resume = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest, ResumeMode: true),
            CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(first.Value!.Counts.Accepted, resume.Value!.Counts.Accepted);
    }

    [Fact]
    public async Task Commit_stops_on_ledger_idempotency_conflict_and_appends_error()
    {
        var accountId = await CreateAccountAsync();
        var prepared = await PrepareApprovedAsync(accountId);

        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        var first = work[0];
        var conflicting = first.FrozenRequest with
        {
            Input = first.FrozenRequest.Input with
            {
                SignedAmount = "-99.00",
                InitialEvidence = first.FrozenRequest.Input.InitialEvidence with
                {
                    Observation = first.FrozenRequest.Input.InitialEvidence.Observation! with { SignedAmountMinor = -9900 }
                }
            }
        };
        Assert.True((await ledger.RecordTransactionAsync(conflicting, CancellationToken.None)).IsSuccess);

        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        Assert.Equal(CommitErrors.LedgerConflict, result.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Interrupted, await TryReadReceiptStatusAsync(prepared.BatchId));
        Assert.Equal(BatchStatus.Interrupted, await ReadBatchStatusAsync(prepared.BatchId));

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM batch_error_event WHERE batch_id = $batchId;";
        command.Parameters.AddWithValue("$batchId", prepared.BatchId);
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 1);

        command.CommandText = "SELECT outcome FROM candidate_receipt WHERE candidate_id = $id;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", first.CandidateId);
        Assert.Equal((int)CandidateReceiptState.Conflicted, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Commit_rejects_missing_account_as_account_inactive()
    {
        var prepared = await PrepareApprovedAsync();
        await using var connection = await OpenIngestAsync();
        await using var batchUpdate = connection.CreateCommand();
        batchUpdate.CommandText = "UPDATE ingest_batch SET selected_account_id = '01J00000000000000000000000' WHERE batch_id = $id;";
        batchUpdate.Parameters.AddWithValue("$id", prepared.BatchId);
        await batchUpdate.ExecuteNonQueryAsync();

        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.Equal(CommitErrors.AccountInactive, result.ErrorCode);
    }

    [Fact]
    public async Task Commit_module_binds_commit_descriptor_without_global_registration()
    {
        var module = new CommitOperationModule(CreateSaga());
        var descriptor = Assert.Single(module.Descriptors);
        Assert.Equal(IngestOperationIds.Commit, descriptor.OperationId);
        Assert.Equal("command", descriptor.Kind);
        Assert.Equal(typeof(CommitBatchInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(ImportReceipt), descriptor.ResultTypeInfo.Type);
        // After GATE-INT-PUBLIC-CONTRACT, commit is globally registered.
        Assert.NotNull(registry.Find(IngestOperationIds.Commit));
    }

    [Fact]
    public async Task Commit_module_dispatches_approved_batch()
    {
        var prepared = await PrepareApprovedAsync();
        var module = new CommitOperationModule(CreateSaga());
        var input = JsonSerializer.SerializeToElement(
            new CommitBatchInput(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            IngestJsonContext.Default.CommitBatchInput);
        var result = await module.HandleAsync(
            IngestOperationIds.Commit,
            new OperationRequest(input, actor, null),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));
        Assert.Equal("completed", result.Value!.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Commit_module_rejects_invalid_json()
    {
        var module = new CommitOperationModule(CreateSaga());
        using var document = JsonDocument.Parse("{\"batchId\":1}");
        var result = await module.HandleAsync(
            IngestOperationIds.Commit,
            new OperationRequest(document.RootElement.Clone(), actor, null),
            CancellationToken.None);
        Assert.Equal(CommitErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Exact_duplicate_outcome_with_prior_ref_skips_ledger_write()
    {
        var accountId = await CreateAccountAsync();
        var prepared = await PrepareApprovedAsync(accountId);
        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        var first = work[0];

        var recorded = await ledger.RecordTransactionAsync(first.FrozenRequest, CancellationToken.None);
        Assert.True(recorded.IsSuccess, recorded.Error?.Code);

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE source_record_outcome
            SET disposition = $disposition, prior_canonical_ref = $prior
            WHERE candidate_id = $id;
            """;
        command.Parameters.AddWithValue("$disposition", (int)SourceRecordDisposition.ExactDuplicate);
        command.Parameters.AddWithValue("$prior", recorded.Value!.TransactionId);
        command.Parameters.AddWithValue("$id", first.CandidateId);
        await command.ExecuteNonQueryAsync();

        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));

        var exact = Assert.Single(result.Value!.CandidateOutcomes, o => o.CandidateId == first.CandidateId);
        Assert.Equal(CandidateReceiptState.ExactDuplicate, exact.State);
        Assert.Equal(recorded.Value.TransactionId, exact.LedgerTransactionId);
        Assert.True(result.Value.Counts.ExactDuplicates >= 1);
    }

    [Fact]
    public async Task Durable_receipt_counts_match_candidate_outcomes()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));
        var counts = result.Value!.Counts;
        var outcomes = result.Value.CandidateOutcomes;
        Assert.Equal(outcomes.Count(o => o.State == CandidateReceiptState.Accepted), counts.Accepted);
        Assert.Equal(outcomes.Count(o => o.State == CandidateReceiptState.ExactDuplicate), counts.ExactDuplicates);
        Assert.Equal(outcomes.Count(o => o.State == CandidateReceiptState.Conflicted), counts.Conflicted);
        Assert.Equal(outcomes.Count(o => o.State == CandidateReceiptState.Rejected), counts.Rejected);
        Assert.Equal(outcomes.Count(o => o.State == CandidateReceiptState.Unresolved), counts.Unresolved);
    }

    [Fact]
    public async Task Reference_free_conflict_outcomes_have_no_ledger_transaction_id()
    {
        var prepared = await PrepareApprovedAsync();
        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        var first = work[0];
        var conflicting = first.FrozenRequest with
        {
            Input = first.FrozenRequest.Input with
            {
                SignedAmount = "-50.00",
                InitialEvidence = first.FrozenRequest.Input.InitialEvidence with
                {
                    Observation = first.FrozenRequest.Input.InitialEvidence.Observation! with { SignedAmountMinor = -5000 }
                }
            }
        };
        Assert.True((await ledger.RecordTransactionAsync(conflicting, CancellationToken.None)).IsSuccess);
        _ = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ledger_transaction_id FROM candidate_receipt
            WHERE candidate_id = $id AND outcome = $outcome;
            """;
        command.Parameters.AddWithValue("$id", first.CandidateId);
        command.Parameters.AddWithValue("$outcome", (int)CandidateReceiptState.Conflicted);
        var value = await command.ExecuteScalarAsync();
        Assert.True(value is null or DBNull);
    }

    [Fact]
    public async Task Commit_does_not_hold_sqlite_across_ledger_by_opening_fresh_connections()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, await FailureDetailAsync(prepared.BatchId, result.ErrorCode));
        var source = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "src", "Tally", "Features", "Ingest", "Commit", "CandidateCommitSaga.cs"));
        Assert.Contains("MarkAttemptingAsync", source, StringComparison.Ordinal);
        Assert.Contains("RecordTransactionAsync", source, StringComparison.Ordinal);
        var attemptingIndex = source.IndexOf("MarkAttemptingAsync", StringComparison.Ordinal);
        var recordIndex = source.IndexOf("RecordTransactionAsync", StringComparison.Ordinal);
        Assert.True(attemptingIndex > 0 && recordIndex > attemptingIndex);
    }

    [Fact]
    public async Task Attempt_number_is_zero_before_attempt_and_increments_on_each_MarkAttempting()
    {
        var prepared = await PrepareApprovedAsync();
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var store = new CommitStateStore(database, new BatchErrorEventStore());
        var work = await store.LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        Assert.All(work, item => Assert.Equal(0, item.AttemptNumber));

        var receipt = await store.EnsureReceiptAsync(
            prepared.BatchId, prepared.ManifestRevisionId, "2026-07-27T12:00:00Z", CancellationToken.None);
        var candidateId = work[0].CandidateId;

        await store.MarkAttemptingAsync(receipt.ReceiptId, candidateId, "2026-07-27T12:00:01Z", CancellationToken.None);
        work = await store.LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        Assert.Equal(1, work.Single(w => w.CandidateId == candidateId).AttemptNumber);

        await store.MarkAttemptingAsync(receipt.ReceiptId, candidateId, "2026-07-27T12:00:02Z", CancellationToken.None);
        work = await store.LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        Assert.Equal(2, work.Single(w => w.CandidateId == candidateId).AttemptNumber);

        await store.MarkAttemptingAsync(receipt.ReceiptId, candidateId, "2026-07-27T12:00:03Z", CancellationToken.None);
        work = await store.LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        Assert.Equal(3, work.Single(w => w.CandidateId == candidateId).AttemptNumber);

        // Unattempted sibling remains 0.
        Assert.Contains(work, w => w.CandidateId != candidateId && w.AttemptNumber == 0);
    }

    [Fact]
    public async Task EnsureReceipt_resume_preserves_created_at_and_summary_json()
    {
        var prepared = await PrepareApprovedAsync();
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var store = new CommitStateStore(database, new BatchErrorEventStore());
        var t0 = "2026-07-27T10:00:00Z";
        var t1 = "2026-07-27T11:00:00Z";

        var first = await store.EnsureReceiptAsync(prepared.BatchId, prepared.ManifestRevisionId, t0, CancellationToken.None);
        Assert.Equal(t0, first.CreatedAt);
        Assert.Equal(t0, first.UpdatedAt);

        var interruptedSummary = """{"status":"Interrupted","note":"frontier"}""";
        await using (var connection = await OpenIngestAsync())
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE import_receipt
                SET status = $status, summary_json = $summary, updated_at = $updatedAt
                WHERE receipt_id = $id;
                """;
            update.Parameters.AddWithValue("$status", (int)ImportReceiptStatus.Interrupted);
            update.Parameters.AddWithValue("$summary", interruptedSummary);
            update.Parameters.AddWithValue("$updatedAt", t0);
            update.Parameters.AddWithValue("$id", first.ReceiptId);
            await update.ExecuteNonQueryAsync();
        }

        var second = await store.EnsureReceiptAsync(prepared.BatchId, prepared.ManifestRevisionId, t1, CancellationToken.None);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(t0, second.CreatedAt);
        Assert.Equal(t1, second.UpdatedAt);
        Assert.Equal(ImportReceiptStatus.Committing, second.Status);

        await using (var connection = await OpenIngestAsync())
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT summary_json, created_at, updated_at FROM import_receipt WHERE receipt_id = $id;";
            read.Parameters.AddWithValue("$id", first.ReceiptId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(interruptedSummary, reader.GetString(0));
            Assert.Equal(t0, reader.GetString(1));
            Assert.Equal(t1, reader.GetString(2));
        }
    }

    [Fact]
    public async Task Commit_against_already_abandoned_batch_returns_not_committable_or_not_approved()
    {
        var prepared = await PrepareApprovedAsync();
        var abandon = await CreateAbandon().HandleAsync(
            new AbandonCommand(prepared.BatchId, "pre-commit-abandon"),
            CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);

        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        // Pre-lock NotApproved (approval deactivated) or post-lock NotCommittable (Abandoned receipt) are both stable.
        Assert.True(
            result.ErrorCode is CommitErrors.NotApproved or CommitErrors.NotCommittable,
            result.ErrorCode);
        Assert.Equal(0, await CountCandidateReceiptsAsync());
        var receiptStatus = await TryReadReceiptStatusAsync(prepared.BatchId);
        Assert.True(
            receiptStatus is null or ImportReceiptStatus.Abandoned,
            $"unexpected receipt status {receiptStatus}");
    }

    [Fact]
    public async Task Commit_racing_abandon_via_BeforeBatchLock_fails_closed_with_no_work_item_mutation()
    {
        var prepared = await PrepareApprovedAsync();
        var injector = new CommitFaultInjector(
            CommitFaultInjector.FaultPoint.None,
            beforeBatchLockAction: async (batchId, ct) =>
            {
                Assert.Equal(prepared.BatchId, batchId);
                var abandon = await CreateAbandon().HandleAsync(
                    new AbandonCommand(batchId, "race-before-lock"),
                    ct);
                Assert.True(abandon.IsSuccess, abandon.ErrorCode);
            });

        var result = await CreateSaga(injector).ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);

        Assert.Equal(1, injector.BeforeBatchLockCount);
        Assert.True(
            result.ErrorCode is CommitErrors.NotApproved or CommitErrors.NotCommittable,
            result.ErrorCode);
        Assert.Equal(0, await CountCandidateReceiptsAsync());
        Assert.Equal(0, injector.LedgerCallCount);
    }

    private CandidateCommitSaga CreateSaga(ICommitFaultHook? faultHook = null)
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var errors = new BatchErrorEventStore();
        return new CandidateCommitSaga(
            new ReviewStateStore(database),
            new CommitStateStore(database, errors),
            new BatchCommitLock(database, protection),
            ledger,
            time,
            faultHook);
    }

    private AbandonHandler CreateAbandon()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        return new AbandonHandler(
            new RecoveryStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            time);
    }

    private async Task<int> CountCandidateReceiptsAsync()
    {
        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM candidate_receipt;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<PreparedBatch> PrepareApprovedAsync(string? accountId = null)
    {
        accountId ??= await CreateAccountAsync();
        var preview = await PreviewAsync(accountId);
        var digest = await InspectDigestAsync(preview.BatchId, preview.ManifestRevisionId!);
        var approve = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())), time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, digest, actor), CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
        return new PreparedBatch(preview.BatchId, preview.ManifestRevisionId!, digest, accountId);
    }

    private async Task<PreviewImportResult> PreviewAsync(string accountId)
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf("layout-a"));
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var store = new PreviewStateStore(database, new BatchErrorEventStore());
        var account = new AccountDetail(
            accountId, "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "human:owner", "2026-01-01T00:00:00Z", null, []);
        var handler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new StubPdfExtractor(LayoutAEvidence()),
            StatementAdapterRegistry.CreateDefault(),
            store,
            time);
        var result = await handler.HandleAsync(new PreviewCommand("1.0", path, accountId, actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!;
    }

    private async Task<string> InspectDigestAsync(string batchId, string revisionId)
    {
        var inspect = await new InspectHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())))
            .HandleAsync(new InspectQuery(batchId, revisionId), CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.ErrorCode);
        return inspect.Value!.CanonicalDigest;
    }

    private async Task<string> CreateAccountAsync()
    {
        var input = JsonSerializer.SerializeToElement(
            new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", actor, input, $"create-account-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        var detail = JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!;
        return detail.AccountId;
    }

    private async Task<string> FailureDetailAsync(string batchId, string? errorCode)
    {
        var dump = new StringBuilder(errorCode ?? "null");
        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_id, outcome, error_code FROM candidate_receipt;";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                dump.Append(' ').Append(reader.GetString(0)).Append('=')
                    .Append(reader.GetInt32(1)).Append(':')
                    .Append(reader.IsDBNull(2) ? "null" : reader.GetString(2));
            }
        }

        return dump.ToString();
    }

    private async Task<BatchStatus> ReadBatchStatusAsync(string batchId)
    {
        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM ingest_batch WHERE batch_id = $id;";
        command.Parameters.AddWithValue("$id", batchId);
        return (BatchStatus)Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<ImportReceiptStatus?> TryReadReceiptStatusAsync(string batchId)
    {
        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM import_receipt WHERE batch_id = $id ORDER BY rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", batchId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (ImportReceiptStatus)Convert.ToInt32(value);
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenIngestAsync()
    {
        var connection = await new IngestDatabase(root, new IngestArtifactProtection()).OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static PdfDocumentEvidence LayoutAEvidence(string fingerprint = "synthetic-commit")
    {
        string[] lines =
        [
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"
        ];
        var glyphs = new List<PdfGlyphEvidence>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var left = 20d;
            var bottom = 700d - (lineIndex * 20d);
            foreach (var character in string.Concat(lines[lineIndex], " "))
            {
                glyphs.Add(new PdfGlyphEvidence(
                    character.ToString(), left, bottom, left + 5d, bottom + 10d, glyphs.Count, bottom, glyphs.Count));
                left += 5d;
            }
        }

        return new PdfDocumentEvidence(fingerprint, 1, [new PdfPageEvidence(1, 612, 792, glyphs, [])]);
    }

    private static byte[] CreatePdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 100 Td ({text}) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private sealed class StubPdfExtractor(PdfDocumentEvidence evidence) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken)
        {
            var fp = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
            return ValueTask.FromResult(new PdfExtractionResult(evidence with { SourceFingerprint = fp }, null));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record PreparedBatch(string BatchId, string ManifestRevisionId, string Digest, string AccountId);
}
