using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC001AccountWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc001-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UC_LEDGER_001_agent_discovers_the_concrete_account_contract()
    {
        var schema = await Success(
            ["schema", "show", "ledger.account.create", "--input", "-"],
            Envelope("{}"),
            "system.schema.show");
        var operation = schema.GetProperty("operation");

        Assert.Equal("ledger.account.create", operation.GetProperty("operationId").GetString());
        Assert.EndsWith("CreateAccountInput", operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("AccountDetail", operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
        Assert.True(operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        Assert.Contains(operation.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "LEDGER-ACCOUNT-DUPLICATE"
            && error.GetProperty("exitCode").GetInt32() == 5);
    }

    [Fact]
    public async Task UC_LEDGER_001_main_flow_preserves_identity_history_and_transaction_references()
    {
        var account = await CreateAccount("account-create", "Daily", "****1234");
        var accountId = account.GetProperty("accountId").GetString()!;
        var transaction = await RecordTransaction(accountId, "transaction-before-rename", 'a');
        var transactionId = transaction.GetProperty("transactionId").GetString()!;

        var renamed = await Success(
            ["ledger", "account", "rename", "--input", "-"],
            Envelope($"{{\"accountId\":\"{accountId}\",\"newDisplayName\":\"Household\",\"reason\":\"Owner clarified label\"}}", "account-rename"),
            "ledger.account.rename");
        var renamedAccount = renamed.GetProperty("account");
        Assert.Equal(accountId, renamedAccount.GetProperty("accountId").GetString());
        Assert.Equal("Household", renamedAccount.GetProperty("displayName").GetString());

        var referencedTransaction = await Success(
            ["ledger", "transaction", "get", "--input", "-"],
            Envelope($"{{\"transactionId\":\"{transactionId}\",\"includeHistory\":true}}"),
            "ledger.transaction.get");
        Assert.Equal(accountId, referencedTransaction.GetProperty("accountId").GetString());

        var archived = await Success(
            ["ledger", "account", "archive", "--input", "-"],
            Envelope($"{{\"accountId\":\"{accountId}\",\"reason\":\"Closed at bank\"}}", "account-archive"),
            "ledger.account.archive");
        Assert.Equal("archived", archived.GetProperty("account").GetProperty("status").GetString());

        var rejected = await Run(
            ["ledger", "transaction", "record", "--input", "-"],
            TransactionRequest(accountId, "archived-record", 'b'));
        AssertError(rejected, 6, "LEDGER-ACCOUNT-ARCHIVED");

        var replacementAccount = await CreateAccount("replacement-account", "Reserve", "****4321");
        var accepted = await Run(
            ["ledger", "transaction", "record", "--input", "-"],
            TransactionRequest(replacementAccount.GetProperty("accountId").GetString()!, "archived-record", 'b'));
        AssertSuccess(accepted, "ledger.transaction.record");

        var fetched = await Success(
            ["ledger", "account", "get", "--input", "-"],
            Envelope($"{{\"accountId\":\"{accountId}\",\"includeHistory\":true}}"),
            "ledger.account.get");
        Assert.Equal(accountId, fetched.GetProperty("accountId").GetString());
        Assert.Equal("****1234", fetched.GetProperty("maskedIdentifier").GetString());
        Assert.Equal(
            ["create", "rename", "archive"],
            fetched.GetProperty("lifecycleHistory").EnumerateArray()
                .Select(item => item.GetProperty("action").GetString()));

        var archivedList = await Success(
            ["ledger", "account", "list", "--input", "-"],
            Envelope("{\"status\":\"archived\"}"),
            "ledger.account.list");
        Assert.Equal(accountId, Assert.Single(archivedList.GetProperty("items").EnumerateArray()).GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task UC_LEDGER_001_missing_field_fails_without_consuming_the_idempotency_identity()
    {
        const string key = "missing-field-retry";
        var invalid = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope("{\"institutionName\":\"Bank\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"****1234\",\"currencyCode\":\"ZAR\"}", key));
        AssertError(invalid, 3, "validation.invalid_input");

        var corrected = await CreateAccount(key, "Daily", "****1234");

        Assert.Equal("Daily", corrected.GetProperty("displayName").GetString());
        Assert.Single((await ListAccounts("active")).EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_001_duplicate_masked_identity_is_stable_and_creates_no_second_account()
    {
        await CreateAccount("first-account", "Daily", "XXXX1234", "Example Bank");

        var duplicate = await Run(
            ["ledger", "account", "create", "--input", "-"],
            AccountRequest("second-account", "Reserve", "xxxx1234", "example bank"));

        AssertError(duplicate, 5, "LEDGER-ACCOUNT-DUPLICATE");
        Assert.Single((await ListAccounts("active")).EnumerateArray());
    }

    [Theory]
    [InlineData("maskedIdentifier", "123456789")]
    [InlineData("maskedIdentifier", "credential:1234")]
    [InlineData("onlineBankingPassword", "PRIVATE_PASSWORD_CANARY")]
    [InlineData("fullAccountNumber", "4111111111111111")]
    public async Task UC_LEDGER_001_credential_or_full_identifier_input_is_rejected_without_disclosure(
        string field,
        string privateValue)
    {
        var input = field == "maskedIdentifier"
            ? $"{{\"institutionName\":\"Bank\",\"displayName\":\"Daily\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"{privateValue}\",\"currencyCode\":\"ZAR\"}}"
            : $"{{\"institutionName\":\"Bank\",\"displayName\":\"Daily\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"****1234\",\"currencyCode\":\"ZAR\",\"{field}\":\"{privateValue}\"}}";

        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(input, "private-input"));

        AssertError(result, 3, "validation.invalid_input");
        Assert.DoesNotContain(privateValue, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, result.Stderr, StringComparison.Ordinal);
        Assert.Empty((await ListAccounts("active")).EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_001_physical_delete_intent_is_not_a_public_operation_and_preserves_the_account()
    {
        var account = await CreateAccount("delete-guard-create", "Daily", "****1234");
        var accountId = account.GetProperty("accountId").GetString()!;

        var result = await Run(
            ["ledger", "account", "delete", "--input", "-"],
            Envelope($"{{\"accountId\":\"{accountId}\"}}", "delete-intent"));

        AssertError(result, 2, "operation.unknown");
        var fetched = await Success(
            ["ledger", "account", "get", "--input", "-"],
            Envelope($"{{\"accountId\":\"{accountId}\"}}"),
            "ledger.account.get");
        Assert.Equal("active", fetched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UC_LEDGER_001_identical_mutation_replay_returns_the_original_result_once()
    {
        var request = AccountRequest("exact-replay", "Daily", "****1234");

        var first = await Run(["ledger", "account", "create", "--input", "-"], request);
        var replay = await Run(["ledger", "account", "create", "--input", "-"], request);

        AssertSuccess(first, "ledger.account.create");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Single((await ListAccounts("active")).EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_001_changed_mutation_replay_conflicts_and_preserves_the_original()
    {
        var original = await CreateAccount("changed-replay", "Daily", "****1234");

        var changed = await Run(
            ["ledger", "account", "create", "--input", "-"],
            AccountRequest("changed-replay", "Changed", "****1234"));

        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        var fetched = await Success(
            ["ledger", "account", "get", "--input", "-"],
            Envelope($"{{\"accountId\":\"{original.GetProperty("accountId").GetString()}\"}}"),
            "ledger.account.get");
        Assert.Equal("Daily", fetched.GetProperty("displayName").GetString());
        Assert.Single((await ListAccounts("active")).EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_001_account_maintenance_does_not_create_other_dimensions()
    {
        await CreateAccount("dimension-isolation", "Daily", "****1234");

        foreach (var path in new[]
                 {
                     new[] { "ledger", "category", "list", "--input", "-" },
                     new[] { "ledger", "instrument", "list", "--input", "-" },
                     new[] { "ledger", "cardholder", "list", "--input", "-" },
                     new[] { "ledger", "pool", "list", "--input", "-" }
                 })
        {
            var result = await Success(path, Envelope("{}"), string.Join('.', path.Take(3)));
            Assert.Empty(result.GetProperty("items").EnumerateArray());
        }
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

    private async Task<JsonElement> CreateAccount(
        string key,
        string displayName,
        string maskedIdentifier,
        string institution = "Example Bank")
    {
        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            AccountRequest(key, displayName, maskedIdentifier, institution));
        return AssertSuccess(result, "ledger.account.create");
    }

    private async Task<JsonElement> RecordTransaction(string accountId, string key, char digestCharacter)
    {
        var result = await Run(
            ["ledger", "transaction", "record", "--input", "-"],
            TransactionRequest(accountId, key, digestCharacter));
        return AssertSuccess(result, "ledger.transaction.record");
    }

    private async Task<JsonElement> ListAccounts(string status)
    {
        var result = await Success(
            ["ledger", "account", "list", "--input", "-"],
            Envelope($"{{\"status\":\"{status}\"}}"),
            "ledger.account.list");
        return result.GetProperty("items");
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) =>
        fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(
        IReadOnlyList<string> arguments,
        string input,
        string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + errorCode, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("system.process", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static string AccountRequest(
        string key,
        string displayName,
        string maskedIdentifier,
        string institution = "Example Bank") => Envelope(
        $"{{\"institutionName\":\"{institution}\",\"displayName\":\"{displayName}\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"{maskedIdentifier}\",\"currencyCode\":\"ZAR\"}}",
        key);

    private static string TransactionRequest(string accountId, string key, char digestCharacter) => Envelope(
        $"{{\"accountId\":\"{accountId}\",\"signedAmount\":\"-12.34\",\"currencyCode\":\"ZAR\",\"transactionDate\":\"2026-07-22\",\"postingDate\":null,\"originalDescription\":\"Groceries\",\"instrumentId\":null,\"cardholderId\":null,\"initialEvidence\":{{\"kind\":\"agent_capture\",\"logicalIdentityDigest\":\"{new string(digestCharacter, 64)}\",\"opaqueExternalReference\":null,\"contentFingerprint\":null,\"observation\":null}}}}",
        key);

    private static string Envelope(string input, string? idempotencyKey = null) =>
        "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"uc001\",\"runId\":\"published-e2e\"}"
        + (idempotencyKey is null ? string.Empty : ",\"idempotencyKey\":\"" + idempotencyKey + "\"")
        + ",\"input\":" + input + "}";
}
