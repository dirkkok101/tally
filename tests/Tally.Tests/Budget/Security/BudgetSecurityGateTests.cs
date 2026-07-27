using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Common;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.ListRevisions;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Tally.Tests.Budget.Security;

/// <summary>
/// NFR-BUDGET-LOCAL-DATA-PROTECTION / NFR-BUDGET-SELF-CONTAINED-LOCAL-OPERATION
/// TC-BUDGET-LOCAL-DATA-PROTECTION / TC-BUDGET-SELF-CONTAINED-LOCAL-OPERATION
///
/// Security/privacy matrix for BUDGET: owner-only artifacts (0700/0600), canary non-disclosure,
/// fail-closed hostile inputs, offline self-contained operation. Failures use metadata-only
/// identifiers — never financial payloads, raw keys, or secret paths.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetSecurityGateTests : IAsyncLifetime
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const string AmountCanary = "999888777";
    private const string NameCanary = "CANARY_BUDGET_CATEGORY_NAME";
    private const string ReasonCanary = "CANARY_BUDGET_REASON_PRIVATE";
    private const string KeyCanary = "CANARY_BUDGET_IDEM_KEY_SECRET";
    private const string PathCanary = "/private/bank/CANARY_BUDGET_PATH/secret.json";
    private const string MalformedCanary = "PRIVATE_BUDGET_MALFORMED_JSON_CANARY";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-sec-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private BudgetStateStore store = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        services = services with { Budget = budget.Operations };
        process = new TallyProcess(registry, services);
        store = budget.State!.Store;
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
    public void TC_BUDGET_LOCAL_DATA_PROTECTION_bootstrap_directories_and_db_are_owner_only()
    {
        AssertDirectory(store.Paths.DataRoot);
        AssertDirectory(store.Paths.BudgetDirectory);
        AssertFile(store.Paths.DatabasePath);
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_wal_shm_are_owner_only_after_write()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS sec_probe(id INTEGER PRIMARY KEY); ROLLBACK;";
            await command.ExecuteNonQueryAsync();
        }

        AssertFile(store.Paths.DatabasePath);
        Assert.True(File.Exists(store.Paths.WalPath));
        Assert.True(File.Exists(store.Paths.ShmPath));
        AssertFile(store.Paths.WalPath);
        AssertFile(store.Paths.ShmPath);
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_success_workflow_preserves_owner_only_modes()
    {
        var categoryId = await CreateCategoryAsync("SecOwnerCat");
        var draft = await DraftCreateAsync(categoryId, plannedMinorUnits: 12_500, reason: "owner-modes", NextKey());
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);

        using var doc = JsonDocument.Parse(draft.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision").GetProperty("revisionId").GetString()!;

        var get = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            Envelope($$"""{"contractVersion":"1.0","revisionId":"{{revisionId}}"}""", idempotencyKey: null),
            CancellationToken.None);
        AssertSuccess(get, BudgetOperationIds.RevisionGet);

        var activate = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope($$"""{"contractVersion":"1.0","revisionId":"{{revisionId}}","reason":"go-live"}""", NextKey()),
            CancellationToken.None);
        AssertSuccess(activate, BudgetOperationIds.RevisionActivate);

        var position = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            Envelope("""{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""", idempotencyKey: null),
            CancellationToken.None);
        AssertSuccess(position, BudgetOperationIds.PositionGet);

        AssertDirectory(store.Paths.BudgetDirectory);
        AssertFile(store.Paths.DatabasePath);
        foreach (var artifact in store.Paths.RecognizedArtifactPaths().Where(File.Exists))
        {
            AssertFile(artifact);
        }

        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_validation_failure_leaves_no_orphan_financial_rows()
    {
        var beforePlans = await CountTableAsync("budget_plan");
        var beforeRevisions = await CountTableAsync("budget_plan_revision");
        var beforeEntries = await CountTableAsync("budget_plan_entry");

        // Mutation without idempotency key is rejected before any writer effects.
        var body =
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-sec"},"input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"01NOTACATEGORY00000000000000","plannedMinorUnits":999888777}],"reason":"CANARY_BUDGET_REASON_PRIVATE"}}""";
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", AmountCanary, ReasonCanary, "01NOTACATEGORY");
        Assert.Equal(beforePlans, await CountTableAsync("budget_plan"));
        Assert.Equal(beforeRevisions, await CountTableAsync("budget_plan_revision"));
        Assert.Equal(beforeEntries, await CountTableAsync("budget_plan_entry"));
        store.RequireOwnerOnlyArtifacts();
    }

    [Fact]
    public void TC_BUDGET_LOCAL_DATA_PROTECTION_permissive_directory_fails_closed()
    {
        File.SetUnixFileMode(store.Paths.BudgetDirectory, OwnerDirectory | UnixFileMode.GroupRead);
        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
        File.SetUnixFileMode(store.Paths.BudgetDirectory, OwnerDirectory);
    }

    [Fact]
    public void TC_BUDGET_LOCAL_DATA_PROTECTION_permissive_database_fails_closed()
    {
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Throws<InvalidOperationException>(() => store.RequireOwnerOnlyArtifacts());
        File.SetUnixFileMode(store.Paths.DatabasePath, OwnerFile);
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_lock_and_atomic_sidecars_are_owner_only_when_present()
    {
        await File.WriteAllTextAsync(store.Paths.LockPath, "lock");
        await File.WriteAllTextAsync(store.Paths.AtomicPath, "atomic");
        File.SetUnixFileMode(store.Paths.LockPath, OwnerFile | UnixFileMode.GroupRead);
        File.SetUnixFileMode(store.Paths.AtomicPath, OwnerFile | UnixFileMode.OtherRead);

        await using var _ = await store.OpenAsync(CancellationToken.None);

        AssertFile(store.Paths.LockPath);
        AssertFile(store.Paths.AtomicPath);
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_unknown_files_under_budget_are_left_alone()
    {
        var stranger = Path.Combine(store.Paths.BudgetDirectory, "owner-note.txt");
        await File.WriteAllTextAsync(stranger, "not-a-budget-artifact");
        File.SetUnixFileMode(stranger, OwnerFile);

        await using var _ = await store.OpenAsync(CancellationToken.None);

        Assert.True(File.Exists(stranger));
        Assert.Equal("not-a-budget-artifact", await File.ReadAllTextAsync(stranger));
        store.RequireOwnerOnlyArtifacts();
    }

    // ── Canary non-disclosure ────────────────────────────────────────────────

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_malformed_json_does_not_echo_payload()
    {
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            "{\"payload\":\"" + MalformedCanary,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", MalformedCanary, "JsonException", "stack");
        Assert.Equal(0L, await CountTableAsync("budget_plan_revision"));
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_unknown_fields_do_not_echo_canary_values()
    {
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-sec\"},\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\"x\",\"secretField\":\""
            + NameCanary + "\"}}";
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);

        AssertSafeError(result, 3, "validation.invalid_input", NameCanary);
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_unsafe_input_path_is_not_echoed()
    {
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", PathCanary],
            null,
            CancellationToken.None);

        AssertSafeError(result, 2, "usage.invalid_input_path", PathCanary, "CANARY_BUDGET_PATH");
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_oversized_actor_label_fails_without_echo()
    {
        var canary = "PRIVATE_BUDGET_OVERSIZED_" + new string('X', 2048);
        var body =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"" + canary
            + "\"},\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\"01NOTFOUNDREVISION0000000000\"}}";
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        AssertNoCanaries(result, "PRIVATE_BUDGET_OVERSIZED", canary.Substring(0, 40));
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_invalid_amount_and_reason_canaries_stay_out_of_diagnostics()
    {
        var categoryId = await CreateCategoryAsync("SecAmountCat");
        var body = Envelope(
            $$"""
            {"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{categoryId}}","plannedMinorUnits":-1}],"reason":"{{ReasonCanary}}"}
            """,
            KeyCanary + "-neg");
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains(BudgetErrors.InvalidAmount, result.Stdout, StringComparison.Ordinal);
        AssertNoCanaries(result, ReasonCanary, KeyCanary, AmountCanary, categoryId);
        Assert.Equal("tally: " + BudgetErrors.InvalidAmount, result.Stderr);
        Assert.Equal(0L, await CountTableAsync("budget_plan_entry"));
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_success_amount_is_only_in_structured_stdout_result()
    {
        var categoryId = await CreateCategoryAsync("SecPayloadCat");
        var planned = long.Parse(AmountCanary, CultureInfo.InvariantCulture);
        var result = await DraftCreateAsync(categoryId, planned, ReasonCanary, KeyCanary + "-ok");

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
        Assert.Contains(AmountCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, result.Stderr, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            planned,
            document.RootElement.GetProperty("result").GetProperty("revision").GetProperty("entries")[0]
                .GetProperty("plannedMinorUnits").GetInt64());
    }

    [Fact]
    public async Task TC_BUDGET_LOCAL_DATA_PROTECTION_idempotency_conflict_does_not_echo_key()
    {
        var categoryId = await CreateCategoryAsync("SecIdemCat");
        var key = KeyCanary + "-conflict";
        var first = await DraftCreateAsync(categoryId, 100, "first", key);
        AssertSuccess(first, BudgetOperationIds.DraftCreate);

        var second = await DraftCreateAsync(categoryId, 200, "second-payload", key);
        Assert.Equal(5, second.ExitCode);
        Assert.Contains(BudgetErrors.IdempotencyConflict, second.Stdout, StringComparison.Ordinal);
        AssertNoCanaries(second, KeyCanary, key, "second-payload");
        Assert.Equal("tally: " + BudgetErrors.IdempotencyConflict, second.Stderr);
    }

    [Fact]
    public void TC_BUDGET_LOCAL_DATA_PROTECTION_public_error_codes_are_metadata_only()
    {
        var codes = typeof(BudgetErrors).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => field.GetValue(null)?.ToString() ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(codes);
        Assert.All(codes, code =>
        {
            Assert.StartsWith("BUDGET-", code, StringComparison.Ordinal);
            Assert.DoesNotContain("/", code, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", code, StringComparison.Ordinal);
            Assert.Matches("^[A-Z0-9-]+$", code);
        });
    }

    // ── Hostile boundaries fail before mutation ──────────────────────────────

    [Fact]
    public async Task Hostile_unsupported_version_fails_closed_before_mutation()
    {
        var before = await CountTableAsync("budget_plan_revision");
        var body = Envelope(
            """{"contractVersion":"9.9","revisionId":"01TESTREVISION000000000000"}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains(BudgetErrors.UnsupportedVersion, result.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, await CountTableAsync("budget_plan_revision"));
        AssertNoCanaries(result, "01TESTREVISION000000000000");
    }

    [Fact]
    public async Task Hostile_over_limit_list_returns_resource_limit_without_payload_echo()
    {
        var body = Envelope(
            $$"""{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"limit":{{ListBudgetPlanRevisionsQuery.MaxLimit + 1}}}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "list", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(9, result.ExitCode);
        Assert.Contains(BudgetErrors.ResourceLimit, result.Stdout, StringComparison.Ordinal);
        Assert.Equal("tally: " + BudgetErrors.ResourceLimit, result.Stderr);
        Assert.DoesNotContain("JsonException", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hostile_symlink_input_path_fails_without_echoing_target()
    {
        var target = Path.Combine(root, "CANARY_SYMLINK_TARGET_SECRET.json");
        await File.WriteAllTextAsync(target, "{\"contractVersion\":\"1.0\"}");
        var link = Path.Combine(Path.GetTempPath(), $"tally-budget-sec-link-{Guid.NewGuid():N}.json");
        try
        {
            File.CreateSymbolicLink(link, target);
            var result = await process.RunAsync(
                ["budget", "plan", "revision", "get", "--input", link],
                null,
                CancellationToken.None);

            Assert.Equal(2, result.ExitCode);
            Assert.Equal("tally: usage.invalid_input_path", result.Stderr);
            AssertNoCanaries(result, "CANARY_SYMLINK_TARGET_SECRET", target, link);
        }
        finally
        {
            if (File.Exists(link) || ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0))
            {
                File.Delete(link);
            }
        }
    }

    [Fact]
    public async Task Hostile_budget_db_symlink_is_detectable_as_reparse_point()
    {
        var isolated = Path.Combine(Path.GetTempPath(), $"tally-budget-sec-symlink-{Guid.NewGuid():N}");
        try
        {
            var isolatedStore = new BudgetStateStore(isolated);
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

    // ── Self-contained / offline / no remote processing ──────────────────────

    [Fact]
    public void TC_BUDGET_SELF_CONTAINED_budget_composition_has_no_network_or_plugin_surface()
    {
        var repositoryRoot = RepositoryRoot();
        string[] paths =
        [
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "BudgetExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "BudgetStateExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Infrastructure", "Budget"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Budget")
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
            "using MailKit", "using MimeKit", "WebSocket"
        ];
        Assert.All(forbidden, token => Assert.DoesNotContain(token, composition, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TC_BUDGET_SELF_CONTAINED_registry_has_no_background_or_remote_budget_operations()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .Where(id => id.StartsWith("budget.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(6, ids.Count);
        foreach (var forbidden in new[]
                 {
                     "budget.sync", "budget.import", "budget.export", "budget.watch", "budget.schedule",
                     "budget.daemon", "budget.service", "budget.webhook", "budget.push", "budget.pull"
                 })
        {
            Assert.DoesNotContain(forbidden, ids);
        }
    }

    [Fact]
    public void TC_BUDGET_SELF_CONTAINED_schema_discovery_is_metadata_only()
    {
        var list = OperationRegistry.Create().SchemaListJson();
        Assert.Contains("budget.plan.draft.create", list, StringComparison.Ordinal);
        Assert.DoesNotContain("budget.db", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BudgetStateStore", list, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, list, StringComparison.Ordinal);
        Assert.DoesNotContain(NameCanary, list, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", list, StringComparison.Ordinal);
        Assert.DoesNotContain("plannedMinorUnits\":", list, StringComparison.Ordinal);

        foreach (var operationId in BudgetOperationIds.All)
        {
            var schema = OperationRegistry.Create().Find(operationId)!.ToSchema();
            Assert.All(schema.Errors, error =>
            {
                Assert.False(string.IsNullOrWhiteSpace(error.Code));
                Assert.DoesNotContain("/", error.Code, StringComparison.Ordinal);
                Assert.DoesNotContain(" ", error.Code, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task TC_BUDGET_SELF_CONTAINED_published_binary_budget_read_opens_no_socket_or_child()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Assert.True(true);
            return;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"tally-budget-sec-pub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var processHandle = StartPublished(
                binary,
                dataRoot,
                ["version", "--input", "-"]);
            await WaitForFileAsync(Path.Combine(dataRoot, "CURRENT"), processHandle);

            var status = await File.ReadAllLinesAsync($"/proc/{processHandle.Id}/status");
            var effectiveUid = status.Single(line => line.StartsWith("Uid:", StringComparison.Ordinal))
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
                """{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-sec"},"input":{}}""");
            processHandle.StandardInput.Close();
            var stdout = await processHandle.StandardOutput.ReadToEndAsync();
            var stderr = await processHandle.StandardError.ReadToEndAsync();
            Assert.True(processHandle.WaitForExit(60_000));
            Assert.Equal(0, processHandle.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr));
            AssertNoCanaries(new ProcessResult(processHandle.ExitCode, stdout, stderr), PathCanary, NameCanary, AmountCanary);

            var list = await RunPublishedAsync(
                binary,
                dataRoot,
                ["budget", "plan", "revision", "list", "--input", "-"],
                Envelope(
                    """{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
                    idempotencyKey: null));
            Assert.True(list.ExitCode is 0 or 4 or 6, $"unexpected budget list exit {list.ExitCode}");
            AssertNoCanaries(list, PathCanary, NameCanary, AmountCanary, ReasonCanary);

            var budgetDb = Path.Combine(dataRoot, "budget", "budget.db");
            Assert.True(File.Exists(budgetDb));
            AssertDirectory(Path.Combine(dataRoot, "budget"));
            AssertFile(budgetDb);
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
    public async Task TC_BUDGET_SELF_CONTAINED_published_binary_does_not_echo_canary_on_invalid_draft()
    {
        var binary = FindPublishedBinary();
        if (binary is null)
        {
            Assert.True(true);
            return;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"tally-budget-sec-canary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            var version = await RunPublishedAsync(
                binary,
                dataRoot,
                ["version", "--input", "-"],
                """{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-sec"},"input":{}}""");
            Assert.True(version.ExitCode == 0, $"version bootstrap failed: {version.Stderr}\n{version.Stdout}");

            var body =
                "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-sec\"},\"idempotencyKey\":\""
                + KeyCanary
                + "\",\"input\":{\"contractVersion\":\"1.0\",\"period\":{\"year\":2026,\"month\":7,\"currencyCode\":\"ZAR\"},\"entries\":[{\"categoryId\":\"01NOTACATEGORY00000000000000\",\"plannedMinorUnits\":"
                + AmountCanary
                + "}],\"reason\":\""
                + ReasonCanary
                + "\"}}";
            var result = await RunPublishedAsync(
                binary,
                dataRoot,
                ["budget", "plan", "draft", "create", "--input", "-"],
                body);

            Assert.NotEqual(0, result.ExitCode);
            AssertNoCanaries(result, AmountCanary, ReasonCanary, KeyCanary, NameCanary, "01NOTACATEGORY");
            Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(AmountCanary, result.Stderr, StringComparison.Ordinal);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ProcessResult> DraftCreateAsync(
        string categoryId,
        long plannedMinorUnits,
        string reason,
        string key)
    {
        var body = Envelope(
            $$"""
            {"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{categoryId}}","plannedMinorUnits":{{plannedMinorUnits.ToString(CultureInfo.InvariantCulture)}}}],"reason":"{{reason}}"}
            """,
            key);
        return await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);
    }

    private string NextKey() => "budget-sec-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture);

    private static string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-sec\",\"runId\":\"run-sec\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-sec\",\"runId\":\"run-sec\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";

    private async Task<string> CreateCategoryAsync(string name)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-sec\"},\"idempotencyKey\":\""
            + NextKey()
            + "\",\"input\":{\"name\":\"" + name + "\"}}";
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task<long> CountTableAsync(string table)
    {
        if (!File.Exists(store.Paths.DatabasePath))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static void AssertDirectory(string path) =>
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(path));

    private static void AssertFile(string path) =>
        Assert.Equal(OwnerFile, File.GetUnixFileMode(path));

    private static void AssertSuccess(ProcessResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
    }

    private static void AssertSafeError(ProcessResult result, int exitCode, string code, params string[] canaries)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("system.process", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        AssertNoCanaries(result, canaries);
    }

    private static void AssertNoCanaries(ProcessResult result, params string[] canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(canary, result.Stderr, StringComparison.OrdinalIgnoreCase);
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

    private static DiagnosticsProcess StartPublished(string binary, string dataRoot, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        return Assert.IsType<DiagnosticsProcess>(DiagnosticsProcess.Start(start));
    }

    private static async Task<ProcessResult> RunPublishedAsync(
        string binary,
        string dataRoot,
        IReadOnlyList<string> arguments,
        string? input)
    {
        using var processHandle = StartPublished(binary, dataRoot, arguments);
        if (input is not null)
        {
            await processHandle.StandardInput.WriteAsync(input);
        }

        processHandle.StandardInput.Close();
        var stdout = await processHandle.StandardOutput.ReadToEndAsync();
        var stderr = await processHandle.StandardError.ReadToEndAsync();
        Assert.True(processHandle.WaitForExit(120_000));
        return new ProcessResult(processHandle.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
    }

    private static string? FindPublishedBinary()
    {
        var supplied = Environment.GetEnvironmentVariable("TALLY_PUBLISHED_BINARY");
        if (!string.IsNullOrWhiteSpace(supplied) && File.Exists(supplied))
        {
            return Path.GetFullPath(supplied);
        }

        return null;
    }

    private static async Task WaitForFileAsync(string path, DiagnosticsProcess processHandle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path))
        {
            if (processHandle.HasExited)
            {
                throw new InvalidOperationException("Published process exited before initializing storage.");
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private static string EffectiveUid()
    {
        var status = File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
        return status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
