using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace Tally.Tests.Process;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class PublishedLedgerContractTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-published-ledger-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TC_LEDGER_OFFLINE_SELF_CONTAINED_is_a_native_executable_with_embedded_sqlite()
    {
        Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], File.ReadAllBytes(fixture.BinaryPath)[..4]);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.BinaryPath)!, "libe_sqlite3.so")));
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(fixture.BinaryPath) & UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task TC_LEDGER_AGENT_CONTRACT_CONFORMANCE_publishes_exactly_73_provider_neutral_schemas()
    {
        var result = await Run(["schema", "list", "--input", "-"], EmptyRequest());

        AssertSuccess(result, "system.schema.list");
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(73, document.RootElement.GetProperty("result").GetProperty("operations").GetArrayLength());
        foreach (var forbidden in new[] { "mailbox", "mime", "recipient", "whatsapp", "providerCursor", "rawPayload", "statementDocument" })
        {
            Assert.DoesNotContain(forbidden, result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task FR_LEDGER_CONTRACT_DISCOVERY_schema_show_exposes_category_hierarchy()
    {
        var result = await Run(["schema", "show", "ledger.category.reparent", "--input", "-"], EmptyRequest());

        AssertSuccess(result, "system.schema.show");
        Assert.Contains("ReparentCategoryInput", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("CategoryReparentResult", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_LEDGER_OFFLINE_SELF_CONTAINED_version_succeeds_without_a_dotnet_runtime()
    {
        var result = await fixture.RunAsync(
            dataRoot,
            ["version", "--input", "-"],
            EmptyRequest(),
            new Dictionary<string, string?> { ["DOTNET_ROOT"] = "/definitely-not-a-runtime" });

        AssertSuccess(result, "system.version");
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_account_create_replay_returns_the_original_result()
    {
        var request = AccountRequest("account-replay", "Primary");

        var first = await Run(["ledger", "account", "create", "--input", "-"], request);
        var replay = await Run(["ledger", "account", "create", "--input", "-"], request);

        AssertSuccess(first, "ledger.account.create");
        Assert.Equal(first.Stdout, replay.Stdout);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_replay_is_a_stable_conflict()
    {
        AssertSuccess(
            await Run(["ledger", "account", "create", "--input", "-"], AccountRequest("account-conflict", "Primary")),
            "ledger.account.create");

        var conflict = await Run(
            ["ledger", "account", "create", "--input", "-"],
            AccountRequest("account-conflict", "Changed"));

        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task FR_LEDGER_ACCOUNT_MAINTENANCE_domain_conflict_has_one_safe_error_envelope()
    {
        AssertSuccess(
            await Run(["ledger", "account", "create", "--input", "-"], AccountRequest("account-first", "Duplicate")),
            "ledger.account.create");

        var duplicate = await Run(
            ["ledger", "account", "create", "--input", "-"],
            AccountRequest("account-second", "Duplicate"));

        AssertError(duplicate, 5, "LEDGER-ACCOUNT-DUPLICATE");
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_APPLY_unproven_automatic_action_is_review_required()
    {
        const string input = """
            {"evidenceId":"01J00000000000000000000000","evidenceFingerprint":"0000000000000000000000000000000000000000000000000000000000000000","scopeId":"01J00000000000000000000001","expectedProjectionToken":"1111111111111111111111111111111111111111111111111111111111111111","disposition":"record_exception","authorityKind":"deterministic_policy","reviewedCandidateIds":[],"exceptionCode":"missing","reason":"manual review"}
            """;
        var result = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            Envelope(input, "reconciliation-review"));

        AssertError(result, 8, "operation.review_required");
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_status_is_structured_and_payload_free()
    {
        var result = await Run(["ledger", "storage", "status", "--input", "-"], EmptyRequest());

        AssertSuccess(result, "ledger.storage.status");
        Assert.Contains("currentGenerationId", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("currentFingerprint", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("originalDescription", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedAmount", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR_LEDGER_SKILL_COMPATIBILITY_help_remains_complete_without_installed_guidance()
    {
        var result = await Run(["help"]);

        AssertSuccess(result, "system.schema.list");
        Assert.Contains("system.guidance.install", result.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, ".agents")));
        Assert.False(Directory.Exists(Path.Combine(dataRoot, ".claude")));
    }

    [Fact]
    public async Task TC_LEDGER_STRUCTURED_INVOCATION_rejects_incompatible_contract_before_mutation()
    {
        const string incompatible = "{\"contractVersion\":\"2.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"published-contract\"},\"idempotencyKey\":\"incompatible\",\"input\":{\"institutionName\":\"Bank\",\"displayName\":\"Never Written\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"***1234\",\"currencyCode\":\"ZAR\"}}";

        var result = await Run(["ledger", "account", "create", "--input", "-"], incompatible);

        AssertError(result, 3, "validation.invalid_input");
        var list = await Run(["ledger", "account", "list", "--input", "-"], Envelope("{}"));
        AssertSuccess(list, "ledger.account.list");
        using var document = JsonDocument.Parse(list.Stdout);
        Assert.Empty(document.RootElement.GetProperty("result").GetProperty("items").EnumerateArray());
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(dataRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        return Task.CompletedTask;
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) =>
        fixture.RunAsync(dataRoot, arguments, input);

    private static string EmptyRequest() => Envelope("{}");

    private static string AccountRequest(string key, string displayName) => Envelope(
        $"{{\"institutionName\":\"Bank\",\"displayName\":\"{displayName}\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"***1234\",\"currencyCode\":\"ZAR\"}}",
        key);

    private static string Envelope(string input, string? idempotencyKey = null) =>
        "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"published-contract\"}"
        + (idempotencyKey is null ? string.Empty : ",\"idempotencyKey\":\"" + idempotencyKey + "\"")
        + ",\"input\":" + input + "}";

    private static void AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        AssertEnvelope(result, operationId, "success");
        Assert.True(string.IsNullOrEmpty(result.Stderr));
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        AssertEnvelope(result, "system.process", "error", code);
        Assert.Equal("tally: " + code, result.Stderr);
    }

    private static void AssertEnvelope(PublishedTallyResult result, string operationId, string outcome, string? errorCode = null)
    {
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(outcome, document.RootElement.GetProperty("outcome").GetString());
        if (errorCode is not null) Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
