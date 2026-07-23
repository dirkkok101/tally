using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace Tally.Tests.Process;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class PublishedReconciliationScopeContractTests(PublishedTallyFixture fixture) : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-published-scope-" + Guid.NewGuid().ToString("N"));

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_registration_returns_a_completed_scope()
    {
        var registration = await SeedScopeAsync("success");

        Assert.Equal("completed", registration.Scope.GetProperty("status").GetString());
        Assert.Equal(registration.EvidenceId, Assert.Single(registration.Scope.GetProperty("evidenceIds").EnumerateArray()).GetString());
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_same_key_replay_returns_the_original_scope()
    {
        var seed = await SeedAccountAndStatementAsync("same-key");
        var first = await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:same-key", "same-key");
        var replay = await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:same-key", "same-key");

        Assert.Equal(first.Stdout, replay.Stdout);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_cross_key_replay_returns_the_original_scope()
    {
        var seed = await SeedAccountAndStatementAsync("cross-key");
        var first = await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:cross-key", "first-key");
        var replay = await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:cross-key", "second-key");

        Assert.Equal(first.Stdout, replay.Stdout);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_changed_membership_conflicts()
    {
        var seed = await SeedAccountAndStatementAsync("changed");
        await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:original", "changed-key");

        AssertError(
            await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:changed", "changed-key"),
            5,
            "LEDGER-IDEMPOTENCY-001");
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-RECONCILIATION-CONTRACT
    [Theory]
    [InlineData("receipt")]
    [InlineData("incomplete")]
    [InlineData("outside-period")]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_invalid_evidence_is_rejected_atomically(string scenario)
    {
        var accountId = await CreateAccountAsync("invalid-" + scenario);
        var evidenceId = scenario == "receipt"
            ? await RegisterEvidenceAsync("receipt", accountId, null, "invalid-" + scenario)
            : await RegisterEvidenceAsync("statement_row", accountId, scenario == "incomplete" ? null : "2026-08-01", "invalid-" + scenario);
        var expected = scenario == "receipt" ? "LEDGER-SCOPE-STATEMENT-EVIDENCE-REQUIRED"
            : scenario == "incomplete" ? "LEDGER-SCOPE-INCOMPLETE-OBSERVATION"
            : "LEDGER-SCOPE-ACCOUNT-DATE-CONFLICT";
        var expectedExitCode = scenario == "outside-period" ? 5 : 3;

        AssertError(await RegisterScopeAsync(accountId, [evidenceId], "statement:invalid", "invalid-" + scenario), expectedExitCode, expected);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, FR-LEDGER-RECONCILIATION-COVERAGE
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_published_scope_supports_a_following_candidates_query()
    {
        var registration = await SeedScopeAsync("candidates");
        var result = await RunAsync(
            ["ledger", "reconciliation", "candidates", "--input", "-"],
            Envelope($"{{\"evidenceId\":\"{registration.EvidenceId}\",\"scopeId\":\"{registration.Scope.GetProperty("scopeId").GetString()}\",\"policyId\":\"manual_review_projection\",\"policyVersion\":\"1.0\"}}"));

        AssertSuccess(result, "ledger.reconciliation.candidates");
    }

    public void Dispose()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }

    private async Task<(JsonElement Scope, string EvidenceId)> SeedScopeAsync(string suffix)
    {
        var seed = await SeedAccountAndStatementAsync(suffix);
        var result = await RegisterScopeAsync(seed.AccountId, [seed.EvidenceId], "statement:" + suffix, "scope-" + suffix);
        return (Result(result, "ledger.reconciliation.scope.register"), seed.EvidenceId);
    }

    private async Task<(string AccountId, string EvidenceId)> SeedAccountAndStatementAsync(string suffix)
    {
        var accountId = await CreateAccountAsync(suffix);
        return (accountId, await RegisterEvidenceAsync("statement_row", accountId, "2026-07-10", suffix));
    }

    private async Task<string> CreateAccountAsync(string suffix) => Result(await RunAsync(
        ["ledger", "account", "create", "--input", "-"],
        Envelope($"{{\"institutionName\":\"Bank\",\"displayName\":\"Scope {suffix}\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"***1234\",\"currencyCode\":\"ZAR\"}}", "account-" + suffix)), "ledger.account.create").GetProperty("accountId").GetString()!;

    private async Task<string> RegisterEvidenceAsync(string kind, string accountId, string? date, string suffix) => Result(await RunAsync(
        ["ledger", "evidence", "register", "--input", "-"],
        Envelope($"{{\"kind\":\"{kind}\",\"logicalIdentityDigest\":\"{new string('a', 64)}\",\"opaqueExternalReference\":\"statement:{suffix}\",\"contentFingerprint\":\"{new string('b', 64)}\",\"observation\":{Observation(accountId, date)}}}", "evidence-" + suffix)), "ledger.evidence.register").GetProperty("evidenceId").GetString()!;

    private static string Observation(string accountId, string? date) => date is null
        ? "null"
        : $"{{\"accountId\":\"{accountId}\",\"signedAmountMinor\":-1234,\"currencyCode\":\"ZAR\",\"transactionDate\":\"{date}\",\"postingDate\":null,\"instrumentId\":null,\"cardholderId\":null,\"descriptionFingerprint\":\"{new string('c', 64)}\"}}";

    private Task<PublishedTallyResult> RegisterScopeAsync(string accountId, IReadOnlyList<string> evidenceIds, string manifest, string key) => RunAsync(
        ["ledger", "reconciliation", "scope", "register", "--input", "-"],
        Envelope($"{{\"accountId\":\"{accountId}\",\"periodStart\":\"2026-07-01\",\"periodEnd\":\"2026-07-31\",\"manifestOpaqueReference\":\"{manifest}\",\"evidenceIds\":[{string.Join(',', evidenceIds.Select(id => JsonSerializer.Serialize(id)))}]}}", key));

    private Task<PublishedTallyResult> RunAsync(IReadOnlyList<string> arguments, string input) => fixture.RunAsync(dataRoot, arguments, input);

    private static string Envelope(string input, string? key = null) => "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"published-scope\"}"
        + (key is null ? string.Empty : ",\"idempotencyKey\":\"" + key + "\"") + ",\"input\":" + input + "}";

    private static JsonElement Result(PublishedTallyResult result, string operationId)
    {
        AssertSuccess(result, operationId);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
