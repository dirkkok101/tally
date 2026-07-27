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
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.Recovery;

[SupportedOSPlatform("linux")]
public sealed class AbandonCleanupTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-abandon-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("human", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 18, 0, 0, TimeSpan.Zero));
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
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Abandon_requires_batch_and_reason()
    {
        var result = await CreateAbandon().HandleAsync(new AbandonCommand("", ""), CancellationToken.None);
        Assert.Equal(AbandonErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_unknown_batch_fails_closed()
    {
        var result = await CreateAbandon().HandleAsync(new AbandonCommand("missing", "stop"), CancellationToken.None);
        Assert.Equal(AbandonErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_previewed_batch_marks_abandoned_and_retains_metadata()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        var result = await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "owner-stop"), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BatchStatus.Abandoned, result.Value!.Status);
        Assert.True(result.Value.RetainedMetadata);
        Assert.Equal(0, result.Value.PriorLedgerEffectCount);
        Assert.Equal(BatchStatus.Abandoned, await ReadStatus(preview.BatchId));
    }

    [Fact]
    public async Task Abandon_deactivates_approvals_and_blocks_commit()
    {
        var prepared = await PrepareApprovedAsync();
        var abandon = await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "nope"), CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);

        var commit = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.Equal(CommitErrors.NotApproved, commit.ErrorCode);
    }

    [Fact]
    public async Task Abandon_compacts_sensitive_candidate_payloads()
    {
        var prepared = await PrepareApprovedAsync();
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "compact"), CancellationToken.None)).IsSuccess);

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT frozen_ledger_request_json FROM import_candidate LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("{}", reader.GetString(0));
    }

    [Fact]
    public async Task Abandon_rejects_completed_batch()
    {
        var prepared = await PrepareApprovedAsync();
        Assert.True((await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None)).IsSuccess);
        var result = await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "late"), CancellationToken.None);
        Assert.Equal(AbandonErrors.NotAbandonable, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_rejects_when_lock_held()
    {
        var prepared = await PrepareApprovedAsync();
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        await using var held = await new BatchCommitLock(database, protection).TryAcquireAsync(prepared.BatchId, CancellationToken.None);
        Assert.NotNull(held);
        var result = await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "locked"), CancellationToken.None);
        Assert.Equal(AbandonErrors.LockHeld, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_preserves_prior_ledger_effect_count()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitRecovery.CommitFaultInjector(
            CommitRecovery.CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitRecovery.CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));

        // Interrupt then abandon — prior accepted refs remain counted.
        var snapshot = await new RecoveryStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadBatchAsync(prepared.BatchId, CancellationToken.None);
        Assert.True(snapshot!.PriorLedgerEffectCount >= 1);

        var abandon = await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "after-partial"), CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);
        Assert.Equal(snapshot.PriorLedgerEffectCount, abandon.Value!.PriorLedgerEffectCount);
    }

    [Fact]
    public async Task Cleanup_rejects_incomplete_preview_as_retained_for_recovery()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.RetainedForRecovery, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_rejects_approved_batch()
    {
        var prepared = await PrepareApprovedAsync();
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(prepared.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.RetainedForRecovery, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_rejects_mismatched_expected_terminal_status()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.RetainedForRecovery, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_removes_abandoned_tombstone_artifacts()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Abandoned),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BatchStatus.Cleaned, result.Value!.Status);
        Assert.Contains(ArtifactKind.Metadata, result.Value.RemovedArtifactKinds);
        Assert.Equal(BatchStatus.Cleaned, await ReadStatus(preview.BatchId));
    }

    [Fact]
    public async Task Cleanup_removes_completed_batch_artifacts()
    {
        var prepared = await PrepareApprovedAsync();
        Assert.True((await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None)).IsSuccess);
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(prepared.BatchId, BatchStatus.Completed),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BatchStatus.Cleaned, result.Value!.Status);
        Assert.Contains(ArtifactKind.Manifest, result.Value.RemovedArtifactKinds);
        Assert.Contains(ArtifactKind.Receipt, result.Value.RemovedArtifactKinds);

        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM manifest_revision WHERE batch_id = $id;";
        command.Parameters.AddWithValue("$id", prepared.BatchId);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Cleanup_rejects_when_lock_held()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        await using var held = await new BatchCommitLock(database, protection).TryAcquireAsync(preview.BatchId, CancellationToken.None);
        Assert.NotNull(held);
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Abandoned),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.LockHeld, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_unknown_batch_fails_closed()
    {
        var result = await CreateCleanup().HandleAsync(
            new CleanupCommand("missing", BatchStatus.Completed),
            CancellationToken.None);
        Assert.Equal(CleanupErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_does_not_delete_caller_owned_source()
    {
        var accountId = await CreateAccountAsync();
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        var bytes = CreatePdf("layout-a");
        await File.WriteAllBytesAsync(path, bytes);
        var preview = await PreviewPathAsync(accountId, path);
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        Assert.True((await CreateCleanup().HandleAsync(
            new CleanupCommand(preview.BatchId, BatchStatus.Abandoned),
            CancellationToken.None)).IsSuccess);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Module_binds_abandon_and_cleanup_without_global_registration()
    {
        var module = new RecoveryCleanupOperationModule(CreateAbandon(), CreateCleanup());
        Assert.Equal(2, module.Descriptors.Count);
        Assert.Contains(module.Descriptors, d => d.OperationId == IngestOperationIds.Abandon);
        Assert.Contains(module.Descriptors, d => d.OperationId == IngestOperationIds.Cleanup);
        Assert.Null(registry.Find(IngestOperationIds.Abandon));
        Assert.Null(registry.Find(IngestOperationIds.Cleanup));
    }

    [Fact]
    public async Task Module_dispatches_abandon()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        var module = new RecoveryCleanupOperationModule(CreateAbandon(), CreateCleanup());
        var input = JsonSerializer.SerializeToElement(
            new AbandonBatchInput(preview.BatchId, "via-module"),
            IngestJsonContext.Default.AbandonBatchInput);
        var result = await module.HandleAsync(IngestOperationIds.Abandon, new OperationRequest(input, actor, null), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("abandoned", result.Value!.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Module_dispatches_cleanup()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        var module = new RecoveryCleanupOperationModule(CreateAbandon(), CreateCleanup());
        var input = JsonSerializer.SerializeToElement(
            new CleanupBatchInput(preview.BatchId, BatchStatus.Abandoned),
            IngestJsonContext.Default.CleanupBatchInput);
        var result = await module.HandleAsync(IngestOperationIds.Cleanup, new OperationRequest(input, actor, null), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("cleaned", result.Value!.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Abandon_interrupted_batch_is_allowed()
    {
        var prepared = await PrepareApprovedAsync();
        var firstCandidate = (await LoadWork(prepared)).First().CandidateId;
        var injector = new CommitRecovery.CommitFaultInjector(
            CommitRecovery.CommitFaultInjector.FaultPoint.BetweenCandidates, firstCandidate);
        await Assert.ThrowsAsync<CommitRecovery.CommitFaultException>(() =>
            CreateSaga(injector).ExecuteAsync(
                new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
                CancellationToken.None));
        // Fault throws before InterruptReceiptAsync; status may remain Committing with free lock.
        var status = await ReadStatus(prepared.BatchId);
        Assert.True(status is BatchStatus.Interrupted or BatchStatus.Committing, status.ToString());
        var abandon = await CreateAbandon().HandleAsync(new AbandonCommand(prepared.BatchId, "stop-resume"), CancellationToken.None);
        Assert.True(abandon.IsSuccess, abandon.ErrorCode);
        Assert.Equal(BatchStatus.Abandoned, abandon.Value!.Status);
    }

    [Fact]
    public async Task Double_abandon_is_rejected()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "one"), CancellationToken.None)).IsSuccess);
        var second = await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "two"), CancellationToken.None);
        Assert.Equal(AbandonErrors.NotAbandonable, second.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_after_cleaned_is_retained()
    {
        var preview = await PreviewAsync(await CreateAccountAsync());
        Assert.True((await CreateAbandon().HandleAsync(new AbandonCommand(preview.BatchId, "x"), CancellationToken.None)).IsSuccess);
        Assert.True((await CreateCleanup().HandleAsync(new CleanupCommand(preview.BatchId, BatchStatus.Abandoned), CancellationToken.None)).IsSuccess);
        var again = await CreateCleanup().HandleAsync(new CleanupCommand(preview.BatchId, BatchStatus.Abandoned), CancellationToken.None);
        Assert.Equal(CleanupErrors.RetainedForRecovery, again.ErrorCode);
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

    private CleanupHandler CreateCleanup()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        return new CleanupHandler(
            new RecoveryStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            time);
    }

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

    private async Task<(string BatchId, string ManifestRevisionId, string Digest)> PrepareApprovedAsync()
    {
        var accountId = await CreateAccountAsync();
        var preview = await PreviewAsync(accountId);
        var digest = await InspectDigestAsync(preview.BatchId, preview.ManifestRevisionId!);
        var approve = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())), time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, digest, actor), CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
        return (preview.BatchId, preview.ManifestRevisionId!, digest);
    }

    private Task<IReadOnlyList<CommitStateStore.CandidateWorkItem>> LoadWork(
        (string BatchId, string ManifestRevisionId, string Digest) prepared) =>
        new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);

    private async Task<PreviewImportResult> PreviewAsync(string accountId)
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf("layout-a"));
        return await PreviewPathAsync(accountId, path);
    }

    private async Task<PreviewImportResult> PreviewPathAsync(string accountId, string path)
    {
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
            new CreateAccountInput("Abandon Bank", "Primary", AccountType.Cheque, "****7777", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", actor, input, $"create-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!.AccountId;
    }

    private async Task<BatchStatus> ReadStatus(string batchId)
    {
        await using var connection = await OpenIngestAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM ingest_batch WHERE batch_id = $id;";
        command.Parameters.AddWithValue("$id", batchId);
        return (BatchStatus)Convert.ToInt32(await command.ExecuteScalarAsync());
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
