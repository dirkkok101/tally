using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC002TransactionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private static readonly string[] TransactionTables =
    [
        "transaction_fact",
        "evidence_record",
        "evidence_link_event",
        "transaction_attribution_event",
        "pool_assignment_event"
    ];

    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc002-" + Guid.NewGuid().ToString("N"));
    private int backupSequence;

    [Fact]
    public async Task UC_LEDGER_002_agent_discovers_the_concrete_transaction_contract()
    {
        var schema = await Success(
            ["schema", "show", "ledger.transaction.record", "--input", "-"],
            Envelope(new JsonObject()),
            "system.schema.show");
        var operation = schema.GetProperty("operation");

        Assert.Equal("ledger.transaction.record", operation.GetProperty("operationId").GetString());
        Assert.EndsWith("RecordTransactionInput", operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("TransactionDetail", operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
        Assert.True(operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        Assert.Contains(operation.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "LEDGER-TRANSACTION-EVIDENCE-CONFLICT"
            && error.GetProperty("exitCode").GetInt32() == 5);
    }

    [Fact]
    public async Task UC_LEDGER_002_main_flow_round_trips_exact_facts_evidence_and_explicit_defaults()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var input = TransactionInput(accountId, digestCharacter: 'a', postingDate: "2026-07-03");
        input["initialEvidence"]!["observation"] = Observation(accountId, -1234, "2026-07-01", "2026-07-03");

        var recorded = await Record(input, "record-main");
        var transactionId = recorded.GetProperty("transactionId").GetString()!;
        var fetched = await Success(
            ["ledger", "transaction", "get", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
            "ledger.transaction.get");

        Assert.Equal(transactionId, fetched.GetProperty("transactionId").GetString());
        Assert.Equal(accountId, fetched.GetProperty("accountId").GetString());
        Assert.Equal("-12.34", fetched.GetProperty("signedAmount").GetString());
        Assert.Equal("ZAR", fetched.GetProperty("currencyCode").GetString());
        Assert.Equal("2026-07-01", fetched.GetProperty("transactionDate").GetString());
        Assert.Equal("2026-07-03", fetched.GetProperty("postingDate").GetString());
        Assert.Equal("2026-07-01", fetched.GetProperty("effectiveDate").GetString());
        Assert.Equal("Owner-safe purchase", fetched.GetProperty("originalDescription").GetString());
        Assert.Equal("recorded_unreconciled", fetched.GetProperty("reconciliationState").GetString());
        Assert.Equal("uncategorized", fetched.GetProperty("category").GetProperty("state").GetString());
        Assert.Equal("unassigned", fetched.GetProperty("pool").GetProperty("state").GetString());
        Assert.Equal("unknown", fetched.GetProperty("paymentAttribution").GetProperty("instrumentState").GetString());
        Assert.Equal("unknown", fetched.GetProperty("paymentAttribution").GetProperty("cardholderState").GetString());

        var evidence = Assert.Single(fetched.GetProperty("evidence").EnumerateArray());
        Assert.Equal("agent_capture", evidence.GetProperty("kind").GetString());
        Assert.Equal(Digest('a'), evidence.GetProperty("logicalIdentityDigest").GetString());
        Assert.Equal("capture:one", evidence.GetProperty("opaqueExternalReference").GetString());
        Assert.Equal("supporting", evidence.GetProperty("role").GetString());
        Assert.Equal(-1234, evidence.GetProperty("observation").GetProperty("signedAmountMinor").GetInt64());

        var history = fetched.GetProperty("history");
        Assert.Empty(history.GetProperty("lifecycle").EnumerateArray());
        Assert.Single(history.GetProperty("paymentAttribution").EnumerateArray());
        Assert.Single(history.GetProperty("poolAssignments").EnumerateArray());
        Assert.Empty(history.GetProperty("categoryAssignments").EnumerateArray());

        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 1, observationCount: 1);
        Assert.Equal(2, counts["idempotency_record"]);
        Assert.Equal(1, counts["logical_effect"]);
    }

    [Theory]
    [InlineData("12.34")]
    [InlineData("-12.34")]
    public async Task UC_LEDGER_002_owner_economic_sign_round_trips_exactly(string signedAmount)
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;

        var recorded = await Record(TransactionInput(accountId, signedAmount: signedAmount), "signed-amount");

        Assert.Equal(signedAmount, recorded.GetProperty("signedAmount").GetString());
        AssertTransactionEffects(await DurableCounts(), 1);
    }

    [Fact]
    public async Task UC_LEDGER_002_optional_payment_identities_are_explicit_and_exact()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var instrument = await Success(
            ["ledger", "instrument", "create", "--input", "-"],
            Envelope(new JsonObject { ["label"] = "Primary card", ["accountId"] = accountId, ["maskedSuffix"] = "1234" }, "instrument-create"),
            "ledger.instrument.create");
        var cardholder = await Success(
            ["ledger", "cardholder", "create", "--input", "-"],
            Envelope(new JsonObject { ["label"] = "Owner" }, "cardholder-create"),
            "ledger.cardholder.create");
        var instrumentId = instrument.GetProperty("instrumentId").GetString()!;
        var cardholderId = cardholder.GetProperty("cardholderId").GetString()!;
        var input = TransactionInput(accountId);
        input["instrumentId"] = instrumentId;
        input["cardholderId"] = cardholderId;

        var recorded = await Record(input, "known-attribution");
        var attribution = recorded.GetProperty("paymentAttribution");

        Assert.Equal("known", attribution.GetProperty("instrumentState").GetString());
        Assert.Equal(instrumentId, attribution.GetProperty("instrumentId").GetString());
        Assert.Equal("known", attribution.GetProperty("cardholderState").GetString());
        Assert.Equal(cardholderId, attribution.GetProperty("cardholderId").GetString());
        Assert.Equal(2, recorded.GetProperty("history").GetProperty("paymentAttribution").GetArrayLength());
        AssertTransactionEffects(await DurableCounts(), 1, attributionCount: 2);
    }

    [Theory]
    [InlineData("0", "ZAR", "2026-07-01", "amount.zero")]
    [InlineData("1.2", "ZAR", "2026-07-01", "amount.invalid")]
    [InlineData("-12.34", "USD", "2026-07-01", "currency.unsupported")]
    [InlineData("-12.34", "ZAR", "2026-02-30", "date.invalid")]
    public async Task UC_LEDGER_002_invalid_financial_facts_create_no_durable_effect(
        string amount,
        string currency,
        string transactionDate,
        string errorCode)
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var input = TransactionInput(accountId, signedAmount: amount, transactionDate: transactionDate);
        input["currencyCode"] = currency;

        AssertError(await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(input, "invalid-fact")), 3, errorCode);
        await AssertOnlyAccountMutationExists();
    }

    [Theory]
    [InlineData("kind", "bank_email", "validation.invalid_input")]
    [InlineData("opaqueExternalReference", "account:123456789", "validation.invalid_input")]
    [InlineData("providerPayload", "PRIVATE_PROVIDER_PAYLOAD", "validation.invalid_input")]
    [InlineData("rawPayload", "PRIVATE_RAW_PAYLOAD", "validation.invalid_input")]
    public async Task UC_LEDGER_002_invalid_or_private_evidence_creates_no_effect_and_is_not_disclosed(
        string field,
        string privateValue,
        string errorCode)
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var input = TransactionInput(accountId);
        input["initialEvidence"]![field] = privateValue;

        var result = await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(input, "invalid-evidence"));

        AssertError(result, 3, errorCode);
        Assert.DoesNotContain(privateValue, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, result.Stderr, StringComparison.Ordinal);
        await AssertOnlyAccountMutationExists();
    }

    [Fact]
    public async Task UC_LEDGER_002_incompatible_evidence_observation_creates_no_effect()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var input = TransactionInput(accountId);
        input["initialEvidence"]!["observation"] = Observation(accountId, -999, "2026-07-01", null);

        var result = await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(input, "bad-observation"));

        AssertError(result, 3, "LEDGER-TRANSACTION-EVIDENCE-INCOMPATIBLE");
        await AssertOnlyAccountMutationExists();
    }

    [Fact]
    public async Task UC_LEDGER_002_invalid_payment_attribution_creates_no_effect()
    {
        var accountId = (await CreateAccount("Primary", "****1234", "primary-account")).GetProperty("accountId").GetString()!;
        var otherAccountId = (await CreateAccount("Other", "****5678", "other-account")).GetProperty("accountId").GetString()!;
        var instrument = await Success(
            ["ledger", "instrument", "create", "--input", "-"],
            Envelope(new JsonObject { ["label"] = "Other card", ["accountId"] = otherAccountId, ["maskedSuffix"] = "5678" }, "other-instrument"),
            "ledger.instrument.create");
        var input = TransactionInput(accountId);
        input["instrumentId"] = instrument.GetProperty("instrumentId").GetString();

        var result = await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(input, "bad-attribution"));

        AssertError(result, 6, "LEDGER-TRANSACTION-ATTRIBUTION-INCOMPATIBLE");
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 0);
        Assert.Equal(3, counts["idempotency_record"]);
        Assert.Equal(0, counts["logical_effect"]);
    }

    [Theory]
    [InlineData(false, 4, "LEDGER-ACCOUNT-NOT-FOUND")]
    [InlineData(true, 6, "LEDGER-ACCOUNT-ARCHIVED")]
    public async Task UC_LEDGER_002_missing_or_archived_account_creates_no_effect(bool archive, int exitCode, string errorCode)
    {
        var accountId = archive
            ? (await CreateAccount()).GetProperty("accountId").GetString()!
            : "01J00000000000000000000000";
        if (archive)
        {
            await Success(
                ["ledger", "account", "archive", "--input", "-"],
                Envelope(new JsonObject { ["accountId"] = accountId, ["reason"] = "Closed" }, "archive-account"),
                "ledger.account.archive");
        }

        var result = await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(TransactionInput(accountId), "account-state"));

        AssertError(result, exitCode, errorCode);
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 0);
        Assert.Equal(archive ? 2 : 0, counts["idempotency_record"]);
        Assert.Equal(0, counts["logical_effect"]);
    }

    [Fact]
    public async Task UC_LEDGER_002_missing_key_is_rejected_and_a_corrected_request_succeeds()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var input = TransactionInput(accountId);

        AssertError(await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(input)), 3, "validation.invalid_input");
        var recorded = await Record(TransactionInput(accountId), "corrected-key");

        Assert.False(string.IsNullOrEmpty(recorded.GetProperty("transactionId").GetString()));
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 1);
        Assert.Equal(2, counts["idempotency_record"]);
        Assert.Equal(1, counts["logical_effect"]);
    }

    [Fact]
    public async Task UC_LEDGER_002_identical_request_replay_returns_the_original_effect_once()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var request = Envelope(TransactionInput(accountId), "exact-replay");

        var first = await Run(["ledger", "transaction", "record", "--input", "-"], request);
        var replay = await Run(["ledger", "transaction", "record", "--input", "-"], request);

        AssertSuccess(first, "ledger.transaction.record");
        Assert.Equal(first.Stdout, replay.Stdout);
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 1);
        Assert.Equal(2, counts["idempotency_record"]);
        Assert.Equal(1, counts["logical_effect"]);
    }

    [Fact]
    public async Task UC_LEDGER_002_cross_key_evidence_replay_returns_the_original_effect_once()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;

        var first = await Record(TransactionInput(accountId), "first-key");
        var replay = await Record(TransactionInput(accountId), "second-key");

        Assert.Equal(first.GetProperty("transactionId").GetString(), replay.GetProperty("transactionId").GetString());
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 1);
        Assert.Equal(2, counts["idempotency_record"]);
        Assert.Equal(1, counts["logical_effect"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UC_LEDGER_002_conflicting_replay_preserves_the_original(bool reuseRequestKey)
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var original = await Record(TransactionInput(accountId), "original-key");
        var changed = TransactionInput(accountId, signedAmount: "-99.00");
        var key = reuseRequestKey ? "original-key" : "different-key";

        var conflict = await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(changed, key));

        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
        var fetched = await Success(
            ["ledger", "transaction", "get", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = original.GetProperty("transactionId").GetString(), ["includeHistory"] = true }),
            "ledger.transaction.get");
        Assert.Equal("-12.34", fetched.GetProperty("signedAmount").GetString());
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 1);
        Assert.Equal(2, counts["idempotency_record"]);
        Assert.Equal(1, counts["logical_effect"]);
    }

    [Fact]
    public async Task UC_LEDGER_002_process_crash_leaves_the_complete_transaction_effect_or_none_and_retry_converges()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var request = Envelope(TransactionInput(accountId), "crash-record");

        var killed = await KillPublishedProcessDuringMutation(request);

        Assert.True(killed, "The published process completed before the crash could be injected.");
        var interruptedCounts = await DurableCounts();
        var committed = interruptedCounts["transaction_fact"];
        Assert.True(committed is 0 or 1);
        AssertTransactionEffects(interruptedCounts, committed);
        Assert.Equal(1 + committed, interruptedCounts["idempotency_record"]);
        Assert.Equal(committed, interruptedCounts["logical_effect"]);

        var replay = await Record(TransactionInput(accountId), "crash-record");
        Assert.False(string.IsNullOrEmpty(replay.GetProperty("transactionId").GetString()));
        var recoveredCounts = await DurableCounts();
        AssertTransactionEffects(recoveredCounts, 1);
        Assert.Equal(3, recoveredCounts["idempotency_record"]);
        Assert.Equal(2, recoveredCounts["logical_effect"]);
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
        string displayName = "Daily",
        string maskedIdentifier = "****1234",
        string key = "account-create") => await Success(
        ["ledger", "account", "create", "--input", "-"],
        Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = displayName,
            ["accountType"] = "cheque",
            ["maskedIdentifier"] = maskedIdentifier,
            ["currencyCode"] = "ZAR"
        }, key),
        "ledger.account.create");

    private async Task<JsonElement> Record(JsonObject input, string key) => await Success(
        ["ledger", "transaction", "record", "--input", "-"],
        Envelope(input, key),
        "ledger.transaction.record");

    private async Task<IReadOnlyDictionary<string, long>> DurableCounts()
    {
        var backupRoot = Path.Combine(dataRoot, "verification-artifacts");
        Directory.CreateDirectory(backupRoot);
        File.SetUnixFileMode(backupRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sequence = ++backupSequence;
        var target = Path.Combine(backupRoot, $"state-{sequence}.tally-backup");
        var receipt = await Success(
            ["ledger", "backup", "create", "--input", "-"],
            Envelope(new JsonObject { ["targetPath"] = target }, "inspect-state-" + sequence),
            "ledger.backup.create");

        return receipt.GetProperty("manifest").GetProperty("types").EnumerateArray()
            .ToDictionary(
                type => type.GetProperty("name").GetString()!,
                type => type.GetProperty("rowCount").GetInt64(),
                StringComparer.Ordinal);
    }

    private async Task AssertOnlyAccountMutationExists()
    {
        var counts = await DurableCounts();
        AssertTransactionEffects(counts, 0);
        Assert.Equal(1, counts["idempotency_record"]);
        Assert.Equal(0, counts["logical_effect"]);
    }

    private static void AssertTransactionEffects(
        IReadOnlyDictionary<string, long> counts,
        long expected,
        long observationCount = 0,
        long? attributionCount = null)
    {
        foreach (var table in TransactionTables.Where(table => table != "transaction_attribution_event")) Assert.Equal(expected, counts[table]);
        Assert.Equal(attributionCount ?? expected, counts["transaction_attribution_event"]);
        Assert.Equal(observationCount, counts["evidence_observation"]);
        Assert.Equal(0, counts["transaction_lifecycle_event"]);
    }

    private async Task<bool> KillPublishedProcessDuringMutation(string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "ledger", "transaction", "record", "--input", "-" }) start.ArgumentList.Add(argument);
        start.Environment["TALLY_DATA_ROOT"] = dataRoot;

        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var killed = !process.HasExited;
        if (killed) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return killed;
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) =>
        fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) =>
        AssertSuccess(await Run(arguments, input), operationId);

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

    private static JsonObject TransactionInput(
        string accountId,
        string signedAmount = "-12.34",
        string transactionDate = "2026-07-01",
        string? postingDate = null,
        char digestCharacter = 'a') => new()
        {
            ["accountId"] = accountId,
            ["signedAmount"] = signedAmount,
            ["currencyCode"] = "ZAR",
            ["transactionDate"] = transactionDate,
            ["postingDate"] = postingDate,
            ["originalDescription"] = "Owner-safe purchase",
            ["instrumentId"] = null,
            ["cardholderId"] = null,
            ["initialEvidence"] = new JsonObject
            {
                ["kind"] = "agent_capture",
                ["logicalIdentityDigest"] = Digest(digestCharacter),
                ["opaqueExternalReference"] = "capture:one",
                ["contentFingerprint"] = null,
                ["observation"] = null
            }
        };

    private static JsonObject Observation(string accountId, long amountMinor, string transactionDate, string? postingDate) => new()
    {
        ["accountId"] = accountId,
        ["signedAmountMinor"] = amountMinor,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = transactionDate,
        ["postingDate"] = postingDate,
        ["instrumentId"] = null,
        ["cardholderId"] = null,
        ["descriptionFingerprint"] = null
    };

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject
            {
                ["kind"] = "automation",
                ["label"] = "uc002",
                ["runId"] = "published-e2e"
            },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static string Digest(char value) => new(value, 64);
}
