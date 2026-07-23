using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC017PaymentAttributionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc017-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_017_agent_discovers_identity_and_attribution_contracts()
    {
        foreach (var (operationId, requestType) in new[]
                 {
                     ("ledger.instrument.create", "CreatePaymentInstrumentInput"),
                     ("ledger.instrument.archive", "ArchivePaymentInstrumentInput"),
                     ("ledger.cardholder.create", "CreateCardholderInput"),
                     ("ledger.cardholder.archive", "ArchiveCardholderInput"),
                     ("ledger.transaction.attribution.assign", "AssignPaymentAttributionInput"),
                     ("ledger.transaction.attribution.correct", "CorrectPaymentAttributionInput")
                 })
        {
            var operation = (await Success(["schema", "show", operationId, "--input", "-"], Envelope(new JsonObject()), "system.schema.show")).GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.True(operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_creates_privacy_safe_provider_neutral_identities()
    {
        var accountId = await CreateAccount("Identity");
        var instrument = await CreateInstrument("Daily card", accountId, "1234", Key("instrument"));
        var cardholder = await CreateCardholder("Owner", Key("cardholder"));

        Assert.Equal("Daily card", instrument.GetProperty("label").GetString());
        Assert.Equal(accountId, instrument.GetProperty("accountId").GetString());
        Assert.Equal("1234", instrument.GetProperty("maskedSuffix").GetString());
        Assert.Equal("active", instrument.GetProperty("status").GetString());
        Assert.Single(instrument.GetProperty("lifecycleHistory").EnumerateArray());
        Assert.Equal("Owner", cardholder.GetProperty("label").GetString());
        Assert.Equal("active", cardholder.GetProperty("status").GetString());
        Assert.DoesNotContain("provider", instrument.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cardNumber", instrument.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_assigns_dimensions_independently_without_other_mutation()
    {
        var accountId = await CreateAccount("Independent");
        var transaction = await Record(accountId, "-12.34");
        var instrument = await CreateInstrument("Card", accountId, "1717", Key("instrument"));
        var cardholder = await CreateCardholder("Owner", Key("cardholder"));
        var before = await Actuals();
        var original = transaction.Clone();

        var instrumentResult = await Assign(transaction, KnownInstrument(InstrumentId(instrument)), null, "Owner identified instrument", Key("assign-instrument"));
        var afterInstrument = instrumentResult.GetProperty("transaction");
        Assert.Equal("known", afterInstrument.GetProperty("paymentAttribution").GetProperty("instrumentState").GetString());
        Assert.Equal(InstrumentId(instrument), afterInstrument.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal("unknown", afterInstrument.GetProperty("paymentAttribution").GetProperty("cardholderState").GetString());
        AssertUnchangedDimensions(original, afterInstrument);

        var holderResult = await Correct(afterInstrument, null, KnownCardholder(CardholderId(cardholder)), "Owner identified cardholder", Key("correct-holder"));
        var final = holderResult.GetProperty("transaction");
        Assert.Equal(InstrumentId(instrument), final.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal(CardholderId(cardholder), final.GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        AssertUnchangedDimensions(original, final);
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_correction_replaces_and_restores_explicit_unknown_with_history()
    {
        var accountId = await CreateAccount("Correction");
        var transaction = await Record(accountId, "-20.00");
        var first = await CreateInstrument("First", accountId, "1111", Key("first"));
        var second = await CreateInstrument("Second", accountId, "2222", Key("second"));
        var assigned = await Assign(transaction, KnownInstrument(InstrumentId(first)), null, "Initial choice", Key("assign"));
        var corrected = await Correct(assigned.GetProperty("transaction"), KnownInstrument(InstrumentId(second)), null, "Owner corrected instrument", Key("correct"));
        var unknown = await Correct(corrected.GetProperty("transaction"), Unknown(), null, "Owner withdrew attribution", Key("unknown"));
        var current = unknown.GetProperty("transaction");

        Assert.Equal("unknown", current.GetProperty("paymentAttribution").GetProperty("instrumentState").GetString());
        Assert.Null(current.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        var history = current.GetProperty("history").GetProperty("paymentAttribution").EnumerateArray().ToArray();
        Assert.Equal(4, history.Length);
        Assert.Equal(["initialize", "assign", "correct", "correct"], history.Select(item => item.GetProperty("action").GetString()));
        Assert.Equal("Owner withdrew attribution", history[^1].GetProperty("reason").GetString());
        Assert.Equal(history[^2].GetProperty("attributionEventId").GetString(), history[^1].GetProperty("previousEventId").GetString());
    }

    [Theory]
    [InlineData("instrument")]
    [InlineData("cardholder")]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_identity_lifecycle_preserves_stable_id_and_history(string kind)
    {
        var accountId = await CreateAccount("Lifecycle " + kind);
        var created = kind == "instrument"
            ? await CreateInstrument("Original", accountId, "3333", Key("create"))
            : await CreateCardholder("Original", Key("create"));
        var id = kind == "instrument" ? InstrumentId(created) : CardholderId(created);

        await Lifecycle(kind, "rename", id, "Renamed", "Owner clarified label");
        await Lifecycle(kind, "archive", id, null, "Owner archived identity");
        await Lifecycle(kind, "reactivate", id, null, "Owner restored identity");
        var current = await GetIdentity(kind, id, true);

        Assert.Equal(id, kind == "instrument" ? InstrumentId(current) : CardholderId(current));
        Assert.Equal("Renamed", current.GetProperty("label").GetString());
        Assert.Equal("active", current.GetProperty("status").GetString());
        Assert.Equal(4, current.GetProperty("lifecycleHistory").GetArrayLength());
    }

    [Theory]
    [InlineData("providerId")]
    [InlineData("cardNumber")]
    [InlineData("providerPayload")]
    [InlineData("full-suffix")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_rejects_provider_or_full_identifier_payload(string scenario)
    {
        var input = new JsonObject { ["label"] = "Unsafe", ["accountId"] = null, ["maskedSuffix"] = scenario == "full-suffix" ? "12345" : "1234" };
        if (scenario != "full-suffix") input[scenario] = "secret";

        AssertError(
            await Run(["ledger", "instrument", "create", "--input", "-"], Envelope(input, Key("privacy"))),
            3,
            scenario == "full-suffix" ? "LEDGER-PAYMENT-IDENTITY-INVALID" : "validation.invalid_input");
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_identity_replay_converges_and_changed_request_conflicts()
    {
        var input = new JsonObject { ["label"] = "Replay card", ["accountId"] = null, ["maskedSuffix"] = "4444" };
        var request = Envelope(input, "identity-key");

        var first = await Run(["ledger", "instrument", "create", "--input", "-"], request);
        var replay = await Run(["ledger", "instrument", "create", "--input", "-"], request);
        var changed = input.DeepClone().AsObject();
        changed["label"] = "Changed card";
        var conflict = await Run(["ledger", "instrument", "create", "--input", "-"], Envelope(changed, "identity-key"));

        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Theory]
    [InlineData("missing-instrument", 4, "LEDGER-PAYMENT-INSTRUMENT-NOT-FOUND")]
    [InlineData("archived-instrument", 6, "LEDGER-PAYMENT-INSTRUMENT-ARCHIVED")]
    [InlineData("wrong-account", 6, "LEDGER-PAYMENT-ATTRIBUTION-ACCOUNT-INCOMPATIBLE")]
    [InlineData("archived-cardholder", 6, "LEDGER-CARDHOLDER-ARCHIVED")]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_invalid_identity_selection_is_atomic(string scenario, int exitCode, string errorCode)
    {
        var accountId = await CreateAccount("Invalid selection");
        var transaction = await Record(accountId, "-10.00");
        var input = AttributionInput(transaction, "Invalid selection");
        if (scenario == "missing-instrument") input["instrument"] = KnownInstrument(Ulid("missing-instrument"));
        if (scenario == "archived-instrument")
        {
            var identity = await CreateInstrument("Archived", accountId, "5555", Key("instrument"));
            await Lifecycle("instrument", "archive", InstrumentId(identity), null, "Archive test identity");
            input["instrument"] = KnownInstrument(InstrumentId(identity));
        }
        if (scenario == "wrong-account")
        {
            var identity = await CreateInstrument("Other", await CreateAccount("Other"), "6666", Key("instrument"));
            input["instrument"] = KnownInstrument(InstrumentId(identity));
        }
        if (scenario == "archived-cardholder")
        {
            var identity = await CreateCardholder("Archived", Key("cardholder"));
            await Lifecycle("cardholder", "archive", CardholderId(identity), null, "Archive test identity");
            input["cardholder"] = KnownCardholder(CardholderId(identity));
        }

        AssertError(await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], Envelope(input, Key("invalid"))), exitCode, errorCode);
        Assert.Single((await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("paymentAttribution").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_inactive_transaction_rejects_assignment()
    {
        var transaction = await Record(await CreateAccount("Inactive"), "-8.00");
        var holder = await CreateCardholder("Owner", Key("holder"));
        await Success(
            ["ledger", "transaction", "void", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = TransactionId(transaction), ["reason"] = "Owner voided transaction" }, Key("void")),
            "ledger.transaction.void");

        AssertError(
            await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], Envelope(AttributionInput(transaction, "Late assignment", null, KnownCardholder(CardholderId(holder))), Key("late"))),
            6,
            "LEDGER-PAYMENT-ATTRIBUTION-TRANSACTION-INACTIVE");
    }

    [Theory]
    [InlineData("stale", "LEDGER-PAYMENT-ATTRIBUTION-STALE")]
    [InlineData("unchanged", "LEDGER-PAYMENT-ATTRIBUTION-UNCHANGED")]
    [InlineData("assign-again", "LEDGER-PAYMENT-ATTRIBUTION-ALREADY-ASSIGNED")]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_stale_or_same_state_request_is_rejected(string scenario, string errorCode)
    {
        var transaction = await Record(await CreateAccount("State conflict"), "-9.00");
        var holder = await CreateCardholder("Owner", Key("holder"));
        var assigned = await Assign(transaction, null, KnownCardholder(CardholderId(holder)), "Owner assigned holder", Key("assign"));
        var current = assigned.GetProperty("transaction");
        PublishedTallyResult result;
        if (scenario == "stale") result = await Run(["ledger", "transaction", "attribution", "correct", "--input", "-"], Envelope(AttributionInput(transaction, "Stale correction", null, Unknown()), Key("stale")));
        else if (scenario == "unchanged") result = await Run(["ledger", "transaction", "attribution", "correct", "--input", "-"], Envelope(AttributionInput(current, "No change", null, KnownCardholder(CardholderId(holder))), Key("unchanged")));
        else result = await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], Envelope(AttributionInput(current, "Assign again", null, Unknown()), Key("again")));

        AssertError(result, 5, errorCode);
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("paymentAttribution").GetArrayLength());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_attribution_replay_converges_and_changed_input_conflicts()
    {
        var transaction = await Record(await CreateAccount("Replay attribution"), "-7.00");
        var holder = await CreateCardholder("Owner", Key("holder"));
        var input = AttributionInput(transaction, "Owner selected holder", null, KnownCardholder(CardholderId(holder)));
        var request = Envelope(input, "attribution-key");

        var first = await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], request);
        var replay = await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], request);
        var changed = input.DeepClone().AsObject();
        changed["reason"] = "Changed reason";
        var conflict = await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], Envelope(changed, "attribution-key"));

        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("paymentAttribution").GetArrayLength());
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_interrupted_assignment_commits_none_or_one_and_retry_converges()
    {
        var transaction = await Record(await CreateAccount("Crash"), "-6.00");
        var holder = await CreateCardholder("Owner", Key("holder"));
        var request = Envelope(AttributionInput(transaction, "Crash-atomic assignment", null, KnownCardholder(CardholderId(holder))), "crash-attribution");

        Assert.True(await KillPublishedProcessDuringMutation(["ledger", "transaction", "attribution", "assign", "--input", "-"], request), "The published process completed before interruption.");
        Assert.InRange((await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("paymentAttribution").GetArrayLength(), 1, 2);

        var converged = AssertSuccess(await Run(["ledger", "transaction", "attribution", "assign", "--input", "-"], request), "ledger.transaction.attribution.assign");
        Assert.Equal(CardholderId(holder), converged.GetProperty("transaction").GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("paymentAttribution").GetArrayLength());
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

    private async Task<string> CreateAccount(string label)
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC017 " + label + " " + suffix,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> CreateInstrument(string label, string? accountId, string? suffix, string key) => await Success(
        ["ledger", "instrument", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label, ["accountId"] = accountId, ["maskedSuffix"] = suffix }, key),
        "ledger.instrument.create");

    private async Task<JsonElement> CreateCardholder(string label, string key) => await Success(
        ["ledger", "cardholder", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label }, key),
        "ledger.cardholder.create");

    private async Task<JsonElement> Lifecycle(string kind, string action, string id, string? label, string reason)
    {
        var input = new JsonObject { [kind == "instrument" ? "instrumentId" : "cardholderId"] = id, ["reason"] = reason };
        if (action == "rename") input["newLabel"] = label;
        var result = await Success(["ledger", kind, action, "--input", "-"], Envelope(input, Key(kind + "-" + action)), $"ledger.{kind}.{action}");
        return result.GetProperty(kind).Clone();
    }

    private async Task<JsonElement> GetIdentity(string kind, string id, bool history) => await Success(
        ["ledger", kind, "get", "--input", "-"],
        Envelope(new JsonObject { [kind == "instrument" ? "instrumentId" : "cardholderId"] = id, ["includeHistory"] = history }),
        $"ledger.{kind}.get");

    private async Task<JsonElement> Record(string accountId, string amount)
    {
        var token = Key("capture");
        return await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmount"] = amount,
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = "2026-07-17",
                ["postingDate"] = null,
                ["originalDescription"] = "Payment attribution transaction",
                ["instrumentId"] = null,
                ["cardholderId"] = null,
                ["initialEvidence"] = new JsonObject
                {
                    ["kind"] = "agent_capture",
                    ["logicalIdentityDigest"] = Digest(token),
                    ["opaqueExternalReference"] = "capture:" + token,
                    ["contentFingerprint"] = null,
                    ["observation"] = null
                }
            }, Key("record")),
            "ledger.transaction.record");
    }

    private async Task<JsonElement> Assign(JsonElement transaction, JsonObject? instrument, JsonObject? cardholder, string reason, string key) => await Success(
        ["ledger", "transaction", "attribution", "assign", "--input", "-"],
        Envelope(AttributionInput(transaction, reason, instrument, cardholder), key),
        "ledger.transaction.attribution.assign");

    private async Task<JsonElement> Correct(JsonElement transaction, JsonObject? instrument, JsonObject? cardholder, string reason, string key) => await Success(
        ["ledger", "transaction", "attribution", "correct", "--input", "-"],
        Envelope(AttributionInput(transaction, reason, instrument, cardholder), key),
        "ledger.transaction.attribution.correct");

    private static JsonObject AttributionInput(JsonElement transaction, string reason, JsonObject? instrument = null, JsonObject? cardholder = null) => new()
    {
        ["transactionId"] = TransactionId(transaction),
        ["expectedAttributionEventId"] = transaction.GetProperty("paymentAttribution").GetProperty("attributionEventId").GetString(),
        ["instrument"] = instrument,
        ["cardholder"] = cardholder,
        ["reason"] = reason
    };

    private async Task<JsonElement> GetTransaction(string id) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = id, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> Actuals() => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject { ["filter"] = new JsonObject { ["groupBy"] = "pool_category" }, ["pageSize"] = 100, ["cursor"] = null }),
        "ledger.actuals.query");

    private async Task<bool> KillPublishedProcessDuringMutation(IReadOnlyList<string> arguments, string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
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

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);
    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);
    private string Key(string purpose) => "uc017-" + purpose + "-" + Interlocked.Increment(ref sequence);
    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;
    private static string InstrumentId(JsonElement instrument) => instrument.GetProperty("instrumentId").GetString()!;
    private static string CardholderId(JsonElement cardholder) => cardholder.GetProperty("cardholderId").GetString()!;
    private static JsonObject KnownInstrument(string id) => new() { ["state"] = "known", ["instrumentId"] = id };
    private static JsonObject KnownCardholder(string id) => new() { ["state"] = "known", ["cardholderId"] = id };
    private static JsonObject Unknown() => new() { ["state"] = "unknown" };
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Ulid(string seed)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "01J" + new string(bytes.Take(23).Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static void AssertUnchangedDimensions(JsonElement before, JsonElement after)
    {
        foreach (var property in new[] { "accountId", "signedAmount", "currencyCode", "transactionDate", "postingDate", "originalDescription", "category", "pool", "evidence", "reconciliationState" })
            Assert.Equal(before.GetProperty(property).GetRawText(), after.GetProperty(property).GetRawText());
    }

    private static void AssertFinancialActualsEqual(JsonElement before, JsonElement after)
    {
        Assert.Equal(before.GetProperty("totals").GetRawText(), after.GetProperty("totals").GetRawText());
        Assert.Equal(before.GetProperty("groups").GetRawText(), after.GetProperty("groups").GetRawText());
        Assert.Equal(
            before.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText()),
            after.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText()));
    }

    private static string Envelope(JsonNode input, string? key = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc017", ["runId"] = "published-e2e" },
            ["input"] = input
        };
        if (key is not null) envelope["idempotencyKey"] = key;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string errorCode)
    {
        Assert.True(result.ExitCode == exitCode, $"Expected {exitCode}/{errorCode}; actual {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal("tally: " + errorCode, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
