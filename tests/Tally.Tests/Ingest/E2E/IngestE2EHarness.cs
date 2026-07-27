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
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>
/// Shared published-surface harness for INGEST UC verification gates.
/// Never logs fixture paths or financial payloads.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class IngestE2EHarness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tally-ingest-e2e-{Guid.NewGuid():N}");
    public SafeActor Actor { get; } = new("human", "owner");
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 7, 27, 20, 0, 0, TimeSpan.Zero));
    public LedgerContractClient Ledger { get; private set; } = null!;
    public OperationRegistry Registry { get; private set; } = null!;
    private TallyProcess process = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Root, CancellationToken.None);
        Registry = OperationRegistry.Create();
        process = new TallyProcess(Registry, LedgerServices.Create(database));
        Ledger = new LedgerContractClient(Registry, process);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    public PrivateStatementFixtureSet? TryPrivateFixtures() =>
        PrivateStatementFixtureSet.TryLoadFromEnvironment(RepositoryRoot());

    public async Task<string> CreateAccountAsync(string displayName = "E2E Bank")
    {
        var input = JsonSerializer.SerializeToElement(
            new CreateAccountInput(displayName, "Primary", AccountType.Cheque, "****4242", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", Actor, input, $"create-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!.AccountId;
    }

    public async Task<PreviewImportResult> PreviewSyntheticAsync(string accountId)
    {
        var path = Path.Combine(Root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreateLayoutAPdf());
        return await PreviewPathAsync(accountId, path);
    }

    public async Task<PreviewImportResult> PreviewPathAsync(string accountId, string path)
    {
        var database = new IngestDatabase(Root, new IngestArtifactProtection());
        var account = new AccountDetail(
            accountId, "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "human:owner", "2026-01-01T00:00:00Z", null, []);
        // Synthetic PDFs use a layout-A glyph stub so workflow gates exercise contracts without
        // depending on private fixture availability. Private fixtures use real extraction elsewhere.
        var handler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new StubLayoutAExtractor(LayoutAEvidence()),
            StatementAdapterRegistry.CreateDefault(),
            new PreviewStateStore(database, new BatchErrorEventStore()),
            Time);
        var result = await handler.HandleAsync(new PreviewCommand("1.0", path, accountId, Actor), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!;
    }

    public async Task<(string BatchId, string ManifestRevisionId, string Digest)> ApprovePreviewAsync(PreviewImportResult preview)
    {
        var inspect = await new InspectHandler(new ReviewStateStore(new IngestDatabase(Root, new IngestArtifactProtection())))
            .HandleAsync(new InspectQuery(preview.BatchId, preview.ManifestRevisionId!), CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.ErrorCode);
        var digest = inspect.Value!.CanonicalDigest;
        var approve = await new ApproveHandler(new ReviewStateStore(new IngestDatabase(Root, new IngestArtifactProtection())), Time)
            .HandleAsync(new ApproveCommand(preview.BatchId, preview.ManifestRevisionId!, digest, Actor), CancellationToken.None);
        Assert.True(approve.IsSuccess, approve.ErrorCode);
        return (preview.BatchId, preview.ManifestRevisionId!, digest);
    }

    public CandidateCommitSaga CreateSaga(ICommitFaultHook? hook = null)
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(Root, protection);
        return new CandidateCommitSaga(
            new ReviewStateStore(database),
            new CommitStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            Ledger,
            Time,
            hook);
    }

    public ResumeHandler CreateResume()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(Root, protection);
        return new ResumeHandler(
            new CommitStateStore(database, new BatchErrorEventStore()),
            CreateSaga());
    }

    public AbandonHandler CreateAbandon()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(Root, protection);
        return new AbandonHandler(
            new RecoveryStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            Time);
    }

    public CleanupHandler CreateCleanup()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(Root, protection);
        return new CleanupHandler(
            new RecoveryStateStore(database, new BatchErrorEventStore()),
            new BatchCommitLock(database, protection),
            Time);
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    public static byte[] CreateLayoutAPdf()
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
        // Synthetic PDF is enough for adapter registry + workflow contracts; private fixtures cover real layouts.
        var content = $"BT /F1 12 Tf 72 700 Td ({string.Join(") Tj T* (", lines)}) Tj ET";
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

    public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PdfDocumentEvidence LayoutAEvidence(string fingerprint = "synthetic-e2e")
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

    private sealed class StubLayoutAExtractor(PdfDocumentEvidence evidence) : IPreviewPdfExtractor
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
}
