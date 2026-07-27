using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.LedgerContract;

[SupportedOSPlatform("linux")]
public sealed class LedgerRoundTripCommitTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-roundtrip-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "ingest-commit", "run-01");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 16, 0, 0, TimeSpan.Zero));
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

    // DM-INGEST-LEDGER-COMMIT-CONTRACT / LedgerImmutableVerification
    [Fact]
    public void Immutable_verification_matches_request_facts_and_single_evidence()
    {
        var request = FrozenRequest("01JACCOUNT0000000000000001", "key-1");
        var detail = new TransactionDetail(
            "01JTXN0000000000000000001",
            request.Input.AccountId,
            request.Input.SignedAmount,
            request.Input.CurrencyCode,
            request.Input.TransactionDate,
            request.Input.PostingDate,
            request.Input.TransactionDate,
            request.Input.OriginalDescription,
            TransactionLifecycleStatus.Active,
            null,
            TransactionReconciliationState.RecordedUnreconciled,
            new TransactionCategoryAssignment(TransactionCategoryState.Uncategorized, null, null, []),
            new TransactionPoolAssignment("pool-evt", TransactionPoolState.Unassigned, null),
            new TransactionPaymentAttribution("attr-evt", TransactionKnowledgeState.Unknown, request.Input.InstrumentId, TransactionKnowledgeState.Unknown, request.Input.CardholderId),
            [
                new TransactionEvidenceDetail(
                    "ev-1",
                    request.Input.InitialEvidence.Kind,
                    request.Input.InitialEvidence.LogicalIdentityDigest,
                    request.Input.InitialEvidence.OpaqueExternalReference,
                    request.Input.InitialEvidence.ContentFingerprint,
                    request.Input.InitialEvidence.Observation,
                    EvidenceLinkRole.Supporting,
                    "link-1",
                    "automation:ingest-commit",
                    "2026-07-01T00:00:00Z")
            ],
            "automation:ingest-commit",
            "2026-07-01T00:00:00Z",
            null);

        Assert.True(CandidateCommitSaga.LedgerImmutableFactsMatch(detail, request, detail.TransactionId));
    }

    [Fact]
    public void Immutable_verification_ignores_mutable_projections_when_facts_match()
    {
        var request = FrozenRequest("01JACCOUNT0000000000000001", "key-2");
        var detail = new TransactionDetail(
            "01JTXN0000000000000000002",
            request.Input.AccountId,
            request.Input.SignedAmount,
            request.Input.CurrencyCode,
            request.Input.TransactionDate,
            request.Input.PostingDate,
            "2099-01-01",
            request.Input.OriginalDescription,
            TransactionLifecycleStatus.Active,
            null,
            TransactionReconciliationState.StatementReconciled,
            new TransactionCategoryAssignment(TransactionCategoryState.Categorized, "alloc", "cat", ["root", "cat"]),
            new TransactionPoolAssignment("pool-evt", TransactionPoolState.Assigned, "pool-1"),
            new TransactionPaymentAttribution("attr-evt", TransactionKnowledgeState.Unknown, null, TransactionKnowledgeState.Unknown, null),
            [
                new TransactionEvidenceDetail(
                    "ev-2",
                    request.Input.InitialEvidence.Kind,
                    request.Input.InitialEvidence.LogicalIdentityDigest,
                    request.Input.InitialEvidence.OpaqueExternalReference,
                    request.Input.InitialEvidence.ContentFingerprint,
                    request.Input.InitialEvidence.Observation,
                    EvidenceLinkRole.Supporting,
                    "link-2",
                    "other-actor",
                    "2099-01-01T00:00:00Z")
            ],
            "other-actor",
            "2099-01-01T00:00:00Z",
            new TransactionHistory([], [], [], []));

        Assert.True(CandidateCommitSaga.LedgerImmutableFactsMatch(detail, request, detail.TransactionId));
    }

    [Fact]
    public void Immutable_verification_rejects_amount_mismatch()
    {
        var request = FrozenRequest("01JACCOUNT0000000000000001", "key-3");
        var detail = new TransactionDetail(
            "01JTXN0000000000000000003",
            request.Input.AccountId,
            "-99.00",
            request.Input.CurrencyCode,
            request.Input.TransactionDate,
            request.Input.PostingDate,
            request.Input.TransactionDate,
            request.Input.OriginalDescription,
            TransactionLifecycleStatus.Active,
            null,
            TransactionReconciliationState.RecordedUnreconciled,
            new TransactionCategoryAssignment(TransactionCategoryState.Uncategorized, null, null, []),
            new TransactionPoolAssignment("pool-evt", TransactionPoolState.Unassigned, null),
            new TransactionPaymentAttribution("attr-evt", TransactionKnowledgeState.Unknown, null, TransactionKnowledgeState.Unknown, null),
            [
                new TransactionEvidenceDetail(
                    "ev-3",
                    request.Input.InitialEvidence.Kind,
                    request.Input.InitialEvidence.LogicalIdentityDigest,
                    request.Input.InitialEvidence.OpaqueExternalReference,
                    request.Input.InitialEvidence.ContentFingerprint,
                    request.Input.InitialEvidence.Observation,
                    EvidenceLinkRole.Supporting,
                    "link-3",
                    "actor",
                    "2026-07-01T00:00:00Z")
            ],
            "actor",
            "2026-07-01T00:00:00Z",
            null);

        Assert.False(CandidateCommitSaga.LedgerImmutableFactsMatch(detail, request, detail.TransactionId));
    }

    [Fact]
    public void Immutable_verification_rejects_missing_or_extra_evidence()
    {
        var request = FrozenRequest("01JACCOUNT0000000000000001", "key-4");
        var baseDetail = new TransactionDetail(
            "01JTXN0000000000000000004",
            request.Input.AccountId,
            request.Input.SignedAmount,
            request.Input.CurrencyCode,
            request.Input.TransactionDate,
            request.Input.PostingDate,
            request.Input.TransactionDate,
            request.Input.OriginalDescription,
            TransactionLifecycleStatus.Active,
            null,
            TransactionReconciliationState.RecordedUnreconciled,
            new TransactionCategoryAssignment(TransactionCategoryState.Uncategorized, null, null, []),
            new TransactionPoolAssignment("pool-evt", TransactionPoolState.Unassigned, null),
            new TransactionPaymentAttribution("attr-evt", TransactionKnowledgeState.Unknown, null, TransactionKnowledgeState.Unknown, null),
            [],
            "actor",
            "2026-07-01T00:00:00Z",
            null);

        Assert.False(CandidateCommitSaga.LedgerImmutableFactsMatch(baseDetail, request, baseDetail.TransactionId));
    }

    // FR-INGEST-APPROVED-BATCH-COMMIT: public round-trip after commit
    [Fact]
    public async Task Commit_round_trips_each_accepted_candidate_through_public_get()
    {
        var accountId = await CreateAccountAsync();
        var prepared = await PrepareApprovedAsync(accountId);
        var saga = CreateSaga();
        var result = await saga.ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);

        foreach (var outcome in result.Value!.CandidateOutcomes.Where(o => o.State == CandidateReceiptState.Accepted))
        {
            var item = work.Single(w => w.CandidateId == outcome.CandidateId);
            var fetched = await ledger.GetTransactionAsync(
                outcome.LedgerTransactionId!,
                item.FrozenRequest.LedgerContractVersion,
                item.FrozenRequest.Actor,
                CancellationToken.None);
            Assert.True(fetched.IsSuccess, fetched.Error?.Code);
            Assert.True(CandidateCommitSaga.LedgerImmutableFactsMatch(
                fetched.Value!,
                item.FrozenRequest,
                outcome.LedgerTransactionId!));
            Assert.Null(fetched.Value!.History);
        }
    }

    [Fact]
    public async Task Replay_of_frozen_request_preserves_prior_ledger_transaction_id()
    {
        var accountId = await CreateAccountAsync();
        var prepared = await PrepareApprovedAsync(accountId);
        var work = await new CommitStateStore(new IngestDatabase(root, new IngestArtifactProtection()), new BatchErrorEventStore())
            .LoadWorkItemsAsync(prepared.BatchId, prepared.ManifestRevisionId, CancellationToken.None);
        var first = work[0];

        var recorded = await ledger.RecordTransactionAsync(first.FrozenRequest, CancellationToken.None);
        var replay = await ledger.RecordTransactionAsync(first.FrozenRequest, CancellationToken.None);
        Assert.True(recorded.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(recorded.Value!.TransactionId, replay.Value!.TransactionId);

        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var accepted = result.Value!.CandidateOutcomes.Single(o => o.CandidateId == first.CandidateId);
        Assert.Equal(recorded.Value.TransactionId, accepted.LedgerTransactionId);
    }

    [Fact]
    public async Task Get_after_commit_excludes_history_per_public_contract()
    {
        var accountId = await CreateAccountAsync();
        var prepared = await PrepareApprovedAsync(accountId);
        var result = await CreateSaga().ExecuteAsync(
            new CommitCommand(prepared.BatchId, prepared.ManifestRevisionId, prepared.Digest),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var txnId = result.Value!.CandidateOutcomes.First(o => o.LedgerTransactionId is not null).LedgerTransactionId!;
        var fetched = await ledger.GetTransactionAsync(txnId, "1.0", actor, CancellationToken.None);
        Assert.True(fetched.IsSuccess);
        Assert.Null(fetched.Value!.History);
    }

    private CandidateCommitSaga CreateSaga()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        return new CandidateCommitSaga(
            new ReviewStateStore(database),
            new CommitStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            ledger,
            time);
    }

    private async Task<(string BatchId, string ManifestRevisionId, string Digest)> PrepareApprovedAsync(string accountId)
    {
        var preview = await PreviewAsync(accountId);
        var inspect = await new InspectHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())))
            .HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.ErrorCode);
        var digest = inspect.Value!.CanonicalDigest;
        var approve = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(root, new IngestArtifactProtection())), time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, digest, actor), CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
        return (preview.BatchId, preview.ManifestRevisionId!, digest);
    }

    private async Task<PreviewImportResult> PreviewAsync(string accountId)
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreatePdf("layout-a"));
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var account = new AccountDetail(
            accountId, "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "automation:ingest-commit", "2026-01-01T00:00:00Z", null, []);
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

    private async Task<string> CreateAccountAsync()
    {
        var input = JsonSerializer.SerializeToElement(
            new CreateAccountInput("Round Trip Bank", "Primary", AccountType.Cheque, "****9999", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", actor, input, $"create-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!.AccountId;
    }

    private static string Digest(char value) => new(value, 64);

    private FrozenLedgerRecordRequest FrozenRequest(string accountId, string idempotencyKey) => new(
        "1.0",
        "ledger.transaction.record",
        idempotencyKey,
        actor,
        new RecordTransactionInput(
            accountId,
            "-12.34",
            "ZAR",
            "2026-07-01",
            "2026-07-03",
            "Synthetic transaction",
            null,
            null,
            new RegisterEvidenceInput(
                EvidenceKind.StatementRow,
                Digest('a'),
                $"ingest:{Digest('a')}",
                Digest('b'),
                new EvidenceObservation(accountId, -1234, "ZAR", "2026-07-01", "2026-07-03", null, null, Digest('c')))));

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
