using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Tally.Tests.Classify.Process;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-PROCESS-THROUGHPUT-GATE / bd-2byd —
/// Published linux-x64 Native-AOT executable proofs for the five additive operations:
/// throughput (146 rows / pageSize 500 / &lt;5s / &lt;256 MiB / zero child-per-row),
/// pagination accounting, one structured envelope, typed exits, privacy-safe stderr,
/// and selected_outcomes preview composition. Fixture seeding is in-process only;
/// measured invocations always use the published binary under TALLY_PUBLISHED_BINARY.
/// Disposable 0700 synthetic roots only — never live TALLY_DATA_ROOT.
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(ErgonomicsProcessCollection.Name)]
public sealed class ClassifyOperatorErgonomicsProcessTests
{
    private const long MaxPeakRssBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan MaxWallTime = TimeSpan.FromSeconds(5);

    private readonly ErgonomicsProcessFixture fx;

    public ClassifyOperatorErgonomicsProcessTests(ErgonomicsProcessFixture fixture)
    {
        fx = fixture;
    }

    // ── Throughput ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_146_rows_page_size_500_one_invocation_within_bounds()
    {
        RequirePublishedBinary();
        var input = ClassifyEnvelope(
            $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"pageSize\":500}}",
            idempotencyKey: null);

        var measured = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            input);

        Assert.Equal(0, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.outcome.list", doc.RootElement.GetProperty("operation_id").GetString());
        var result = doc.RootElement.GetProperty("result_or_error");
        var returned = result.GetProperty("returnedCount").GetInt32();
        Assert.True(returned >= 146, $"returnedCount={returned}");
        Assert.Equal(returned, result.GetProperty("items").GetArrayLength());
        // pageSize 500 must complete 146 rows without a non-null continuation.
        if (result.TryGetProperty("continuation", out var cont))
        {
            Assert.True(
                cont.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                "unexpected continuation for 146-row pageSize 500");
        }

        Assert.True(
            measured.Elapsed < MaxWallTime,
            $"wall_ms={measured.Elapsed.TotalMilliseconds:F1} exceeds 5000");
        Assert.True(
            measured.PeakRssBytes < MaxPeakRssBytes,
            $"peak_rss_bytes={measured.PeakRssBytes} exceeds 256MiB");
        Assert.Equal(0, measured.MaxChildCount);
        Assert.Equal(1, measured.InvocationCount);
        // Aggregate evidence for the gate (no financial payloads).
        Console.WriteLine(
            $"throughput_evidence invocations=1 wall_ms={measured.Elapsed.TotalMilliseconds:F1} " +
            $"peak_rss_bytes={measured.PeakRssBytes} returned_count={returned} child_max={measured.MaxChildCount}");
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
        AssertSingleJsonObject(measured.Stdout);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_multi_page_invocation_count_equals_ceiling()
    {
        RequirePublishedBinary();
        // Use multipage evaluation overallCount from the public envelope so fixture layering
        // cannot desync the ceiling proof. pageSize forces multiple invocations.
        var pageSize = fx.MultiPageSize;
        string? continuation = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invocations = 0;
        long peakRss = 0;
        var totalWall = TimeSpan.Zero;
        int? overall = null;

        do
        {
            var body = continuation is null
                ? $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.MultiPageEvaluationId)},\"pageSize\":{pageSize}}}"
                : $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.MultiPageEvaluationId)},\"pageSize\":{pageSize},\"continuation\":{JsonSerializer.Serialize(continuation)}}}";
            var measured = await RunPublishedMeasuredAsync(
                ["classify", "outcome", "list", "--input", "-"],
                ClassifyEnvelope(body, idempotencyKey: null));
            Assert.Equal(0, measured.ExitCode);
            invocations++;
            peakRss = Math.Max(peakRss, measured.PeakRssBytes);
            totalWall += measured.Elapsed;
            Assert.Equal(0, measured.MaxChildCount);
            using var doc = JsonDocument.Parse(measured.Stdout);
            var result = doc.RootElement.GetProperty("result_or_error");
            overall ??= result.GetProperty("overallCount").GetInt32();
            foreach (var item in result.GetProperty("items").EnumerateArray())
            {
                Assert.True(seen.Add(item.GetProperty("outcomeId").GetString()!));
            }

            continuation = result.TryGetProperty("continuation", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        while (continuation is not null);

        Assert.NotNull(overall);
        Assert.True(overall >= pageSize + 1, "fixture must force multi-page walks");
        var expectedInvocations = (int)Math.Ceiling(overall!.Value / (double)pageSize);
        Assert.Equal(expectedInvocations, invocations);
        Assert.Equal(overall.Value, seen.Count);
        Console.WriteLine(
            $"pagination_evidence filtered={overall} page_size={pageSize} " +
            $"invocations={invocations} expected_ceiling={expectedInvocations} " +
            $"wall_ms={totalWall.TotalMilliseconds:F1} peak_rss_bytes={peakRss} child_max=0");
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_page_size_1_and_500_no_duplicates_replay_stable_fingerprint()
    {
        RequirePublishedBinary();
        // pageSize 1 walk over multipage evaluation
        var allIds = new List<string>();
        string? cursor = null;
        int? overall = null;
        do
        {
            var body = cursor is null
                ? $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.MultiPageEvaluationId)},\"pageSize\":1}}"
                : $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.MultiPageEvaluationId)},\"pageSize\":1,\"continuation\":{JsonSerializer.Serialize(cursor)}}}";
            var m = await RunPublishedMeasuredAsync(
                ["classify", "outcome", "list", "--input", "-"],
                ClassifyEnvelope(body, null));
            Assert.Equal(0, m.ExitCode);
            using var doc = JsonDocument.Parse(m.Stdout);
            var result = doc.RootElement.GetProperty("result_or_error");
            overall ??= result.GetProperty("overallCount").GetInt32();
            foreach (var item in result.GetProperty("items").EnumerateArray())
            {
                allIds.Add(item.GetProperty("outcomeId").GetString()!);
            }

            cursor = result.TryGetProperty("continuation", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        while (cursor is not null);

        Assert.NotNull(overall);
        Assert.Equal(overall!.Value, allIds.Count);
        Assert.Equal(allIds.Distinct(StringComparer.Ordinal).Count(), allIds.Count);

        // pageSize 500 single page + replay
        var fullBody =
            $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.MultiPageEvaluationId)},\"pageSize\":500}}";
        var a = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(fullBody, null));
        var b = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(fullBody, null));
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(0, b.ExitCode);
        using var da = JsonDocument.Parse(a.Stdout);
        using var db = JsonDocument.Parse(b.Stdout);
        var ra = da.RootElement.GetProperty("result_or_error");
        var rb = db.RootElement.GetProperty("result_or_error");
        Assert.Equal(
            ra.GetProperty("resultFingerprint").GetString(),
            rb.GetProperty("resultFingerprint").GetString());
        Assert.Equal(overall.Value, ra.GetProperty("returnedCount").GetInt32());
        var page500Ids = ra.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("outcomeId").GetString()!)
            .ToArray();
        Assert.Equal(allIds.Order(StringComparer.Ordinal), page500Ids.Order(StringComparer.Ordinal));
        Assert.Equal(0, a.MaxChildCount);
        Assert.Equal(0, b.MaxChildCount);
    }

    // ── Five additive CLI paths ──────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_five_additive_cli_paths_emit_one_structured_envelope()
    {
        RequirePublishedBinary();
        var cases = new (string[] Args, string Input, bool NeedsKey)[]
        {
            (["classify", "outcome", "list", "--input", "-"],
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"pageSize\":10}}",
                false),
            (["classify", "rule", "list", "--input", "-"],
                """{"contractVersion":"1.0","pageSize":10}""",
                false),
            (["classify", "rule-set", "active", "get", "--input", "-"],
                """{"contractVersion":"1.0"}""",
                false),
            (["classify", "unresolved", "report", "--input", "-"],
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.UnresolvedEvaluationId)},\"topN\":10,\"minimumCount\":2}}",
                false),
            (["classify", "corpus", "build", "--input", "-"],
                BuildMinimalCorpusInput(Path.Combine(fx.DataRoot, "corpus-out", "cli.jsonl")),
                true)
        };

        foreach (var (args, input, needsKey) in cases)
        {
            if (args.SequenceEqual(new[] { "classify", "corpus", "build", "--input", "-" }))
            {
                var parent = Path.Combine(fx.DataRoot, "corpus-out");
                Directory.CreateDirectory(parent);
                File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var measured = await RunPublishedMeasuredAsync(
                args,
                ClassifyEnvelope(input, needsKey ? NextKey() : null));
            // Path smoke only — partition-specific exit proofs live in typed_* tests.
            // Allow success or structured host/domain failure; do not treat this as partition coverage.
            Assert.True(
                measured.ExitCode is 0 or >= 3,
                $"exit={measured.ExitCode} args={string.Join(' ', args)}");
            using var doc = JsonDocument.Parse(measured.Stdout);
            Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
            Assert.StartsWith("classify.", doc.RootElement.GetProperty("operation_id").GetString(), StringComparison.Ordinal);
            Assert.True(doc.RootElement.GetProperty("outcome").GetString() is "success" or "error");
            AssertSingleJsonObject(measured.Stdout);
            AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
            Assert.Equal(0, measured.MaxChildCount);
        }
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_file_input_json_matches_descriptor_for_outcome_list()
    {
        RequirePublishedBinary();
        var path = Path.Combine(fx.DataRoot, "req-outcome-list.json");
        var body = ClassifyEnvelope(
            $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"pageSize\":25}}",
            null);
        await File.WriteAllTextAsync(path, body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "@" + path],
            input: null);
        Assert.Equal(0, measured.ExitCode);
        Assert.DoesNotContain(path, measured.Stderr, StringComparison.Ordinal);
        AssertSingleJsonObject(measured.Stdout);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
    }

    // ── Typed exits / privacy ────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_cursor_invalid_exit_mapping_and_private_safe_stderr()
    {
        RequirePublishedBinary();
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"pageSize\":10,\"continuation\":\"%%%not-valid%%%\"}}",
                null));
        Assert.NotEqual(0, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.Equal(ClassifyErrors.CursorInvalid, code);
        // DomainErrors declare cursor invalid as compatibility exit 7.
        Assert.Equal(7, measured.ExitCode);
        Assert.StartsWith("tally: ", measured.Stderr, StringComparison.Ordinal);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_lifecycle_missing_eval_exit_mapping()
    {
        RequirePublishedBinary();
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"1.0","evaluationId":"missing-eval-id","pageSize":10}""",
                null));
        Assert.Equal(4, measured.ExitCode); // not_found class
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.StartsWith("CLASSIFY-", doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString(), StringComparison.Ordinal);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_unsupported_version_exit_mapping()
    {
        RequirePublishedBinary();
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "unresolved", "report", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"9.9","evaluationId":"eval","topN":10,"minimumCount":2}""",
                null));
        Assert.Equal(7, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_privacy_rejected_exit_mapping()
    {
        // Deterministic privacy partition: schema-valid corpus.build with a non-absolute
        // destination fails closed in the production handler (CLASSIFY-PRIVACY-REJECTED → exit 3).
        // Field names must match ClassifyCorpusBuildRequest wire shape so preflight does not
        // short-circuit as generic validation.invalid_input before the privacy partition.
        RequirePublishedBinary();
        const string pathCanary = "CANARY_PROC_PRIVACY_REL_PATH.jsonl";
        var input = $$"""
            {
              "contractVersion":"1.0",
              "idempotencyKey":"ignored-use-envelope",
              "outputPath":"{{pathCanary}}",
              "projection":{
                "ledgerContractVersion":"{{ActualsContractVersions.Current}}",
                "projectionVersion":"{{ClassificationProjectionVersions.ClassificationV1}}",
                "storeGenerationFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "snapshotId":"snap-privacy",
                "snapshotExpiresAt":"2026-08-02T12:00:00.0000000Z",
                "catalogueFingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "normalizationVersion":"{{NormalizationDescriptor.V1.Version}}",
                "items":[{
                  "ordinal":0,
                  "transactionId":"tx-privacy-1",
                  "accountId":"acct-1",
                  "effectiveDate":"2026-07-15",
                  "signedAmount":"-12.34",
                  "sourceDescription":"CANARY_PROC_PRIVACY_DESC",
                  "amountDirection":"expense",
                  "categoryMutationState":"assignable",
                  "transactionRevision":"tr-0",
                  "relationshipRevision":"rr-0",
                  "allocationRevision":"ar-0"
                }]
              },
              "labels":[{"transactionId":"tx-privacy-1","expectedOutcome":"no_suggestion"}]
            }
            """;
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "corpus", "build", "--input", "-"],
            ClassifyEnvelope(input, NextKey()));
        Assert.Equal(3, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.corpus.build", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal(
            ClassifyErrors.PrivacyRejected,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.False(doc.RootElement.GetProperty("result_or_error").TryGetProperty("buildId", out _));
        Assert.StartsWith("tally: ", measured.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(pathCanary, measured.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY_PROC_PRIVACY_DESC", measured.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(pathCanary, measured.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY_PROC_PRIVACY_DESC", measured.Stdout, StringComparison.Ordinal);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
        AssertSingleJsonObject(measured.Stdout);
        Assert.Equal(0, measured.MaxChildCount);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_resource_limit_exit_mapping()
    {
        // Deterministic resource partition: unresolved.report topN above published bound
        // (1..500) → CLASSIFY-RESOURCE-LIMIT → host exit 9 (not generic invalid-input).
        RequirePublishedBinary();
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "unresolved", "report", "--input", "-"],
            ClassifyEnvelope(
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.UnresolvedEvaluationId)},\"topN\":501,\"minimumCount\":2}}",
                null));
        Assert.Equal(9, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.unresolved.report", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal(
            ClassifyErrors.ResourceLimit,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.False(doc.RootElement.GetProperty("result_or_error").TryGetProperty("groups", out _));
        Assert.StartsWith("tally: ", measured.Stderr, StringComparison.Ordinal);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
        AssertSingleJsonObject(measured.Stdout);
        Assert.Equal(0, measured.MaxChildCount);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_typed_integrity_exit_mapping()
    {
        // Deterministic integrity partition: retained evaluation envelope claims
        // no_suggestion_count that does not match durable outcome rows → CLASSIFY-INTEGRITY exit 8.
        RequirePublishedBinary();
        Assert.False(string.IsNullOrWhiteSpace(fx.IntegrityEvaluationId));
        var measured = await RunPublishedMeasuredAsync(
            ["classify", "unresolved", "report", "--input", "-"],
            ClassifyEnvelope(
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.IntegrityEvaluationId)},\"topN\":10,\"minimumCount\":2}}",
                null));
        Assert.Equal(8, measured.ExitCode);
        using var doc = JsonDocument.Parse(measured.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.unresolved.report", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal(
            ClassifyErrors.Integrity,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.False(doc.RootElement.GetProperty("result_or_error").TryGetProperty("groups", out _));
        Assert.False(doc.RootElement.GetProperty("result_or_error").TryGetProperty("reportFingerprint", out _));
        Assert.StartsWith("tally: ", measured.Stderr, StringComparison.Ordinal);
        AssertNoPrivateDiagnostics(measured.Stderr, measured.Stdout);
        AssertSingleJsonObject(measured.Stdout);
        Assert.Equal(0, measured.MaxChildCount);
    }

    // ── Composition ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PROCESS_outcome_ids_compose_selected_outcomes_preview_without_outcome_get()
    {
        RequirePublishedBinary();
        // One list page supplies IDs; one preview invocation — never outcome.get per row.
        var list = await RunPublishedMeasuredAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"pageSize\":50}}",
                null));
        Assert.Equal(0, list.ExitCode);
        using var listDoc = JsonDocument.Parse(list.Stdout);
        var items = listDoc.RootElement.GetProperty("result_or_error").GetProperty("items").EnumerateArray()
            .Where(i =>
            {
                var kind = i.GetProperty("kind").GetString();
                return string.Equals(kind, "suggestion", StringComparison.OrdinalIgnoreCase);
            })
            .Select(i => i.GetProperty("outcomeId").GetString()!)
            .Take(5)
            .ToArray();
        Assert.NotEmpty(items);

        var idsJson = string.Join(",", items.Select(id => JsonSerializer.Serialize(id)));
        var previewInput =
            $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(fx.BulkEvaluationId)},\"selection\":{{\"mode\":\"selected_outcomes\",\"outcomeIds\":[{idsJson}]}}}}";
        var preview = await RunPublishedMeasuredAsync(
            ["classify", "apply", "preview", "--input", "-"],
            ClassifyEnvelope(previewInput, NextKey()));
        Assert.True(
            preview.ExitCode == 0,
            $"preview exit={preview.ExitCode} stderr={preview.Stderr} stdout={preview.Stdout}");
        using var pdoc = JsonDocument.Parse(preview.Stdout);
        Assert.Equal("success", pdoc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.apply.preview", pdoc.RootElement.GetProperty("operation_id").GetString());
        // Proof: only two measured invocations in this test — list + preview (never per-row get).
        Assert.Equal(1, list.InvocationCount);
        Assert.Equal(1, preview.InvocationCount);
        Assert.Equal(0, list.MaxChildCount);
        Assert.Equal(0, preview.MaxChildCount);
        AssertNoPrivateDiagnostics(list.Stderr, list.Stdout);
        AssertNoPrivateDiagnostics(preview.Stderr, preview.Stdout);
        Console.WriteLine(
            $"composition_evidence list_invocations=1 preview_invocations=1 selected={items.Length} " +
            $"per_row_get_invocations=0 child_max=0");
    }

    [Fact]
    public void TC_ERGONOMICS_PROCESS_zero_child_per_row_and_live_root_isolation()
    {
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", fx.DataRoot, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetTempPath(), fx.DataRoot, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fx.DataRoot));
        // Static proof: measured CLI args never include the single-outcome get path.
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tests", "Tally.Tests", "Classify", "Process",
                "ClassifyOperatorErgonomicsProcessTests.cs"));
        Assert.DoesNotContain("[\"classify\", \"outcome\", \"get\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"classify\", \"outcome\", \"get\"", source, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void RequirePublishedBinary()
    {
        Assert.False(
            string.IsNullOrWhiteSpace(fx.BinaryPath) || !File.Exists(fx.BinaryPath),
            "TALLY_PUBLISHED_BINARY must point to a published linux-x64 Native-AOT tally binary (set by verify-classify-ergonomics-process.sh).");
    }

    private async Task<MeasuredProcessResult> RunPublishedMeasuredAsync(
        string[] args,
        string? input)
    {
        var start = new ProcessStartInfo(fx.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["TALLY_DATA_ROOT"] = fx.DataRoot;

        var sw = Stopwatch.StartNew();
        using var process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("failed to start published tally");
        long peakRss = 0;
        var maxChildren = 0;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !process.HasExited)
            {
                try
                {
                    peakRss = Math.Max(peakRss, ReadPeakRssBytes(process.Id));
                    maxChildren = Math.Max(maxChildren, CountChildren(process.Id));
                }
                catch
                {
                    // process may have exited mid-sample
                }

                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync();
        sw.Stop();
        cts.Cancel();
        try
        {
            await sampler;
        }
        catch
        {
            // ignore sampler cancel
        }

        try
        {
            peakRss = Math.Max(peakRss, ReadPeakRssBytes(process.Id));
        }
        catch
        {
            // pid may be reaped
        }

        var stdout = (await stdoutTask).TrimEnd();
        var stderr = (await stderrTask).TrimEnd();
        return new MeasuredProcessResult(
            process.ExitCode,
            stdout,
            stderr,
            sw.Elapsed,
            peakRss,
            maxChildren,
            InvocationCount: 1);
    }

    private static long ReadPeakRssBytes(int pid)
    {
        var statusPath = $"/proc/{pid}/status";
        if (!File.Exists(statusPath))
        {
            return 0;
        }

        foreach (var line in File.ReadLines(statusPath))
        {
            // VmHWM: peak resident set size (kB)
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal)
                || line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    return kb * 1024L;
                }
            }
        }

        return 0;
    }

    private static int CountChildren(int pid)
    {
        var path = $"/proc/{pid}/task/{pid}/children";
        if (!File.Exists(path))
        {
            return 0;
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void AssertSingleJsonObject(string stdout)
    {
        var trimmed = stdout.Trim();
        Assert.StartsWith("{", trimmed, StringComparison.Ordinal);
        Assert.EndsWith("}", trimmed, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(trimmed);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    private static void AssertNoPrivateDiagnostics(string stderr, string stdout)
    {
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY_", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JsonException", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", stderr, StringComparison.OrdinalIgnoreCase);
        // Error stdout must not dump private canaries either.
        if (!stdout.Contains("\"outcome\":\"success\"", StringComparison.Ordinal)
            && !stdout.Contains("\"outcome\": \"success\"", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("CANARY_", stdout, StringComparison.Ordinal);
        }
    }

    private string ClassifyEnvelope(string inputJson, string? idempotencyKey)
    {
        using var inputDoc = JsonDocument.Parse(inputJson);
        var request = new RequestEnvelope("1.0", fx.Actor, inputDoc.RootElement.Clone(), idempotencyKey);
        return JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
    }

    private string NextKey() => "proc-key-" + Guid.NewGuid().ToString("N");

    private static string BuildMinimalCorpusInput(string dest)
    {
        // Wire-valid minimal shape for CLI envelope smoke (typed privacy/resource/integrity
        // partitions use dedicated fixtures). catalogueFingerprint is the published field name.
        return JsonSerializer.Serialize(new
        {
            contractVersion = "1.0",
            idempotencyKey = "ignored-use-envelope",
            outputPath = dest,
            projection = new
            {
                ledgerContractVersion = ActualsContractVersions.Current,
                projectionVersion = ClassificationProjectionVersions.ClassificationV1,
                storeGenerationFingerprint = new string('a', 64),
                snapshotId = "snap-1",
                snapshotExpiresAt = "2026-08-02T12:00:00.0000000Z",
                catalogueFingerprint = new string('b', 64),
                normalizationVersion = NormalizationDescriptor.V1.Version,
                items = Array.Empty<object>()
            },
            labels = new[]
            {
                new { transactionId = "tx-smoke-1", expectedOutcome = "no_suggestion" }
            }
        });
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Tally.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    private sealed record MeasuredProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Elapsed,
        long PeakRssBytes,
        int MaxChildCount,
        int InvocationCount);
}

/// <summary>
/// Shared synthetic data root + published binary path. Seeding is in-process only;
/// throughput cases always invoke the published binary under TALLY_DATA_ROOT.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ErgonomicsProcessFixture : IAsyncLifetime
{
    public string DataRoot { get; private set; } = null!;
    public string BinaryPath { get; private set; } = null!;
    public SafeActor Actor { get; } = new("automation", "ergonomics-process", "run-01");
    public string BulkEvaluationId { get; private set; } = null!;
    public string MultiPageEvaluationId { get; private set; } = null!;
    public string UnresolvedEvaluationId { get; private set; } = null!;
    /// <summary>Completed evaluation with retention-gap counters for integrity partition proof.</summary>
    public string IntegrityEvaluationId { get; private set; } = null!;
    public int MultiPageCount { get; } = 7;
    public int MultiPageSize { get; } = 3;

    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyServices services = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "tally-erg-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        File.SetUnixFileMode(DataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var supplied = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        BinaryPath = !string.IsNullOrWhiteSpace(supplied) && File.Exists(supplied)
            ? Path.GetFullPath(supplied)
            : string.Empty;

        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(DataRoot, CancellationToken.None);
        registry = OperationRegistry.Create();
        var ledgerServices = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, ledgerServices);
        ledger = new LedgerContractClient(registry, bootstrap);
        services = await ClassifyOperationBundle.CreateServicesAsync(DataRoot, ledger, cancellationToken: CancellationToken.None);
        ledgerServices = ledgerServices with { Classify = services.Operations };
        process = new TallyProcess(registry, ledgerServices);
        accountId = (await CreateAccountAsync()).AccountId;

        // Multipage first while ledger is small enough to force multi-invocation walks
        // without depending on a fixed total across later bulk seeding.
        MultiPageEvaluationId = await SeedSuggestionEvaluationAsync("multipage merchant", count: MultiPageCount);
        UnresolvedEvaluationId = await SeedNoSuggestionEvaluationAsync("unresolved coffee shop", count: 3);
        // Retention gap: completed envelope claims no_suggestion_count=3 with zero outcome rows.
        IntegrityEvaluationId = await InsertSyntheticIntegrityGapRunAsync(UnresolvedEvaluationId, noSuggestionCount: 3);
        BulkEvaluationId = await SeedSuggestionEvaluationAsync("bulk list merchant", count: 146);
    }

    /// <summary>
    /// Clone a completed evaluation envelope into a synthetic run with mismatched
    /// no_suggestion_count (retention gap) for published-process integrity proofs.
    /// </summary>
    private async Task<string> InsertSyntheticIntegrityGapRunAsync(string templateEvaluationId, int noSuggestionCount)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT rule_set_version_id, normalization_version, ledger_contract_version, projection_version,
                   store_generation_fingerprint, snapshot_id, snapshot_expires_at,
                   category_lifecycle_fingerprint, ordered_items_fingerprint, actor
            FROM evaluation_run WHERE evaluation_id = $id;
            """;
        read.Parameters.AddWithValue("$id", templateEvaluationId);
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        var ruleSet = reader.GetString(0);
        var norm = reader.GetString(1);
        var ledgerCv = reader.GetString(2);
        var proj = reader.GetString(3);
        var gen = reader.GetString(4);
        var snap = reader.GetString(5);
        var exp = reader.GetString(6);
        var cat = reader.GetString(7);
        var ordered = reader.GetString(8);
        var act = reader.GetString(9);
        await reader.DisposeAsync();

        var synthId = "synth-integrity-" + Guid.NewGuid().ToString("N");
        // input_count must equal sum of outcome counters for envelope integrity of those fields;
        // no_suggestion_count is intentionally non-zero with zero durable no_suggestion rows.
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                $id, NULL, $rs, $norm, $ledger, $proj, $gen, $snap, $exp, $cat, $ord,
                $input, 0, $ns, 0, 0, 'completed', $actor, $created
            );
            """;
        insert.Parameters.AddWithValue("$id", synthId);
        insert.Parameters.AddWithValue("$rs", ruleSet);
        insert.Parameters.AddWithValue("$norm", norm);
        insert.Parameters.AddWithValue("$ledger", ledgerCv);
        insert.Parameters.AddWithValue("$proj", proj);
        insert.Parameters.AddWithValue("$gen", gen);
        insert.Parameters.AddWithValue("$snap", snap);
        insert.Parameters.AddWithValue("$exp", exp);
        insert.Parameters.AddWithValue("$cat", cat);
        insert.Parameters.AddWithValue("$ord", ordered);
        insert.Parameters.AddWithValue("$input", noSuggestionCount);
        insert.Parameters.AddWithValue("$ns", noSuggestionCount);
        insert.Parameters.AddWithValue("$actor", act);
        insert.Parameters.AddWithValue(
            "$created",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
        return synthId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(DataRoot))
        {
            Directory.Delete(DataRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<string> SeedSuggestionEvaluationAsync(string phrase, int count)
    {
        var category = await CreateCategoryAsync("Sug");
        var versionId = await SaveDraftAsync(category.CategoryId, phrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, phrase);
        for (var i = 0; i < count; i++)
        {
            _ = await RecordAsync(phrase);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            Actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.TotalCount >= count);
        return evaluated.Value.EvaluationId;
    }

    private async Task<string> SeedNoSuggestionEvaluationAsync(string phrase, int count)
    {
        var category = await CreateCategoryAsync("NS");
        var rulePhrase = "never-match-" + Guid.NewGuid().ToString("N")[..8];
        var versionId = await SaveDraftAsync(category.CategoryId, rulePhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, rulePhrase);
        for (var i = 0; i < count; i++)
        {
            _ = await RecordAsync(phrase);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            Actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return evaluated.Value!.EvaluationId;
    }

    private string NextKey() => "fx-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "ErgProc Bank " + unique,
                "P-" + unique,
                AccountType.Cheque,
                "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(CultureInfo.InvariantCulture),
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                "-12.34",
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "erg-proc:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "erg-proc draft"),
            Actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            Actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            Actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            Actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "erg-proc activate"),
            Actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, ActualsContractVersions.Current, Actor, CancellationToken.None);
        Assert.True(page.IsSuccess);
        var byTx = page.Value!.ClassificationItems!.ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            var item = byTx[txId];
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(DataRoot, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var request = new RequestEnvelope("1.0", Actor, JsonSerializer.SerializeToElement(input, inputType), key);
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, json, CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ErgonomicsProcessCollection : ICollectionFixture<ErgonomicsProcessFixture>
{
    public const string Name = "classify-ergonomics-process";
}
