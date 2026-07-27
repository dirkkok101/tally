using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
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
using Tally.Tests.Ingest.CommitRecovery;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>
/// Published-surface harness for INGEST UC verification gates (TallyProcess CLI dispatch).
/// Synthetic PDFs use a layout-A glyph stub so gates do not depend on private fixtures.
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
    private LedgerServices ledgerServices = null!;
    private LedgerDb? ledgerDb;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        ledgerDb = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Root, CancellationToken.None);
        Registry = OperationRegistry.Create();
        ledgerServices = LedgerServices.Create(ledgerDb);
        var bootstrap = new TallyProcess(Registry, ledgerServices);
        Ledger = new LedgerContractClient(Registry, bootstrap);
        RebindIngest();
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// Rebuild the published ingest process through the PRODUCTION composition root
    /// (IngestOperationBundle.CreateServices, same two-phase wiring as Program.cs), overriding
    /// only the PDF extractor stub, the clock, and an optional commit fault hook.
    /// </summary>
    public void RebindIngest(ICommitFaultHook? faultHook = null)
    {
        var services = IngestOperationBundle.CreateServices(
            Root, Ledger, Time, new StubLayoutAExtractor(), faultHook);
        process = new TallyProcess(Registry, ledgerServices with { Ingest = services.Operations });
        Ledger = new LedgerContractClient(Registry, process);
    }

    public PrivateStatementFixtureSet? TryPrivateFixtures() =>
        PrivateStatementFixtureSet.TryLoadFromEnvironment(RepositoryRoot());

    public async Task<string> CreateAccountAsync(string displayName = "E2E Bank")
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var masked = $"****{Random.Shared.Next(1000, 9999)}";
        var input = JsonSerializer.SerializeToElement(
            new CreateAccountInput($"{displayName}-{unique}", $"Primary-{unique}", AccountType.Cheque, masked, "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var envelope = new RequestEnvelope("1.0", Actor, input, $"create-{Guid.NewGuid():N}");
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(["ledger", "account", "create", "--input", "-"], json, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(resultEnvelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!.AccountId;
    }

    public async Task<PreviewImportResult> PreviewSyntheticAsync(string accountId, string? contentMarker = null)
    {
        var path = Path.Combine(Root, $"src-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, CreateLayoutAPdf(contentMarker));
        return await PreviewPathAsync(accountId, path);
    }

    public async Task<PreviewImportResult> PreviewPathAsync(string accountId, string path)
    {
        var (ok, error, value) = await InvokeAsync(
            ["ingest", "preview"],
            new PreviewImportInput(IngestOperationIds.ContractVersion, path, accountId, Actor),
            IngestJsonContext.Default.PreviewImportInput,
            IngestJsonContext.Default.PreviewImportResult);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, PreviewImportResult? Value)> TryPreviewPathAsync(
        string accountId,
        string path)
    {
        return await InvokeAsync(
            ["ingest", "preview"],
            new PreviewImportInput(IngestOperationIds.ContractVersion, path, accountId, Actor),
            IngestJsonContext.Default.PreviewImportInput,
            IngestJsonContext.Default.PreviewImportResult);
    }

    public async Task<InspectManifestResult> InspectAsync(string batchId, string revisionId)
    {
        var (ok, error, value) = await InvokeAsync(
            ["ingest", "inspect"],
            new InspectManifestInput(batchId, revisionId),
            IngestJsonContext.Default.InspectManifestInput,
            IngestJsonContext.Default.InspectManifestResult);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, InspectManifestResult? Value)> TryInspectAsync(
        string batchId,
        string revisionId)
    {
        return await InvokeAsync(
            ["ingest", "inspect"],
            new InspectManifestInput(batchId, revisionId),
            IngestJsonContext.Default.InspectManifestInput,
            IngestJsonContext.Default.InspectManifestResult);
    }

    public async Task<(string BatchId, string ManifestRevisionId, string Digest)> ApprovePreviewAsync(
        PreviewImportResult preview)
    {
        var inspect = await InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
        var (ok, error, _) = await TryApproveAsync(preview.BatchId, preview.ManifestRevisionId!, inspect.CanonicalDigest);
        Assert.True(ok, error);
        return (preview.BatchId, preview.ManifestRevisionId!, inspect.CanonicalDigest);
    }

    public async Task<(bool Ok, string? Error, ApproveManifestResult? Value)> TryApproveAsync(
        string batchId,
        string revisionId,
        string digest)
    {
        return await InvokeAsync(
            ["ingest", "approve"],
            new ApproveManifestInput(batchId, revisionId, digest, Actor),
            IngestJsonContext.Default.ApproveManifestInput,
            IngestJsonContext.Default.ApproveManifestResult);
    }

    public async Task<(bool Ok, string? Error, ImportReceipt? Value)> TryCommitAsync(
        string batchId,
        string revisionId,
        string digest)
    {
        return await InvokeAsync(
            ["ingest", "commit"],
            new CommitBatchInput(batchId, revisionId, digest),
            IngestJsonContext.Default.CommitBatchInput,
            IngestJsonContext.Default.ImportReceipt);
    }

    public async Task<ImportReceipt> CommitAsync(string batchId, string revisionId, string digest)
    {
        var (ok, error, value) = await TryCommitAsync(batchId, revisionId, digest);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, ImportReceipt? Value)> TryResumeAsync(string batchId)
    {
        return await InvokeAsync(
            ["ingest", "resume"],
            new ResumeBatchInput(batchId),
            IngestJsonContext.Default.ResumeBatchInput,
            IngestJsonContext.Default.ImportReceipt);
    }

    public async Task<ImportReceipt> ResumeAsync(string batchId)
    {
        var (ok, error, value) = await TryResumeAsync(batchId);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, IngestStatusResult? Value)> TryStatusAsync(
        string? batchId = null,
        int limit = 50)
    {
        return await InvokeAsync(
            ["ingest", "status"],
            new IngestStatusInput(batchId, limit),
            IngestJsonContext.Default.IngestStatusInput,
            IngestJsonContext.Default.IngestStatusResult);
    }

    public async Task<IngestStatusResult> StatusAsync(string? batchId = null)
    {
        var (ok, error, value) = await TryStatusAsync(batchId);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, AbandonBatchResult? Value)> TryAbandonAsync(
        string batchId,
        string reason)
    {
        return await InvokeAsync(
            ["ingest", "abandon"],
            new AbandonBatchInput(batchId, reason),
            IngestJsonContext.Default.AbandonBatchInput,
            IngestJsonContext.Default.AbandonBatchResult);
    }

    public async Task<AbandonBatchResult> AbandonAsync(string batchId, string reason)
    {
        var (ok, error, value) = await TryAbandonAsync(batchId, reason);
        Assert.True(ok, error);
        return value!;
    }

    public async Task<(bool Ok, string? Error, CleanupBatchResult? Value)> TryCleanupAsync(
        string batchId,
        BatchStatus expected)
    {
        return await InvokeAsync(
            ["ingest", "cleanup"],
            new CleanupBatchInput(batchId, expected),
            IngestJsonContext.Default.CleanupBatchInput,
            IngestJsonContext.Default.CleanupBatchResult);
    }

    public async Task<CleanupBatchResult> CleanupAsync(string batchId, BatchStatus expected)
    {
        var (ok, error, value) = await TryCleanupAsync(batchId, expected);
        Assert.True(ok, error);
        return value!;
    }

    /// <summary>Public ledger observation: count reference-bearing outcomes via get-transaction.</summary>
    public async Task<int> CountResolvableLedgerTransactionsAsync(ImportReceipt receipt)
    {
        var count = 0;
        foreach (var outcome in receipt.CandidateOutcomes)
        {
            if (string.IsNullOrWhiteSpace(outcome.LedgerTransactionId))
            {
                continue;
            }

            var detail = await Ledger.GetTransactionAsync(
                outcome.LedgerTransactionId,
                outcome.LedgerContractVersion,
                Actor,
                CancellationToken.None);
            if (detail.IsSuccess && detail.Value is not null)
            {
                count++;
            }
        }

        return count;
    }

    public async Task<(string BatchId, string ManifestRevisionId, string Digest)> PrepareApprovedAsync(
        string? accountId = null)
    {
        accountId ??= await CreateAccountAsync();
        var preview = await PreviewSyntheticAsync(accountId);
        return await ApprovePreviewAsync(preview);
    }

    /// <summary>
    /// Run commit with a fault hook via published rebind; returns the process error code when the host envelope collapses the fault.
    /// </summary>
    public async Task<(bool Ok, string? Error, ImportReceipt? Value)> CommitWithFaultAsync(
        string batchId,
        string revisionId,
        string digest,
        ICommitFaultHook faultHook)
    {
        RebindIngest(faultHook);
        try
        {
            return await TryCommitAsync(batchId, revisionId, digest);
        }
        finally
        {
            RebindIngest();
        }
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

    public static byte[] CreateLayoutAPdf(string? textMarker = null)
    {
        var marker = textMarker ?? "layout-a";
        string[] lines =
        [
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            $"02 Jan Second row {marker} 10.00Cr 120.00Cr"
        ];
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

    private async Task<(bool Ok, string? Error, TResult? Value)> InvokeAsync<TInput, TResult>(
        string[] cliArgs,
        TInput input,
        JsonTypeInfo<TInput> inputInfo,
        JsonTypeInfo<TResult> resultInfo)
    {
        var element = JsonSerializer.SerializeToElement(input, inputInfo);
        // ValidRequest requires idempotency null when not required.
        var envelope = new RequestEnvelope("1.0", Actor, element, IdempotencyKey: null);
        var json = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var args = cliArgs.Concat(["--input", "-"]).ToArray();
        var result = await process.RunAsync(args, json, CancellationToken.None);
        var resultEnvelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope);
        if (resultEnvelope is null || resultEnvelope.Outcome is not ("success" or "error"))
        {
            Assert.Fail($"published surface produced no contract envelope (exit {result.ExitCode}).");
        }

        if (result.ExitCode != 0 || resultEnvelope.Outcome != "success")
        {
            var code = resultEnvelope.Error?.Code;
            Assert.False(string.IsNullOrWhiteSpace(code), $"error envelope carried no stable code (exit {result.ExitCode}).");
            return (false, code, default);
        }

        Assert.NotNull(resultEnvelope.Result);
        var value = JsonSerializer.Deserialize(resultEnvelope.Result!.Value, resultInfo);
        Assert.NotNull(value);
        return (true, null, value);
    }

    private static PdfDocumentEvidence LayoutAEvidence(string fingerprint, string marker)
    {
        // Fixed-width columns: the layout-A adapter classifies money tokens by x-position
        // relative to the header's "Balance" column, so the description field is padded to a
        // constant width and the content marker varies freely inside it (max 25 chars).
        string[] lines =
        [
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date   Description".PadRight(43) + "Amount     Balance",
            "01 Jan First row".PadRight(43) + "10.00Cr    110.00Cr",
            $"02 Jan Second row {marker}".PadRight(43) + "10.00Cr    120.00Cr"
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

    /// <summary>
    /// Derives evidence CONTENT from the actual source bytes (the marker embedded by
    /// CreateLayoutAPdf), so changed bytes produce changed statement rows — not merely a
    /// changed fingerprint. Overlap and replay gates depend on this distinction.
    /// </summary>
    private sealed class StubLayoutAExtractor : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken)
        {
            var text = Encoding.ASCII.GetString(source.AsSpan());
            var match = System.Text.RegularExpressions.Regex.Match(text, @"02 Jan Second row (.+?) 10\.00Cr");
            var marker = match.Success ? match.Groups[1].Value : "layout-a";
            var fp = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
            return ValueTask.FromResult(new PdfExtractionResult(LayoutAEvidence(fp, marker), null));
        }
    }
}
