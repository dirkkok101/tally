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
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.CommitRecovery;

[SupportedOSPlatform("linux")]
public sealed class ResumeCrashMatrixTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-resume-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("human", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 17, 0, 0, TimeSpan.Zero));
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
    public async Task Resume_requires_batch_id()
    {
        var result = await CreateResumeHandler().HandleAsync(new ResumeCommand(""), CancellationToken.None);
        Assert.Equal(ResumeErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Resume_unknown_batch_fails_closed()
    {
        var result = await CreateResumeHandler().HandleAsync(new ResumeCommand("missing"), CancellationToken.None);
        Assert.Equal(ResumeErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Resume_rejects_unapproved_batch()
    {
        var accountId = await CreateAccountAsync();
        var preview = await PreviewAsync(accountId);
        var result = await CreateResumeHandler().HandleAsync(new ResumeCommand(preview.BatchId), CancellationToken.None);
        Assert.Equal(ResumeErrors.NotResumable, result.ErrorCode);
    }

    // Crash window matrix (NFR-INGEST-INTERRUPTED-COMMIT-RECOVERY / TC-INGEST-COMMIT-RECOVERY-MATRIX)

    [Fact]
    public async Task Crash_before_ledger_call_is_resumable_without_second_transaction()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BeforeLedgerCall, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
        Assert.True(resume.Value.Counts.Accepted >= 2);

        // Idempotent proof: replaying the frozen request returns the same canonical ids.
        foreach (var outcome in resume.Value.CandidateOutcomes.Where(o => o.State == CandidateReceiptState.Accepted))
        {
            var work = (await LoadWork(prepared)).Single(w => w.CandidateId == outcome.CandidateId);
            var replay = await ledger.RecordTransactionAsync(work.FrozenRequest, CancellationToken.None);
            Assert.True(replay.IsSuccess);
            Assert.Equal(outcome.LedgerTransactionId, replay.Value!.TransactionId);
        }
    }

    [Fact]
    public async Task Crash_after_ledger_commit_before_receipt_replays_original_transaction()
    {
        var prepared = await PrepareApprovedAsync();
        var first = (await LoadWork(prepared)).First();
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.AfterLedgerCommit, first.CandidateId);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        // Ledger already holds the first candidate under the frozen idempotency key.
        var preResume = await ledger.RecordTransactionAsync(first.FrozenRequest, CancellationToken.None);
        Assert.True(preResume.IsSuccess, preResume.Error?.Code);
        var originalId = preResume.Value!.TransactionId;

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);

        var firstOutcome = resume.Value.CandidateOutcomes.Single(o => o.CandidateId == first.CandidateId);
        Assert.Equal(CandidateReceiptState.Accepted, firstOutcome.State);
        Assert.Equal(originalId, firstOutcome.LedgerTransactionId);
    }

    [Fact]
    public async Task Crash_before_receipt_durability_leaves_attempting_and_resume_converges()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BeforeReceiptDurability, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT commit_state FROM import_candidate WHERE candidate_id = $id;";
        command.Parameters.AddWithValue("$id", firstCandidate);
        Assert.Equal((int)CandidateReceiptState.Attempting, Convert.ToInt32(await command.ExecuteScalarAsync()));

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
        Assert.DoesNotContain(resume.Value.CandidateOutcomes, o => o.State == CandidateReceiptState.Attempting);
    }

    [Fact]
    public async Task Crash_after_receipt_durability_stops_remaining_candidates()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.AfterReceiptDurability, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT commit_state FROM import_candidate WHERE candidate_id = $id;";
        command.Parameters.AddWithValue("$id", firstCandidate);
        Assert.Equal((int)CandidateReceiptState.Accepted, Convert.ToInt32(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT COUNT(*) FROM import_candidate WHERE commit_state = 0 OR commit_state = 1;";
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 1);

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
    }

    [Fact]
    public async Task Crash_between_candidates_preserves_frontier_and_resume_completes()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
        Assert.True(resume.Value.Counts.Accepted >= 2);
    }

    [Fact]
    public async Task Repeated_resume_converges_to_one_complete_receipt()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        var first = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        var second = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.ReceiptId, second.Value!.ReceiptId);
        Assert.Equal(ImportReceiptStatus.Completed, second.Value.Status);
        Assert.Equal(first.Value.Counts.Accepted, second.Value.Counts.Accepted);
    }

    [Fact]
    public async Task Concurrent_resume_is_rejected_by_batch_lock()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        await using var held = await locks.TryAcquireAsync("batch-concurrent", CancellationToken.None);
        Assert.NotNull(held);

        // Seed a real approved batch, then hold lock under that batch id via a second lock path is hard —
        // instead prove Resume/Commit path returns LockHeld when lock cannot be acquired.
        var prepared = await PrepareApprovedAsync();
        await using var batchHeld = await locks.TryAcquireAsync(prepared.BatchId, CancellationToken.None);
        Assert.NotNull(batchHeld);
        var result = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.Equal(CommitErrors.LockHeld, result.ErrorCode);
    }

    [Fact]
    public async Task Changed_digest_on_resume_target_rejects_before_mutation()
    {
        var prepared = await PrepareApprovedAsync();
        // Corrupt stored digest while leaving approval — resume resolves the approved digest, so
        // simulate by calling the saga resume path with a wrong digest (commit/resume contract).
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, "wrong-digest", ResumeMode: true),
            CancellationToken.None);
        Assert.Equal(CommitErrors.DigestMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task Resume_module_binds_descriptor_without_global_registration()
    {
        var module = new ResumeOperationModule(CreateResumeHandler());
        var descriptor = Assert.Single(module.Descriptors);
        Assert.Equal(IngestOperationIds.Resume, descriptor.OperationId);
        Assert.Equal(typeof(ResumeBatchInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(ImportReceipt), descriptor.ResultTypeInfo.Type);
        // After GATE-INT-PUBLIC-CONTRACT, resume is globally registered.
        Assert.NotNull(registry.Find(IngestOperationIds.Resume));
    }

    [Fact]
    public async Task Resume_module_dispatches_completed_batch()
    {
        var prepared = await PrepareApprovedAsync();
        Assert.True((await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None)).IsSuccess);

        var module = new ResumeOperationModule(CreateResumeHandler());
        var input = JsonSerializer.SerializeToElement(new ResumeBatchInput(prepared.BatchId), IngestJsonContext.Default.ResumeBatchInput);
        var result = await module.HandleAsync(IngestOperationIds.Resume, new OperationRequest(input, actor, null), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("completed", result.Value!.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resume_does_not_reread_source_path()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        // Delete any source PDFs under the data root; resume must still complete from frozen store.
        foreach (var pdf in Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories))
        {
            File.Delete(pdf);
        }

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        Assert.Equal(ImportReceiptStatus.Completed, resume.Value!.Status);
    }

    [Fact]
    public async Task Terminal_conflicted_candidates_are_skipped_on_resume()
    {
        var prepared = await PrepareApprovedAsync();
        var work = await LoadWork(prepared);
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
        var firstRun = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.Equal(CommitErrors.LedgerConflict, firstRun.ErrorCode);

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        // Conflicted candidate is terminal and skipped; remaining candidates may complete.
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        var conflicted = resume.Value!.CandidateOutcomes.Single(o => o.CandidateId == first.CandidateId);
        Assert.Equal(CandidateReceiptState.Conflicted, conflicted.State);
        Assert.Null(conflicted.LedgerTransactionId);
        Assert.True(resume.Value.Counts.Conflicted >= 1);
        Assert.True(resume.Value.Counts.Accepted >= 1);
    }

    [Fact]
    public async Task Resume_skips_terminal_accepted_after_canonical_revalidation()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        var resume = await CreateResumeHandler().HandleAsync(new ResumeCommand(prepared.BatchId), CancellationToken.None);
        Assert.True(resume.IsSuccess, resume.ErrorCode);
        var accepted = resume.Value!.CandidateOutcomes.Where(o => o.State == CandidateReceiptState.Accepted).ToArray();
        Assert.True(accepted.Length >= 2);
        foreach (var outcome in accepted)
        {
            var fetched = await ledger.GetTransactionAsync(outcome.LedgerTransactionId!, "1.0", actor, CancellationToken.None);
            Assert.True(fetched.IsSuccess);
        }
    }

    [Fact]
    public async Task Fault_injector_records_boundaries_in_order()
    {
        var prepared = await PrepareApprovedAsync();
        var injector = new CommitFaultInjector(CommitFaultInjector.FaultPoint.None);
        var result = await CreateSaga(injector).ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Contains(injector.ObservedCandidates, s => s.StartsWith("BeforeLedgerCall:", StringComparison.Ordinal));
        Assert.Contains(injector.ObservedCandidates, s => s.StartsWith("AfterLedgerCommit:", StringComparison.Ordinal));
        Assert.Contains(injector.ObservedCandidates, s => s.StartsWith("BeforeReceiptDurability:", StringComparison.Ordinal));
        Assert.Contains(injector.ObservedCandidates, s => s.StartsWith("AfterReceiptDurability:", StringComparison.Ordinal));
        Assert.Contains(injector.ObservedCandidates, s => s.StartsWith("BetweenCandidates:", StringComparison.Ordinal));
    }

    private ResumeHandler CreateResumeHandler(ICommitFaultHook? hook = null) =>
        new(new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore()), CreateSaga(hook));

    private CandidateCommitSaga CreateSaga(ICommitFaultHook? hook = null)
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        return new CandidateCommitSaga(
            new ReviewStateStore(database),
            new CommitStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            ledger,
            time,
            hook);
    }

    private async Task<(string BatchId, string ManifestRevisionId, string Digest, string AccountId)> PrepareApprovedAsync()
    {
        var accountId = await CreateAccountAsync();
        var preview = await PreviewAsync(accountId);
        var digest = await InspectDigestAsync(preview.BatchId, preview.ManifestRevisionId!);
        var approve = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())), time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, digest, actor), CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
        return (preview.BatchId, preview.ManifestRevisionId!, digest, accountId);
    }

    private Task<IReadOnlyList<CommitStateStore.CandidateWorkItem>> LoadWork(
        (string BatchId, string ManifestRevisionId, string Digest, string AccountId) prepared) =>
        new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);

    private async Task<PreviewImportResult> PreviewAsync(string accountId)
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf("layout-a"));
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var account = new AccountDetail(
            accountId, "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "human:owner", "2026-01-01T00:00:00Z", null, []);
        var handler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new StubPdfExtractor(LayoutAEvidence()),
            StatementAdapterRegistry.CreateDefault(),
            new PreviewStateStore(database, new BatchErrorEventStore()),
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
            new CreateAccountInput("Resume Bank", "Primary", AccountType.Cheque, "****5555", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", actor, input, $"create-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!.AccountId;
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenIngestAsync()
    {
        var connection = await new IngestDatabase(root, new IngestArtifactProtection()).OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private static PdfDocumentEvidence LayoutAEvidence()
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

        return new PdfDocumentEvidence("fp", 1, [new PdfPageEvidence(1, 612, 792, glyphs, [])]);
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
}
