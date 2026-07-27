using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Security;

/// <summary>
/// NFR-INGEST-LOCAL-DATA-PROTECTION / TC-INGEST-ARTIFACT-PROTECTION security boundary gate.
/// Failures use metadata-only identifiers — never fixture paths or financial payloads.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class IngestSecurityBoundaryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-sec-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("human", "owner");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 27, 19, 0, 0, TimeSpan.Zero));

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
    public async Task Ingest_directories_and_database_sidecars_are_owner_only()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        await using var connection = await database.OpenAsync(CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(database.IngestDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath));
    }

    [Fact]
    public void Permission_failure_blocks_before_sensitive_persistence()
    {
        var protection = new IngestArtifactProtection();
        Assert.Throws<FileNotFoundException>(() => protection.EnsureOwnerOnly(Path.Combine(root, "missing-sensitive")));
    }

    [Fact]
    public async Task Preview_source_path_is_not_echoed_in_error_codes_or_safe_messages()
    {
        var canaryPath = Path.Combine(root, "CANARY-SECRET-PATH-statement.pdf");
        await File.WriteAllBytesAsync(canaryPath, CreatePdf("not-really-a-statement"));
        var before = await Sha256Async(canaryPath);

        var result = await CreatePreviewHandler().HandleAsync(
            new PreviewCommand("1.0", canaryPath, "acc-1", actor),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorCode));
        Assert.DoesNotContain("CANARY-SECRET-PATH", result.ErrorCode!, StringComparison.Ordinal);
        Assert.DoesNotContain(canaryPath, result.ErrorCode!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await Sha256Async(canaryPath));
    }

    [Fact]
    public async Task Private_source_bytes_are_unchanged_across_preview_failure()
    {
        var path = Path.Combine(root, $"src-{Guid.NewGuid():N}.pdf");
        var bytes = CreatePdf("layout-a");
        await File.WriteAllBytesAsync(path, bytes);
        var before = SHA256.HashData(bytes);

        _ = await CreatePreviewHandler().HandleAsync(
            new PreviewCommand("1.0", path, "acc-missing", actor),
            CancellationToken.None);

        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    }

    [Fact]
    public async Task Startup_cleanup_does_not_remove_unknown_or_source_files()
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(root, protection);
        var locks = new BatchCommitLock(database, protection);
        var source = Path.Combine(root, "owner-statement.pdf");
        await File.WriteAllBytesAsync(source, [9, 9, 9]);
        var before = await File.ReadAllBytesAsync(source);

        _ = await new StartupIngestCleanup(database, locks, protection).RunAsync(CancellationToken.None);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public void Registry_ingest_operations_do_not_accept_sourcePath_as_cli_argument_name()
    {
        foreach (var descriptor in OperationRegistry.Create().Descriptors.Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("sourcePath", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--source", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--input", descriptor.Example, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Architecture_rejects_http_plugin_and_private_ledger_storage_on_ingest_composition()
    {
        var composition = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Bootstrap", "Features", "IngestExtensions.cs"));
        string[] forbidden =
        [
            "FastEndpoints", "Aspire", "Npgsql", "EntityFramework", "Microsoft.AspNetCore",
            "Assembly.Load", "HttpListener", "WebApplication", "AddPlugins", "MEF"
        ];
        Assert.All(forbidden, value => Assert.DoesNotContain(value, composition, StringComparison.OrdinalIgnoreCase));
        // Explicit composition root forbids reflective plugin loading APIs.
        Assert.DoesNotContain("Assembly.LoadFrom", composition, StringComparison.Ordinal);

        var clientSource = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));
        Assert.DoesNotContain("LedgerDb", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", clientSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingest_public_errors_are_metadata_only_without_financial_field_names_in_codes()
    {
        var codes = typeof(PreviewErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)?.ToString() ?? string.Empty)
            .ToArray();
        Assert.All(codes, code =>
        {
            Assert.StartsWith("INGEST-", code, StringComparison.Ordinal);
            Assert.DoesNotContain("amount", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("balance", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("description", code, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Malformed_pdf_fails_closed_with_stable_code()
    {
        var path = Path.Combine(root, "malformed.pdf");
        await File.WriteAllTextAsync(path, "not-a-pdf");
        var before = await Sha256Async(path);
        var result = await CreatePreviewHandler().HandleAsync(
            new PreviewCommand("1.0", path, "acc-1", actor),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.StartsWith("INGEST-", result.ErrorCode!, StringComparison.Ordinal);
        Assert.Equal(before, await Sha256Async(path));
    }

    [Fact]
    public void Ingest_operation_ids_are_exactly_the_eight_named_operations()
    {
        Assert.Equal(8, IngestOperationIds.All.Count);
        Assert.DoesNotContain(IngestOperationIds.All, id => id.Contains("import", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(IngestOperationIds.All, id => id.Contains("run", StringComparison.OrdinalIgnoreCase));
    }

    private PreviewHandler CreatePreviewHandler()
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        var account = new AccountDetail(
            "acc-1", "institution", "display", AccountType.Cheque, AccountClass.Asset, "masked", "ZAR",
            AccountStatus.Active, "human:owner", "2026-01-01T00:00:00Z", null, []);
        return new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory((_, _, _, _) => Task.FromResult<AccountDetail?>(account)),
            new PreviewPdfAdapter(new PdfStatementTextExtractor()),
            StatementAdapterRegistry.CreateDefault(),
            new PreviewStateStore(database, new BatchErrorEventStore()),
            time);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string RepositoryRoot()
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

    private sealed class PreviewPdfAdapter(PdfStatementTextExtractor inner) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken) =>
            inner.ExtractAsync(source, limits, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
