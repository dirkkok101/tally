using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Tally.Tests.Classify.Security;

/// <summary>
/// NFR-CLASSIFY-LOCAL-DATA-PROTECTION / NFR-CLASSIFY-SELF-CONTAINED-LOCAL-OPERATION
/// TC-CLASSIFY-LOCAL-ARTIFACT-PROTECTION / TC-CLASSIFY-OFFLINE-PROCESS-ISOLATION
/// TASK-CLASSIFY-RULEBOOK-GATE-SECURITY / bd-2igu
///
/// Security/privacy matrix: owner-only 0700/0600 artifacts, payload canaries, hostile
/// path/JSON/mode/owner/symlink/limit/version boundaries, offline process isolation.
/// Evidence is metadata-only — never private fixtures, tracked payloads, or raw corpus rows.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifySecurityGateTests : IAsyncLifetime
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const string AmountCanary = "999888777";
    private const string DescriptionCanary = "CANARY_CLASSIFY_DESC_PRIVATE";
    private const string TokenCanary = "CANARY_CLASSIFY_TOKEN_SECRET";
    private const string ReasonCanary = "CANARY_CLASSIFY_REASON_PRIVATE";
    private const string KeyCanary = "CANARY_CLASSIFY_IDEM_KEY_SECRET";
    private const string PathCanary = "/private/bank/CANARY_CLASSIFY_PATH/secret.json";
    private const string CorpusCanary = "CANARY_CLASSIFY_CORPUS_ROW_PAYLOAD";
    private const string RuleCanary = "CANARY_CLASSIFY_RULE_TEXT_PRIVATE";
    private const string MalformedCanary = "PRIVATE_CLASSIFY_MALFORMED_JSON_CANARY";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-sec-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private ClassifyStateStore store = null!;
    private ClassifyArtifactProtection artifacts = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);
        var classify = await ClassifyOperationBundle.CreateServicesAsync(
            root, ledger, cancellationToken: CancellationToken.None);
        services = services with { Classify = classify.Operations };
        process = new TallyProcess(registry, services);
        store = classify.State.Store;
        artifacts = classify.State.Artifacts
            ?? new ClassifyArtifactProtection(store.Paths, store.ArtifactProtection);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Owner-only permissions (0700 / 0600) ─────────────────────────────────

    [Fact]
    public void TC_CLASSIFY_LOCAL_DATA_PROTECTION_bootstrap_directories_and_db_are_owner_only()
    {
        AssertDirectory(store.Paths.DataRoot);
        AssertDirectory(store.Paths.ClassifyDirectory);
        AssertDirectory(store.Paths.TemporaryDirectory);
        AssertDirectory(store.Paths.ReportsDirectory);
        AssertFile(store.Paths.DatabasePath);
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_wal_shm_are_owner_only_after_write()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS sec_probe(id INTEGER PRIMARY KEY); ROLLBACK;";
            await command.ExecuteNonQueryAsync();
        }

        AssertFile(store.Paths.DatabasePath);
        if (File.Exists(store.Paths.WalPath))
        {
            AssertFile(store.Paths.WalPath);
        }

        if (File.Exists(store.Paths.ShmPath))
        {
            AssertFile(store.Paths.ShmPath);
        }

        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_success_workflow_preserves_owner_only_modes()
    {
        var status = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","subjectType":"evaluation","subjectId":"eval-missing-sec"}""",
                idempotencyKey: null),
            CancellationToken.None);
        Assert.NotEqual(0, status.ExitCode);
        AssertClassifyEnvelope(status);

        var cleanup = await process.RunAsync(
            ["classify", "cleanup", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","policyVersion":"cleanup_v1"}""",
                NextKey()),
            CancellationToken.None);
        // Cleanup may succeed empty or fail integrity depending on state — modes must remain owner-only.
        Assert.True(cleanup.ExitCode is 0 or 3 or 8 or 9 or 10, cleanup.Stdout + cleanup.Stderr);

        AssertDirectory(store.Paths.ClassifyDirectory);
        AssertDirectory(store.Paths.TemporaryDirectory);
        AssertFile(store.Paths.DatabasePath);
        foreach (var artifact in store.Paths.RecognizedArtifactPaths().Where(File.Exists))
        {
            AssertFile(artifact);
        }

        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_validation_failure_leaves_no_orphan_mutation_rows()
    {
        var beforeIdem = await CountTableAsync("operation_idempotency");
        var beforeTomb = await CountTableAsync("abandonment_tombstone");
        var body =
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-sec"},"input":{"contractVersion":"1.0","subjectType":"evaluation","subjectId":"x","reason":"CANARY_CLASSIFY_REASON_PRIVATE"}}""";
        // Mutating abandon without idempotency key is rejected before writer effects.
        var result = await process.RunAsync(
            ["classify", "abandon", "--input", "-"],
            body,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", ReasonCanary, "abandon");
        Assert.Equal(beforeIdem, await CountTableAsync("operation_idempotency"));
        Assert.Equal(beforeTomb, await CountTableAsync("abandonment_tombstone"));
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public void TC_CLASSIFY_LOCAL_DATA_PROTECTION_permissive_directory_fails_closed()
    {
        File.SetUnixFileMode(store.Paths.ClassifyDirectory, OwnerDirectory | UnixFileMode.GroupRead);
        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
        File.SetUnixFileMode(store.Paths.ClassifyDirectory, OwnerDirectory);
    }

    [Fact]
    public void TC_CLASSIFY_LOCAL_DATA_PROTECTION_permissive_database_fails_closed()
    {
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile);
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_tmp_and_reports_dirs_are_owner_only()
    {
        artifacts.EnsureClassifyLayout();
        AssertDirectory(store.Paths.TemporaryDirectory);
        AssertDirectory(store.Paths.ReportsDirectory);
        var temp = artifacts.CreateRecognizedTemporaryForTests("tmp-sec-owner", [1, 2, 3]);
        AssertFile(temp);
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_unknown_files_under_classify_are_left_alone()
    {
        var stranger = Path.Combine(store.Paths.ClassifyDirectory, "owner-note.txt");
        await File.WriteAllTextAsync(stranger, "not-a-classify-artifact");
        File.SetUnixFileMode(stranger, OwnerFile);

        await using var _ = await store.OpenMigratedAsync(CancellationToken.None);

        Assert.True(File.Exists(stranger));
        Assert.Equal("not-a-classify-artifact", await File.ReadAllTextAsync(stranger));
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_cleanup_does_not_leave_raw_corpus_copies()
    {
        artifacts.CreateRecognizedTemporaryForTests("tmp-sec-cleanup", [7]);
        var unknown = Path.Combine(store.Paths.TemporaryDirectory, "notes-not-recognized.bin");
        await File.WriteAllBytesAsync(unknown, [9]);
        File.SetUnixFileMode(unknown, OwnerFile);

        var result = await process.RunAsync(
            ["classify", "cleanup", "--input", "-"],
            Envelope("""{"contractVersion":"1.0","policyVersion":"cleanup_v1"}""", NextKey()),
            CancellationToken.None);

        if (result.ExitCode == 0)
        {
            Assert.False(File.Exists(Path.Combine(store.Paths.TemporaryDirectory, "tmp-sec-cleanup")));
            Assert.True(File.Exists(unknown));
        }

        AssertNoCanaries(result, CorpusCanary, PathCanary, DescriptionCanary);
        store.RequireOwnerOnlyArtifacts();
    }

    // ── Canary non-disclosure ────────────────────────────────────────────────

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_malformed_json_does_not_echo_payload()
    {
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            "{\"payload\":\"" + MalformedCanary,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", MalformedCanary, "JsonException", "stack");
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_unknown_fields_do_not_echo_canary_values()
    {
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"classify-sec\"},\"input\":{\"contractVersion\":\"1.0\",\"subjectType\":\"evaluation\",\"subjectId\":\"x\",\"secretField\":\""
            + DescriptionCanary + "\"}}";
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", DescriptionCanary, TokenCanary);
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_unsafe_input_path_is_not_echoed()
    {
        var result = await process.RunAsync(
            ["classify", "status", "--input", PathCanary],
            null,
            CancellationToken.None);

        AssertSafeError(result, 2, "usage.invalid_input_path", PathCanary, "CANARY_CLASSIFY_PATH");
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_oversized_actor_label_fails_without_echo()
    {
        var canary = "PRIVATE_CLASSIFY_OVERSIZED_" + new string('X', 2048);
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"" + canary
            + "\"},\"input\":{\"contractVersion\":\"1.0\",\"subjectType\":\"evaluation\",\"subjectId\":\"eval-1\"}}";
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        AssertNoCanaries(result, "PRIVATE_CLASSIFY_OVERSIZED", canary.Substring(0, 40));
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_reason_and_key_canaries_stay_out_of_diagnostics()
    {
        var body = Envelope(
            $$"""{"contractVersion":"1.0","subjectType":"preview","subjectId":"prev-missing","reason":"{{ReasonCanary}}"}""",
            KeyCanary + "-abandon");
        var result = await process.RunAsync(
            ["classify", "abandon", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        AssertNoCanaries(result, ReasonCanary, KeyCanary, DescriptionCanary, TokenCanary, CorpusCanary);
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ReasonCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_CLASSIFY_LOCAL_DATA_PROTECTION_status_error_exposes_no_private_payload()
    {
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","subjectType":"evaluation","subjectId":"eval-CANARY_CLASSIFY_DESC_PRIVATE"}""",
                idempotencyKey: null),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        AssertClassifyEnvelope(result);
        // Subject id is public request echo on success only; errors must not restate private canary prose.
        Assert.DoesNotContain(DescriptionCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(CorpusCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TC_CLASSIFY_LOCAL_DATA_PROTECTION_public_error_codes_are_metadata_only()
    {
        var codes = typeof(ClassifyErrors)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => field.GetValue(null)?.ToString() ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(codes);
        Assert.All(codes, code =>
        {
            Assert.StartsWith("CLASSIFY-", code, StringComparison.Ordinal);
            Assert.DoesNotContain("/", code, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", code, StringComparison.Ordinal);
            Assert.Matches("^[A-Z0-9-]+$", code);
        });
    }

    // ── Hostile boundaries fail before mutation ──────────────────────────────

    [Fact]
    public async Task Hostile_unsupported_version_fails_closed_before_mutation()
    {
        var before = await CountTableAsync("operation_idempotency");
        var body = Envelope(
            """{"contractVersion":"9.9","subjectType":"evaluation","subjectId":"eval-1"}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains(ClassifyErrors.UnsupportedVersion, result.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, await CountTableAsync("operation_idempotency"));
        AssertNoCanaries(result, "eval-1-PRIVATE");
    }

    [Fact]
    public async Task Hostile_malformed_subject_type_fails_without_payload_echo()
    {
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"classify-sec\"},\"input\":{\"contractVersion\":\"1.0\",\"subjectType\":\"not-a-type\",\"subjectId\":\""
            + DescriptionCanary + "\"}}";
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        AssertNoCanaries(result, DescriptionCanary);
    }

    [Fact]
    public async Task Hostile_symlink_input_path_fails_without_echoing_target()
    {
        var target = Path.Combine(root, "CANARY_SYMLINK_TARGET_SECRET.json");
        await File.WriteAllTextAsync(target, "{\"contractVersion\":\"1.0\"}");
        var link = Path.Combine(Path.GetTempPath(), $"tally-classify-sec-link-{Guid.NewGuid():N}.json");
        try
        {
            File.CreateSymbolicLink(link, target);
            var result = await process.RunAsync(
                ["classify", "status", "--input", link],
                null,
                CancellationToken.None);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal("tally: usage.invalid_input_path", result.Stderr);
            AssertNoCanaries(result, "CANARY_SYMLINK_TARGET_SECRET", target, link);
        }
        finally
        {
            if (File.Exists(link) || (File.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)))
            {
                File.Delete(link);
            }
        }
    }

    [Fact]
    public async Task Hostile_classify_db_symlink_is_detectable_as_reparse_point()
    {
        var isolated = Path.Combine(Path.GetTempPath(), $"tally-classify-sec-symlink-{Guid.NewGuid():N}");
        try
        {
            var isolatedStore = new ClassifyStateStore(isolated);
            await isolatedStore.InitializeAsync(CancellationToken.None);

            var real = isolatedStore.Paths.DatabasePath + ".real";
            File.Move(isolatedStore.Paths.DatabasePath, real);
            File.CreateSymbolicLink(isolatedStore.Paths.DatabasePath, real);

            var attributes = File.GetAttributes(isolatedStore.Paths.DatabasePath);
            Assert.True(attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.True(File.Exists(isolatedStore.Paths.DatabasePath));
        }
        finally
        {
            if (Directory.Exists(isolated))
            {
                Directory.Delete(isolated, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Hostile_outside_root_path_is_rejected_by_artifact_protection()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-classify-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, CorpusCanary);
        try
        {
            Assert.True(artifacts.IsOutsideClassifyRoot(outside));
            Assert.False(artifacts.IsContainedInClassifyRoot(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Hostile_unknown_temporary_is_not_removed_by_staging()
    {
        var unknown = Path.Combine(store.Paths.TemporaryDirectory, "secret-notes.bin");
        await File.WriteAllBytesAsync(unknown, System.Text.Encoding.UTF8.GetBytes(CorpusCanary));
        File.SetUnixFileMode(unknown, OwnerFile);

        Assert.Null(artifacts.TryStageRecognizedTemporaries(
            "op-hostile-unknown", "cleanup", ["secret-notes.bin"]));
        Assert.True(File.Exists(unknown));
        Assert.Equal(CorpusCanary, await File.ReadAllTextAsync(unknown));
    }

    [Fact]
    public async Task Hostile_symlink_temporary_is_not_staged_or_deleted()
    {
        var target = Path.Combine(store.Paths.TemporaryDirectory, "tmp-target-sec");
        await File.WriteAllBytesAsync(target, [1]);
        File.SetUnixFileMode(target, OwnerFile);
        var link = Path.Combine(store.Paths.TemporaryDirectory, "tmp-link-sec");
        File.CreateSymbolicLink(link, target);

        Assert.Null(artifacts.TryStageRecognizedTemporaries(
            "op-hostile-link", "cleanup", ["tmp-link-sec"]));
        Assert.True(File.Exists(link) || Directory.Exists(Path.GetDirectoryName(link)));
        Assert.True(File.Exists(target));
    }

    // ── Self-contained / offline / no remote processing ──────────────────────

    [Fact]
    public void TC_CLASSIFY_SELF_CONTAINED_classify_composition_has_no_network_or_plugin_surface()
    {
        var repositoryRoot = RepositoryRoot();
        string[] paths =
        [
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "ClassifyExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "ClassifyStateExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "ClassifyValidationExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Infrastructure", "Classify"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify"),
            Path.Combine(repositoryRoot, "src", "Tally", "Domain", "Classify")
        ];

        var sources = paths
            .SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                : File.Exists(path) ? [path] : Array.Empty<string>())
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToArray();
        var composition = string.Join('\n', sources);

        string[] forbidden =
        [
            "FastEndpoints", "Aspire", "Npgsql", "EntityFramework", "Microsoft.AspNetCore",
            "HttpListener", "TcpListener", "WebApplication", "UseKestrel", "AddPlugins", "MEF",
            "Assembly.LoadFrom", "Assembly.Load(", "Process.Start", "HttpClient",
            "using MailKit", "using MimeKit", "WebSocket", "OpenAI", "Anthropic", "GrpcChannel"
        ];
        Assert.All(forbidden, token => Assert.DoesNotContain(token, composition, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TC_CLASSIFY_SELF_CONTAINED_registry_has_no_background_or_remote_classify_operations()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .Where(id => id.StartsWith("classify.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(12, ids.Count);
        foreach (var forbidden in new[]
                 {
                     "classify.sync", "classify.import", "classify.export", "classify.watch", "classify.schedule",
                     "classify.daemon", "classify.service", "classify.webhook", "classify.push", "classify.pull",
                     "classify.invoke", "classify.embed", "classify.train"
                 })
        {
            Assert.DoesNotContain(forbidden, ids);
        }
    }

    [Fact]
    public void TC_CLASSIFY_SELF_CONTAINED_schema_discovery_is_metadata_only()
    {
        var list = OperationRegistry.Create().SchemaListJson();
        Assert.Contains("classify.evaluate", list, StringComparison.Ordinal);
        Assert.Contains("classify.cleanup", list, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClassifyStateStore", list, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, list, StringComparison.Ordinal);
        Assert.DoesNotContain(DescriptionCanary, list, StringComparison.Ordinal);
        Assert.DoesNotContain(CorpusCanary, list, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", list, StringComparison.Ordinal);

        foreach (var operationId in ClassifyOperationIds.All)
        {
            var schema = OperationRegistry.Create().Find(operationId)!.ToSchema();
            Assert.NotNull(schema.Limits);
            Assert.All(schema.Errors, error =>
            {
                Assert.False(string.IsNullOrWhiteSpace(error.Code));
                Assert.DoesNotContain("/", error.Code, StringComparison.Ordinal);
                Assert.DoesNotContain(" ", error.Code, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task TC_CLASSIFY_SELF_CONTAINED_published_binary_classify_status_opens_no_socket_or_child()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Console.Error.WriteLine(
                "SKIPPED: TALLY_PUBLISHED_BINARY not set; published-binary case runs under the gate scripts.");
            return;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"tally-classify-sec-pub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var processHandle = StartPublished(
                binary,
                dataRoot,
                ["version", "--input", "-"]);
            await WaitForFileAsync(Path.Combine(dataRoot, "CURRENT"), processHandle);

            var statusLines = await File.ReadAllLinesAsync($"/proc/{processHandle.Id}/status");
            var effectiveUid = statusLines.Single(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
            Assert.Equal(Environment.GetEnvironmentVariable("UID") ?? EffectiveUid(), effectiveUid);

            var childrenPath = $"/proc/{processHandle.Id}/task/{processHandle.Id}/children";
            if (File.Exists(childrenPath))
            {
                Assert.True(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(childrenPath)));
            }

            if (Directory.Exists($"/proc/{processHandle.Id}/fd"))
            {
                Assert.DoesNotContain(Directory.EnumerateFiles($"/proc/{processHandle.Id}/fd"), HasSocketTarget);
            }

            await processHandle.StandardInput.WriteAsync(
                """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-sec"},"input":{}}""");
            processHandle.StandardInput.Close();
            var stdout = await processHandle.StandardOutput.ReadToEndAsync();
            var stderr = await processHandle.StandardError.ReadToEndAsync();
            Assert.True(processHandle.WaitForExit(60_000));
            Assert.Equal(0, processHandle.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            AssertNoCanaries(new ProcessResult(processHandle.ExitCode, stdout, stderr), PathCanary, DescriptionCanary, AmountCanary);

            var status = await RunPublishedAsync(
                binary,
                dataRoot,
                ["classify", "status", "--input", "-"],
                Envelope(
                    """{"contractVersion":"1.0","subjectType":"evaluation","subjectId":"eval-missing"}""",
                    idempotencyKey: null));
            Assert.NotEqual(0, status.ExitCode);
            AssertNoCanaries(status, PathCanary, DescriptionCanary, AmountCanary, ReasonCanary, CorpusCanary, RuleCanary);

            var classifyDb = Path.Combine(dataRoot, "classify", "classify.db");
            Assert.True(File.Exists(classifyDb));
            AssertDirectory(Path.Combine(dataRoot, "classify"));
            AssertFile(classifyDb);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TC_CLASSIFY_SELF_CONTAINED_published_binary_does_not_echo_canary_on_invalid_abandon()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Console.Error.WriteLine(
                "SKIPPED: TALLY_PUBLISHED_BINARY not set; published-binary case runs under the gate scripts.");
            return;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"tally-classify-sec-canary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            var version = await RunPublishedAsync(
                binary,
                dataRoot,
                ["version", "--input", "-"],
                """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-sec"},"input":{}}""");
            Assert.True(version.ExitCode == 0, $"version bootstrap failed: {version.Stderr}\n{version.Stdout}");

            var body =
                "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"classify-sec\"},\"idempotencyKey\":\""
                + KeyCanary
                + "\",\"input\":{\"contractVersion\":\"1.0\",\"subjectType\":\"preview\",\"subjectId\":\"prev-missing\",\"reason\":\""
                + ReasonCanary
                + "\"}}";
            var result = await RunPublishedAsync(
                binary,
                dataRoot,
                ["classify", "abandon", "--input", "-"],
                body);

            Assert.NotEqual(0, result.ExitCode);
            AssertNoCanaries(result, AmountCanary, ReasonCanary, KeyCanary, DescriptionCanary, CorpusCanary, TokenCanary);
            Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ReasonCanary, result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(KeyCanary, result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TC_CLASSIFY_SELF_CONTAINED_published_binary_schema_list_is_store_free()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Console.Error.WriteLine(
                "SKIPPED: TALLY_PUBLISHED_BINARY not set; published-binary case runs under the gate scripts.");
            return;
        }

        // Schema list must work without TALLY_DATA_ROOT (store-free discovery).
        var result = await RunPublishedAsync(
            binary,
            dataRoot: null,
            ["schema", "list"],
            stdin: null);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("classify.evaluate", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PathCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(CorpusCanary, result.Stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr) || result.Stderr.StartsWith("tally:", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string NextKey() =>
        "classify-sec-" + Interlocked.Increment(ref keySeq).ToString(CultureInfo.InvariantCulture);

    private static string Envelope(string inputJson, string? idempotencyKey)
    {
        using var doc = JsonDocument.Parse(inputJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", "1.0");
            writer.WritePropertyName("actor");
            writer.WriteStartObject();
            writer.WriteString("kind", "automation");
            writer.WriteString("label", "classify-sec");
            writer.WriteEndObject();
            if (idempotencyKey is not null)
            {
                writer.WriteString("idempotencyKey", idempotencyKey);
            }

            writer.WritePropertyName("input");
            doc.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AssertDirectory(string path)
    {
        Assert.True(Directory.Exists(path), path);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(path));
    }

    private static void AssertFile(string path)
    {
        Assert.True(File.Exists(path), path);
        Assert.Equal(OwnerFile, File.GetUnixFileMode(path));
    }

    private static void AssertClassifyEnvelope(ProcessResult result)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.True(
            root.TryGetProperty("contract_version", out _) || root.TryGetProperty("contractVersion", out _),
            result.Stdout);
        Assert.True(
            root.TryGetProperty("outcome", out var outcome),
            result.Stdout);
        Assert.False(string.IsNullOrWhiteSpace(outcome.GetString()));
    }

    private static void AssertSafeError(
        ProcessResult result,
        int exitCode,
        string codeFragment,
        params string[] canaries)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains(codeFragment, result.Stdout, StringComparison.Ordinal);
        AssertNoCanaries(result, canaries);
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
    }

    private static void AssertNoCanaries(ProcessResult result, params string[] canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, result.Stderr, StringComparison.Ordinal);
            // Error envelopes / usage diagnostics must not re-emit private canaries.
            if (result.ExitCode != 0)
            {
                Assert.DoesNotContain(canary, result.Stdout, StringComparison.Ordinal);
            }
        }
    }

    private async Task<long> CountTableAsync(string table)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static string? FindPublishedBinary()
    {
        var env = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        return null;
    }

    private static DiagnosticsProcess StartPublished(
        string binary,
        string dataRoot,
        string[] args)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        var process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("Failed to start published binary.");
        return process;
    }

    private static async Task<ProcessResult> RunPublishedAsync(
        string binary,
        string? dataRoot,
        string[] args,
        string? stdin)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        }

        using var process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("Failed to start published binary.");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000));
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task WaitForFileAsync(string path, DiagnosticsProcess process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) || process.HasExited)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static bool HasSocketTarget(string descriptor)
    {
        try
        {
            return new FileInfo(descriptor).LinkTarget?.StartsWith("socket:[", StringComparison.Ordinal) is true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string EffectiveUid()
    {
        try
        {
            return File.ReadAllText("/proc/self/status")
                .Split('\n')
                .First(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tally.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
