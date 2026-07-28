using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Common;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Process;

/// <summary>
/// TC-BUDGET-STRUCTURED-INVOCATION-CONTRACT / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY
/// Process-level stdout/stderr/exit matrix for the published BUDGET surface:
/// version/error partitions, coherent-evidence binding, mutation preconditions.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetProcessContractTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-process-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        ledger = new LedgerContractClient(registry, bootstrap);
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        services = services with { Budget = budget.Operations };
        process = new TallyProcess(registry, services);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Error mapping partition ──────────────────────────────────────────────

    /// <summary>
    /// Registry-driven, mirroring <c>IngestErrorProcessTests.DeclaredIngestErrors</c>
    /// (tests/Tally.Tests/Process/IngestErrorProcessTests.cs:40-56): a hand-copied list drifts
    /// silently when a code is added to BUDGET's ErrorSchema without mapping it.
    /// </summary>
    public static TheoryData<string, int, string> DeclaredBudgetErrors
    {
        get
        {
            var data = new TheoryData<string, int, string>();
            var declared = OperationRegistry.Create().Descriptors
                .Where(descriptor => descriptor.OperationId.StartsWith("budget.", StringComparison.Ordinal))
                .SelectMany(descriptor => descriptor.DomainErrors ?? [])
                .DistinctBy(schema => schema.Code, StringComparer.Ordinal);
            foreach (var schema in declared)
            {
                data.Add(schema.Code, schema.ExitCode, schema.Category);
            }

            return data;
        }
    }

    [Fact]
    public void Registry_declares_budget_domain_errors()
    {
        // Guard the guard: an empty enumeration would turn the theory below into a no-op.
        // Floor is the registry-driven distinct-code count today (20): BudgetOperationModule's
        // six DomainErrors lists union to 20 unique codes — BudgetErrors.NotFound is declared in
        // BudgetErrors but never attached to any operation's DomainErrors, so it is legitimately
        // absent from this registry-driven enumeration (unlike the hand-copied list it replaces).
        Assert.True(DeclaredBudgetErrors.Count() >= 20);
    }

    [Theory]
    [MemberData(nameof(DeclaredBudgetErrors))]
    public void Declared_budget_errors_map_to_their_public_process_contract(string code, int exitCode, string category)
    {
        var mapper = typeof(TallyProcess).GetMethod("ErrorForHandler", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsType<ProcessResult>(mapper!.Invoke(null, [code]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
    }

    // ── Process envelope / validation partition ──────────────────────────────

    [Fact]
    public async Task Schema_list_includes_exactly_six_budget_operations()
    {
        var result = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var operations = document.RootElement.GetProperty("result").GetProperty("operations")
            .EnumerateArray()
            .Select(e => e.GetProperty("operationId").GetString()!)
            .Where(id => id.StartsWith("budget.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            global::Tally.Features.Budget.Contract.BudgetOperationIds.All.Order(StringComparer.Ordinal),
            operations);
    }

    [Fact]
    public async Task Malformed_json_is_a_validation_error()
    {
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            "{",
            CancellationToken.None);
        AssertError(result, 3, "validation.invalid_input");
    }

    [Fact]
    public async Task Unknown_fields_are_rejected_before_dispatch()
    {
        var body = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-process"},"input":{"contractVersion":"1.0","revisionId":"x","extra":"nope"}}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);
        AssertError(result, 3, "validation.invalid_input");
    }

    [Fact]
    public async Task Mutation_without_idempotency_is_rejected_before_effects()
    {
        var body = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-process"},"input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"plan"}}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);
        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountBudgetRowsAsync());
    }

    [Fact]
    public async Task Mutation_without_actor_is_rejected_before_effects()
    {
        var body = """
            {"contractVersion":"1.0","input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"plan"},"idempotencyKey":"k1"}
            """;
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);
        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountBudgetRowsAsync());
    }

    [Fact]
    public async Task Unsupported_input_contract_version_is_compatibility_failure()
    {
        var body = Envelope(
            """{"contractVersion":"9.9","revisionId":"01TESTREVISION000000000000"}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains(BudgetErrors.UnsupportedVersion, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revision_not_found_is_stable_not_found()
    {
        var body = Envelope(
            """{"contractVersion":"1.0","revisionId":"01NOTFOUNDREVISION0000000000"}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            body,
            CancellationToken.None);
        Assert.Equal(4, result.ExitCode);
        Assert.Contains(BudgetErrors.RevisionNotFound, result.Stdout, StringComparison.Ordinal);
    }

    // ── Happy-path / coherent-evidence partition ─────────────────────────────

    [Fact]
    public async Task Draft_create_activate_position_and_insights_emit_one_versioned_result_each()
    {
        var categoryId = await CreateCategoryAsync("Groceries");
        var draftBody = Envelope(
            $$"""
            {"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{categoryId}}","plannedMinorUnits":12500}],"reason":"july-plan"}
            """,
            NextKey());
        var draft = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            draftBody,
            CancellationToken.None);
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);
        using var draftDoc = JsonDocument.Parse(draft.Stdout);
        var revisionId = draftDoc.RootElement.GetProperty("result").GetProperty("revision").GetProperty("revisionId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(revisionId));

        var getBody = Envelope(
            $$"""{"contractVersion":"1.0","revisionId":"{{revisionId}}"}""",
            idempotencyKey: null);
        var get = await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            getBody,
            CancellationToken.None);
        AssertSuccess(get, BudgetOperationIds.RevisionGet);

        var listBody = Envelope(
            """{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
            idempotencyKey: null);
        var list = await process.RunAsync(
            ["budget", "plan", "revision", "list", "--input", "-"],
            listBody,
            CancellationToken.None);
        AssertSuccess(list, BudgetOperationIds.RevisionList);

        var activateBody = Envelope(
            $$"""{"contractVersion":"1.0","revisionId":"{{revisionId}}","reason":"go-live"}""",
            NextKey());
        var activate = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            activateBody,
            CancellationToken.None);
        AssertSuccess(activate, BudgetOperationIds.RevisionActivate);

        var positionBody = Envelope(
            """{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
            idempotencyKey: null);
        var position = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            positionBody,
            CancellationToken.None);
        AssertSuccess(position, BudgetOperationIds.PositionGet);

        var evidenceBody = Envelope(
            """{"contractVersion":"1.0","budgetPeriod":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
            idempotencyKey: null);
        var evidence = await process.RunAsync(
            ["budget", "insights", "evidence", "get", "--input", "-"],
            evidenceBody,
            CancellationToken.None);
        AssertSuccess(evidence, BudgetOperationIds.InsightsEvidenceGet);
        using var evidenceDoc = JsonDocument.Parse(evidence.Stdout);
        var planState = evidenceDoc.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("planState").GetString();
        Assert.Equal("bound_revision", planState);
    }

    [Fact]
    public async Task Closed_period_draft_is_rejected_before_mutation()
    {
        var categoryId = await CreateCategoryAsync("ClosedCat");
        var body = Envelope(
            $$"""
            {"contractVersion":"1.0","period":{"year":2026,"month":6,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{categoryId}}","plannedMinorUnits":100}],"reason":"closed"}
            """,
            NextKey());
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(BudgetErrors.InvalidPeriod, result.Stdout, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string NextKey() => "budget-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture);

    private static string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-process\",\"runId\":\"run-01\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-process\",\"runId\":\"run-01\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";


    private async Task<string> CreateCategoryAsync(string name)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-process\"},\"idempotencyKey\":\""
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

    private async Task<long> CountBudgetRowsAsync()
    {
        var dbPath = Path.Combine(root, "budget", "budget.db");
        if (!File.Exists(dbPath))
        {
            return 0;
        }

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM budget_plan_revision;";
        var scalar = await command.ExecuteScalarAsync();
        return scalar is long value ? value : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static void AssertSuccess(ProcessResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
