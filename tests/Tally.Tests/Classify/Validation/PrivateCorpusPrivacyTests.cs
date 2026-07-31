using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap.Features;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-PRIVATE-CORPUS-READER / NFR-CLASSIFY-LOCAL-DATA-PROTECTION / bd-2fdz
/// Canary scans: paths, descriptions, tokens, amounts, expected outcomes, raw rows never reach
/// durable state or metadata-only failure surfaces.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateCorpusPrivacyTests : IAsyncLifetime
{
    private const string CanaryDescription = "CANARY_PRIVATE_DESCRIPTION_7f3a9c";
    private const string CanaryToken = "canaryprivatizedtoken";
    private const string CanaryAmountMarker = "991122334455";
    private const string CanaryExpectedCategory = "cat-canary-expected-zz";
    private const string CanaryPathFragment = "private-canary-path-segment";

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-corpus-privacy-" + Guid.NewGuid().ToString("N"));
    private readonly PrivateCorpusReader reader = ClassifyCorpusExtensions.CreateReader();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
    public async Task Error_codes_never_embed_path_or_canary_payload()
    {
        var dir = Path.Combine(root, CanaryPathFragment);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, "leak-" + CanaryDescription + ".jsonl");
        WriteOwnerFile(path, "{bad-json-with-" + CanaryDescription + "\n");

        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(PrivateCorpusErrors.Malformed, result.ErrorCode);
        AssertNoCanary(result.ErrorCode!);
        Assert.DoesNotContain(path, result.ErrorCode!, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryPathFragment, result.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_gate_manifest_and_fingerprint_exclude_private_payload()
    {
        var path = Path.Combine(root, "ok.jsonl");
        var line = CorpusLine(
            0,
            "tx-canary",
            "acct",
            CanaryDescription + " " + CanaryToken,
            "outflow",
            long.Parse(CanaryAmountMarker, System.Globalization.CultureInfo.InvariantCulture),
            CanaryExpectedCategory);
        WriteOwnerFile(path, line + "\n");

        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var manifest = result.ToGateManifest(Tally.Domain.Classify.Normalization.NormalizationDescriptor.V1.Version);
        var manifestJson = JsonSerializer.Serialize(manifest);
        AssertNoCanary(manifestJson);
        Assert.DoesNotContain(path, manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryPathFragment, manifestJson, StringComparison.Ordinal);
        AssertNoCanary(result.Fingerprint!.Sha256Hex);
        // Fingerprint is hex only — never the raw description.
        Assert.Equal(64, result.Fingerprint.Sha256Hex.Length);
    }

    [Fact]
    public async Task Failure_result_rows_are_null_so_raw_corpus_is_not_retained()
    {
        var path = Path.Combine(root, "fail.jsonl");
        WriteOwnerFile(path, line: """{"ordinal":0,"transactionId":"tx","accountId":"a","sourceDescription":"CANARY_PRIVATE_DESCRIPTION_7f3a9c","amountAbsoluteMinor":1,"itemLifecycleFingerprint":"life","extra":true}""" + "\n");
        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Rows);
        Assert.Null(result.Fingerprint);
        AssertNoCanary(result.ErrorCode!);
    }

    [Fact]
    public async Task Reader_does_not_write_private_payload_into_classify_db()
    {
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        File.SetUnixFileMode(dataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var services = await ClassifyStateExtensions.CreateStateAsync(dataRoot, CancellationToken.None);

        var corpusPath = Path.Combine(root, "corpus.jsonl");
        WriteOwnerFile(
            corpusPath,
            CorpusLine(0, "tx", "acct", CanaryDescription, "outflow", 42, CanaryExpectedCategory) + "\n");
        var read = await reader.ReadAsync(corpusPath, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);

        // Scan durable classify.db bytes for canaries — reader must never have written them.
        await using (var connection = await services.Store.OpenMigratedAsync(CancellationToken.None))
        {
            // Touch schema only.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM classify_store_meta;";
            _ = await cmd.ExecuteScalarAsync();
        }

        var dbBytes = await File.ReadAllBytesAsync(services.Store.Paths.DatabasePath);
        var dbText = Encoding.UTF8.GetString(dbBytes);
        AssertNoCanary(dbText);
        Assert.DoesNotContain(corpusPath, dbText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symlink_rejection_does_not_surface_target_path_or_payload()
    {
        var target = Path.Combine(root, "target-" + CanaryDescription + ".jsonl");
        WriteOwnerFile(target, CorpusLine(0, "tx", "a", CanaryDescription, "outflow", 1) + "\n");
        var link = Path.Combine(root, "link.jsonl");
        File.CreateSymbolicLink(link, target);

        var result = await reader.ReadAsync(link, CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.SymlinkRejected, result.ErrorCode);
        AssertNoCanary(result.ErrorCode!);
        Assert.DoesNotContain(target, result.ErrorCode!, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryDescription, result.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permission_rejection_is_metadata_only()
    {
        var path = Path.Combine(root, "perm.jsonl");
        WriteOwnerFile(path, CorpusLine(0, "tx", "a", CanaryDescription, "outflow", 1) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, result.ErrorCode);
        AssertNoCanary(result.ErrorCode!);
    }

    [Fact]
    public async Task No_temp_files_contain_canary_after_success_or_failure()
    {
        var ok = Path.Combine(root, "ok.jsonl");
        WriteOwnerFile(ok, CorpusLine(0, "tx", "a", CanaryDescription, "outflow", 1) + "\n");
        _ = await reader.ReadAsync(ok, CancellationToken.None);

        var bad = Path.Combine(root, "bad.jsonl");
        WriteOwnerFile(bad, "{ " + CanaryDescription + "\n");
        _ = await reader.ReadAsync(bad, CancellationToken.None);

        foreach (var entry in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Only the intentional corpus files may contain the canary — no temps.
            if (entry.EndsWith("ok.jsonl", StringComparison.Ordinal)
                || entry.EndsWith("bad.jsonl", StringComparison.Ordinal))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(entry);
            AssertNoCanary(text);
        }

        // Ensure no unexpected files under root beyond the two corpora.
        var files = Directory.GetFiles(root);
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Private_corpus_error_catalog_contains_no_payload_shape_hints()
    {
        var codes = new[]
        {
            PrivateCorpusErrors.PathRequired,
            PrivateCorpusErrors.NotFound,
            PrivateCorpusErrors.SymlinkRejected,
            PrivateCorpusErrors.OwnerRejected,
            PrivateCorpusErrors.PermissionsRejected,
            PrivateCorpusErrors.NotRegularFile,
            PrivateCorpusErrors.Malformed,
            PrivateCorpusErrors.DuplicateOrdinal,
            PrivateCorpusErrors.LimitExceeded,
            PrivateCorpusErrors.Timeout,
            PrivateCorpusErrors.Cancelled,
            PrivateCorpusErrors.ReadFailed,
            PrivateCorpusErrors.FieldInvalid
        };
        foreach (var code in codes)
        {
            Assert.StartsWith("CLASSIFY-CORPUS-", code, StringComparison.Ordinal);
            AssertNoCanary(code);
            Assert.DoesNotContain("/", code, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Expected_outcome_fields_stay_memory_only_and_out_of_manifest()
    {
        var path = Path.Combine(root, "expected.jsonl");
        var line = CorpusLine(0, "tx", "a", "neutral", "outflow", 5, CanaryExpectedCategory, "suggestion");
        WriteOwnerFile(path, line + "\n");
        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(CanaryExpectedCategory, result.Rows![0].ExpectedCategoryId);
        Assert.Equal("suggestion", result.Rows[0].ExpectedOutcomeKind);
        var manifestJson = JsonSerializer.Serialize(result.ToGateManifest("normalization_v1"));
        Assert.DoesNotContain(CanaryExpectedCategory, manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("suggestion", manifestJson, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertNoCanary(string text)
    {
        Assert.DoesNotContain(CanaryDescription, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryToken, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryAmountMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryExpectedCategory, text, StringComparison.Ordinal);
    }

    private static string CorpusLine(
        int ordinal,
        string transactionId,
        string accountId,
        string description,
        string direction,
        long minor,
        string? expectedCategory = null,
        string? expectedKind = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ordinal\":").Append(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(transactionId));
        sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(accountId));
        sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
        sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
        sb.Append(",\"amountAbsoluteMinor\":").Append(minor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"itemLifecycleFingerprint\":\"life-1\"");
        if (expectedCategory is not null)
        {
            sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expectedCategory));
        }

        if (expectedKind is not null)
        {
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expectedKind));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteOwnerFile(string path, string line)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(line));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
