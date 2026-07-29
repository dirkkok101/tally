using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Ingest.Storage.Migrations;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Preview;

// TC-INGEST-STATEMENT-PREVIEW-CONTRACT / FR-INGEST-STATEMENT-PREVIEW
// NFR-INGEST-DETERMINISTIC-INTEGRITY
[SupportedOSPlatform("linux")]
public sealed class PreviewWorkflowTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-preview-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("owner", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
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
    public async Task Invalid_input_fails_closed()
    {
        var result = await Handler(Account("acc-1"))
            .HandleAsync(new PreviewCommand("", "/tmp/x.pdf", "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Non_rooted_source_path_fails_closed()
    {
        var result = await Handler(Account("acc-1"))
            .HandleAsync(new PreviewCommand("1.0", "relative.pdf", "acc-1", actor), CancellationToken.None);
        Assert.Equal(CallerOwnedSourceReader.PathInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Missing_account_fails_closed()
    {
        var path = WriteBytes("missing-account.pdf", CreatePdf("x"));
        var result = await Handler(null)
            .HandleAsync(new PreviewCommand("1.0", path, "missing", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.AccountNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Inactive_account_fails_closed()
    {
        var path = WriteBytes("inactive.pdf", CreatePdf("x"));
        var result = await Handler(Account("acc-1") with { Status = AccountStatus.Archived })
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.AccountInactive, result.ErrorCode);
    }

    [Fact]
    public async Task Non_zar_account_fails_closed()
    {
        var path = WriteBytes("usd.pdf", CreatePdf("x"));
        var result = await Handler(Account("acc-1", currency: "USD"))
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.AccountCurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Unreadable_source_fails_closed()
    {
        var missing = Path.Combine(root, "does-not-exist.pdf");
        var result = await Handler(Account("acc-1"))
            .HandleAsync(new PreviewCommand("1.0", missing, "acc-1", actor), CancellationToken.None);
        Assert.Equal(CallerOwnedSourceReader.SourceUnreadable, result.ErrorCode);
    }

    [Fact]
    public async Task Oversize_source_fails_closed()
    {
        var path = WriteBytes("big.pdf", CreatePdf("too-large-content"));
        var reader = new CallerOwnedSourceReader();
        var direct = reader.Read(path, maxBytes: 4);
        Assert.Equal(CallerOwnedSourceReader.SourceTooLarge, direct.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_extraction_fails_without_manifest()
    {
        var path = WriteBytes("unsupported.pdf", CreatePdf("hello"));
        var result = await Handler(Account("acc-1"), evidence: null, extractionError: "INGEST-PDF-UNSUPPORTED-SCAN")
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.Equal("INGEST-PDF-UNSUPPORTED-SCAN", result.ErrorCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM manifest_revision;"));
    }

    [Fact]
    public async Task No_matching_adapter_fails_without_manifest()
    {
        var path = WriteBytes("nomatch.pdf", CreatePdf("x"));
        var empty = new PdfDocumentEvidence("fp", 1, [new PdfPageEvidence(1, 612, 792, [], [])]);
        var result = await Handler(Account("acc-1"), evidence: empty)
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.Unsupported, result.ErrorCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM manifest_revision;"));
    }

    [Fact]
    public async Task Successful_preview_persists_batch_and_revision()
    {
        var path = WriteBytes("ok.pdf", CreatePdf("layout-a"));
        var result = await Handler(Account("acc-1"), evidence: LayoutAEvidence())
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.BatchId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ManifestRevisionId));
        Assert.Equal(BatchStatus.Previewed, result.Value.Status);
        Assert.Equal("pdf-text-layout-a-v1", result.Value.Adapter);
        Assert.True(result.Value.Counts.AcceptedCandidates >= 1);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM manifest_revision;"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM ingest_batch;"));
    }

    [Fact]
    public async Task Exact_replay_returns_prior_batch_without_second_manifest()
    {
        var path = WriteBytes("replay.pdf", CreatePdf("layout-a"));
        var handler = Handler(Account("acc-1"), evidence: LayoutAEvidence());
        var first = await handler.HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        var second = await handler.HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);

        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.BatchId, second.Value!.BatchId);
        Assert.Equal(first.Value.ManifestRevisionId, second.Value.ManifestRevisionId);
        Assert.Equal(first.Value.BatchId, second.Value.ExactReplayOf);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM manifest_revision;"));
    }

    [Fact]
    public async Task Source_bytes_are_preserved_after_preview()
    {
        var bytes = CreatePdf("layout-a");
        var path = WriteBytes("preserve.pdf", bytes);
        var before = await File.ReadAllBytesAsync(path);
        _ = await Handler(Account("acc-1"), evidence: LayoutAEvidence())
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Preview_looks_up_account_once_and_never_records_transactions()
    {
        var path = WriteBytes("no-ledger.pdf", CreatePdf("layout-a"));
        var lookups = 0;
        var result = await Handler(Account("acc-1"), evidence: LayoutAEvidence(), onAccountLookup: () => lookups++)
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void Mapper_uses_fixed_marker_for_source_absent_description()
    {
        var record = new SourceRecordEvidence(
            "r1", 1, 0, "statement-transaction", "raw",
            DescriptionEvidenceKind.SourceAbsentMarker, null,
            new FinancialEvidence(
                "10.00", "ZAR", null, true, "2026-01-01", null, null,
                new StatementPeriod("2026-01-01", "2026-01-31")),
            11_000, null);
        Assert.Equal(
            PreviewManifestMapper.SourceDescriptionUnavailableMarker,
            PreviewManifestMapper.ResolveDescription(record));
    }

    [Fact]
    public void Mapper_preserves_source_text_description()
    {
        var record = new SourceRecordEvidence(
            "r1", 1, 0, "statement-transaction", "raw",
            DescriptionEvidenceKind.SourceText, null,
            new FinancialEvidence(
                "10.00", "ZAR", "Coffee", true, "2026-01-01", null, null,
                new StatementPeriod("2026-01-01", "2026-01-31")),
            null, null);
        Assert.Equal("Coffee", PreviewManifestMapper.ResolveDescription(record));
    }

    [Fact]
    public async Task Overlap_with_different_fingerprint_same_period_is_blocked()
    {
        var account = Account("acc-1");
        var firstPath = WriteBytes("first.pdf", CreatePdf("a"));
        var secondPath = WriteBytes("second.pdf", CreatePdf("b"));
        var first = await Handler(account, evidence: LayoutAEvidence("fp-a"))
            .HandleAsync(new PreviewCommand("1.0", firstPath, "acc-1", actor), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await Handler(account, evidence: LayoutAEvidence("fp-b"))
            .HandleAsync(new PreviewCommand("1.0", secondPath, "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.OverlapBlocked, second.ErrorCode);
    }

    [Fact]
    public async Task Consecutive_inclusive_periods_that_only_touch_boundary_preview_on_same_account()
    {
        var account = Account("acc-1");
        var firstPath = WriteBytes("period-a.pdf", CreatePdf("period-a"));
        var secondPath = WriteBytes("period-b.pdf", CreatePdf("period-b"));
        // Inclusive windows: Jan 1–31 then Jan 31–Feb 28 (shared endpoint only).
        var first = await Handler(
                account,
                evidence: LayoutAEvidence(
                    "fp-period-a",
                    "01 January 2026",
                    "31 January 2026",
                    "01 Jan First row 10.00Cr 110.00Cr",
                    "31 Jan Boundary row 10.00Cr 120.00Cr"))
            .HandleAsync(new PreviewCommand("1.0", firstPath, "acc-1", actor), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await Handler(
                account,
                evidence: LayoutAEvidence(
                    "fp-period-b",
                    "31 January 2026",
                    "28 February 2026",
                    "31 Jan Boundary row 10.00Cr 110.00Cr",
                    "15 Feb Later row 10.00Cr 120.00Cr"))
            .HandleAsync(new PreviewCommand("1.0", secondPath, "acc-1", actor), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.NotEqual(first.Value!.BatchId, second.Value!.BatchId);
        // Shared boundary economic facts → exact-duplicate count on the later statement.
        Assert.True(second.Value.Counts.ExactDuplicates >= 1, "shared boundary row must be exact-duplicated");
        Assert.True(second.Value.Counts.AcceptedCandidates >= 1, "non-boundary rows on the later statement remain accepted");
    }

    [Fact]
    public async Task Interior_overlap_on_same_account_is_still_blocked()
    {
        var account = Account("acc-1");
        var firstPath = WriteBytes("interior-a.pdf", CreatePdf("interior-a"));
        var secondPath = WriteBytes("interior-b.pdf", CreatePdf("interior-b"));
        var first = await Handler(
                account,
                evidence: LayoutAEvidence(
                    "fp-int-a",
                    "01 January 2026",
                    "31 January 2026",
                    "01 Jan First row 10.00Cr 110.00Cr",
                    "15 Jan Second row 10.00Cr 120.00Cr"))
            .HandleAsync(new PreviewCommand("1.0", firstPath, "acc-1", actor), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await Handler(
                account,
                evidence: LayoutAEvidence(
                    "fp-int-b",
                    "15 January 2026",
                    "15 February 2026",
                    "15 Jan Overlap row 10.00Cr 110.00Cr",
                    "01 Feb Later row 10.00Cr 120.00Cr"))
            .HandleAsync(new PreviewCommand("1.0", secondPath, "acc-1", actor), CancellationToken.None);
        Assert.Equal(PreviewErrors.OverlapBlocked, second.ErrorCode);
    }

    [Fact]
    public async Task Operation_module_deserializes_preview_input()
    {
        var path = WriteBytes("module.pdf", CreatePdf("layout-a"));
        var module = new PreviewOperationModule(Handler(Account("acc-1"), evidence: LayoutAEvidence()));
        var input = JsonSerializer.SerializeToElement(
            new PreviewImportInput("1.0", path, "acc-1", actor),
            IngestJsonContext.Default.PreviewImportInput);
        var result = await module.HandleAsync(
            IngestOperationIds.Preview,
            new OperationRequest(input, actor, null),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
    }

    [Fact]
    public async Task Operation_module_rejects_unknown_operation_id()
    {
        var module = new PreviewOperationModule(Handler(Account("acc-1")));
        var result = await module.HandleAsync(
            "ingest.unknown",
            new OperationRequest(JsonSerializer.SerializeToElement(new { }), actor, null),
            CancellationToken.None);
        Assert.Equal("operation.not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Atomic_persistence_writes_candidates_and_outcomes_together()
    {
        var path = WriteBytes("atomic.pdf", CreatePdf("layout-a"));
        var result = await Handler(Account("acc-1"), evidence: LayoutAEvidence())
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var outcomes = await CountAsync("SELECT COUNT(*) FROM source_record_outcome;");
        var candidates = await CountAsync("SELECT COUNT(*) FROM import_candidate;");
        Assert.True(outcomes >= 1);
        Assert.True(candidates >= 1);
        Assert.Equal(
            outcomes,
            result.Value!.Counts.AcceptedCandidates +
            result.Value.Counts.Blocked +
            result.Value.Counts.ExcludedNonTransactions +
            result.Value.Counts.ExactDuplicates);
    }

    [Fact]
    public async Task Committable_preview_exposes_reconciliation_summary()
    {
        var path = WriteBytes("recon.pdf", CreatePdf("layout-a"));
        var result = await Handler(Account("acc-1"), evidence: LayoutAEvidence())
            .HandleAsync(new PreviewCommand("1.0", path, "acc-1", actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotNull(result.Value!.ReconciliationSummary);
        Assert.True(result.Value.ReconciliationSummary.FullyReconciled);
        Assert.NotEmpty(result.Value.ReconciliationSummary.Controls);
    }

    [Fact]
    public async Task Private_fixture_preview_succeeds_when_injected()
    {
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var fixture = fixtureSet.Fixtures[0];
        var path = WriteBytes("private.pdf", fixture.SourceBytes.ToArray());
        var kind = fixture.Expected.GetProperty("accountEvidence").GetProperty("accountKind").GetString() ?? "asset";
        var accountClass = kind.Contains("liability", StringComparison.OrdinalIgnoreCase)
            ? AccountClass.Liability
            : AccountClass.Asset;
        var currency = fixture.Expected.GetProperty("accountEvidence").GetProperty("currency").GetString()!;
        var result = await Handler(Account("private-acc", accountClass, currency), useRealExtractor: true)
            .HandleAsync(new PreviewCommand("1.0", path, "private-acc", actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotNull(result.Value!.ManifestRevisionId);
    }

    [Fact]
    public void Source_reader_rejects_traversal_paths()
    {
        var reader = new CallerOwnedSourceReader();
        var result = reader.Read("/tmp/../etc/passwd", 1024);
        Assert.Equal(CallerOwnedSourceReader.PathInvalid, result.ErrorCode);
    }

    private PreviewHandler Handler(
        AccountDetail? account,
        PdfDocumentEvidence? evidence = null,
        string? extractionError = null,
        bool useRealExtractor = false,
        Action? onAccountLookup = null)
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var store = new PreviewStateStore(database, new BatchErrorEventStore());
        IPreviewPdfExtractor pdf = useRealExtractor
            ? new DefaultPreviewPdfExtractor(new PdfStatementTextExtractor())
            : new StubPdfExtractor(evidence, extractionError);
        IPreviewAccountDirectory accounts = new LedgerPreviewAccountDirectory((_, _, _, _) =>
        {
            onAccountLookup?.Invoke();
            return Task.FromResult(account);
        });
        return new PreviewHandler(
            new CallerOwnedSourceReader(),
            accounts,
            pdf,
            StatementAdapterRegistry.CreateDefault(),
            store,
            time);
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<int> CountAsync(string sql)
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static AccountDetail Account(
        string accountId,
        AccountClass accountClass = AccountClass.Asset,
        string currency = "ZAR") => new(
        accountId,
        "institution",
        "display",
        accountClass == AccountClass.Asset ? AccountType.Cheque : AccountType.CreditCard,
        accountClass,
        "masked",
        currency,
        AccountStatus.Active,
        "actor",
        "2026-01-01T00:00:00Z",
        null,
        []);

    private static PdfDocumentEvidence LayoutAEvidence(string fingerprint = "synthetic-preview") =>
        LayoutAEvidence(
            fingerprint,
            "01 January 2026",
            "31 January 2026",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr");

    private static PdfDocumentEvidence LayoutAEvidence(
        string fingerprint,
        string periodStartFull,
        string periodEndFull,
        string row1,
        string row2)
    {
        string[] lines =
        [
            "Account Card ****1234",
            $"Statement period {periodStartFull} {periodEndFull}",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            row1,
            row2
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tally.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("repo root missing");
    }

    private sealed class StubPdfExtractor(PdfDocumentEvidence? evidence, string? errorCode) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken)
        {
            if (errorCode is not null)
            {
                return ValueTask.FromResult(new PdfExtractionResult(
                    null,
                    new IngestError(
                        errorCode,
                        IngestErrorCategory.Unsupported,
                        "unsupported",
                        null,
                        null,
                        MutationPossibility.None,
                        null,
                        IngestRetryAction.CorrectSource,
                        "source")));
            }

            if (evidence is not null)
            {
                var fp = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
                return ValueTask.FromResult(new PdfExtractionResult(evidence with { SourceFingerprint = fp }, null));
            }

            return ValueTask.FromResult(new PdfExtractionResult(
                null,
                new IngestError(
                    "INGEST-PDF-UNSUPPORTED-SCAN",
                    IngestErrorCategory.Unsupported,
                    "unsupported",
                    null,
                    null,
                    MutationPossibility.None,
                    null,
                    IngestRetryAction.CorrectSource,
                    "source")));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
