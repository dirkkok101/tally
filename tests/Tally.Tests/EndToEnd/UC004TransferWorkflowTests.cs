using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC004TransferWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc004-" + Guid.NewGuid().ToString("N"));
    private int backupSequence;

    [Fact]
    public async Task UC_LEDGER_004_agent_discovers_transfer_confirmation_and_relationship_read_contracts()
    {
        var confirmation = (await Success(
            ["schema", "show", "ledger.transfer.confirm", "--input", "-"],
            Envelope(new JsonObject()),
            "system.schema.show")).GetProperty("operation");
        var get = (await Success(
            ["schema", "show", "ledger.relationship.get", "--input", "-"],
            Envelope(new JsonObject()),
            "system.schema.show")).GetProperty("operation");

        Assert.EndsWith("ConfirmTransferInput", confirmation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("FinancialRelationshipDetail", confirmation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
        Assert.True(confirmation.GetProperty("requiresIdempotencyKey").GetBoolean());
        Assert.Contains(confirmation.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "LEDGER-TRANSFER-CURRENCY");
        Assert.EndsWith("GetRelationshipInput", get.GetProperty("requestType").GetString(), StringComparison.Ordinal);
        Assert.False(get.GetProperty("requiresIdempotencyKey").GetBoolean());
    }

    [Fact]
    public async Task UC_LEDGER_004_equal_opposite_owned_account_legs_create_one_zero_spend_transfer_with_dimensions()
    {
        var firstAccount = await CreateAccount("Cheque", "1111", "cheque", "first-account");
        var secondAccount = await CreateAccount("Savings", "2222", "savings", "second-account");
        var outflow = await Record(firstAccount.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 1);
        var inflow = await Record(secondAccount.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 2);
        var categoryId = (await CreateCategory()).GetProperty("categoryId").GetString()!;
        var poolId = (await CreatePool()).GetProperty("poolId").GetString()!;
        await AssignCategory(outflow.GetProperty("transactionId").GetString()!, categoryId, "outflow-category");
        await AssignCategory(inflow.GetProperty("transactionId").GetString()!, categoryId, "inflow-category");
        await AssignPool(outflow, poolId, "outflow-pool");
        await AssignPool(inflow, poolId, "inflow-pool");

        var relationship = await Confirm(
            outflow.GetProperty("transactionId").GetString()!,
            inflow.GetProperty("transactionId").GetString()!,
            "Owner confirmed transfer",
            "confirm-transfer");

        Assert.Equal("transfer", relationship.GetProperty("type").GetString());
        Assert.Equal(outflow.GetProperty("transactionId").GetString(), relationship.GetProperty("sourceTransactionId").GetString());
        Assert.Equal("transfer_outflow", relationship.GetProperty("sourceRole").GetString());
        Assert.Equal(inflow.GetProperty("transactionId").GetString(), relationship.GetProperty("targetTransactionId").GetString());
        Assert.Equal("transfer_inflow", relationship.GetProperty("targetRole").GetString());
        Assert.Equal("12.34", relationship.GetProperty("principalAmount").GetString());
        Assert.Equal("ZAR", relationship.GetProperty("currencyCode").GetString());
        Assert.Equal("active", relationship.GetProperty("state").GetString());

        var fetched = await GetRelationship(relationship.GetProperty("relationshipId").GetString()!);
        Assert.Equal(relationship.GetRawText(), fetched.GetRawText());
        var actuals = await QueryActuals("pool_category");
        AssertTotals(actuals, "0", "0", "0");
        Assert.Equal(2, actuals.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { "transfer_inflow", "transfer_outflow" },
            actuals.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("relationshipState").GetString()).Order(StringComparer.Ordinal));
        Assert.All(actuals.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(categoryId, item.GetProperty("categoryId").GetString());
            Assert.Equal(poolId, item.GetProperty("poolId").GetString());
            Assert.Equal("0", item.GetProperty("contribution").GetProperty("externalSpend").GetString());
            Assert.Equal("0", item.GetProperty("contribution").GetProperty("budgetActual").GetString());
        });
        Assert.Equal(1, (await DurableCounts())["financial_relationship"]);
    }

    [Fact]
    public async Task UC_LEDGER_004_different_dates_are_accepted_and_a_separate_fee_remains_ordinary_spend()
    {
        var firstAccount = await CreateAccount("Cheque", "1111", "cheque", "first-account");
        var secondAccount = await CreateAccount("Savings", "2222", "savings", "second-account");
        var outflow = await Record(firstAccount.GetProperty("accountId").GetString()!, "-50", "2026-01-01", 3);
        var inflow = await Record(secondAccount.GetProperty("accountId").GetString()!, "50", "2026-02-15", 4);
        var fee = await Record(firstAccount.GetProperty("accountId").GetString()!, "-0.50", "2026-01-01", 5);

        await Confirm(
            outflow.GetProperty("transactionId").GetString()!,
            inflow.GetProperty("transactionId").GetString()!,
            "Dates supplied independently; principal only",
            "confirm-transfer");

        var actuals = await QueryActuals("none");
        AssertTotals(actuals, "-0.50", "0.50", "0.50");
        Assert.Equal(3, actuals.GetProperty("totalCount").GetInt32());
        var feeItem = Assert.Single(actuals.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("transactionId").GetString() == fee.GetProperty("transactionId").GetString());
        Assert.Equal("none", feeItem.GetProperty("relationshipState").GetString());
        Assert.Equal("0.50", feeItem.GetProperty("contribution").GetProperty("externalSpend").GetString());
        Assert.Equal("recorded_unreconciled", (await GetTransaction(fee.GetProperty("transactionId").GetString()!)).GetProperty("reconciliationState").GetString());
        Assert.Equal(1, (await DurableCounts())["financial_relationship"]);
    }

    [Fact]
    public async Task UC_LEDGER_004_same_account_legs_are_rejected_without_relationship()
    {
        var account = await CreateAccount("Cheque", "1111", "cheque", "account");
        var accountId = account.GetProperty("accountId").GetString()!;
        var outflow = await Record(accountId, "-12.34", "2026-07-01", 6);
        var inflow = await Record(accountId, "12.34", "2026-07-01", 7);

        var result = await ConfirmResult(outflow, inflow, "Same account", "confirm");

        AssertError(result, 3, "LEDGER-TRANSFER-SAME-ACCOUNT");
        AssertNoRelationship(await DurableCounts());
    }

    [Theory]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("12.34", "-12.34")]
    public async Task UC_LEDGER_004_explicit_outflow_and_inflow_roles_require_opposite_signs(string outflowAmount, string inflowAmount)
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, outflowAmount, "2026-07-01", 8);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, inflowAmount, "2026-07-01", 9);

        AssertError(await ConfirmResult(outflow, inflow, "Wrong roles", "confirm"), 3, "LEDGER-TRANSFER-SIGN");
        AssertNoRelationship(await DurableCounts());
    }

    [Theory]
    [InlineData("-12.34", "12.33", "Unequal principal")]
    [InlineData("-50.50", "50", "Embedded fee")]
    public async Task UC_LEDGER_004_unequal_or_fee_embedded_principal_is_rejected(
        string outflowAmount,
        string inflowAmount,
        string reason)
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, outflowAmount, "2026-07-01", 10);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, inflowAmount, "2026-07-01", 11);

        AssertError(await ConfirmResult(outflow, inflow, reason, "confirm"), 3, "LEDGER-TRANSFER-AMOUNT");
        AssertNoRelationship(await DurableCounts());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UC_LEDGER_004_inactive_leg_is_rejected_without_relationship(bool voidOutflow)
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 12);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 13);
        var inactiveId = (voidOutflow ? outflow : inflow).GetProperty("transactionId").GetString()!;
        await Success(
            ["ledger", "transaction", "void", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = inactiveId, ["reason"] = "Owner voided transaction" }, "void-leg"),
            "ledger.transaction.void");

        AssertError(await ConfirmResult(outflow, inflow, "Inactive leg", "confirm"), 6, "LEDGER-TRANSFER-TRANSACTION-INACTIVE");
        AssertNoRelationship(await DurableCounts());
    }

    [Fact]
    public async Task UC_LEDGER_004_missing_leg_is_rejected_without_relationship()
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 14);
        var missingInflow = new JsonObject { ["transactionId"] = "01J00000000000000000000000" };

        var result = await Run(
            ["ledger", "transfer", "confirm", "--input", "-"],
            TransferEnvelope(outflow.GetProperty("transactionId").GetString()!, missingInflow["transactionId"]!.GetValue<string>(), "Missing leg", "confirm"));

        AssertError(result, 4, "LEDGER-TRANSACTION-NOT-FOUND");
        AssertNoRelationship(await DurableCounts());
    }

    [Fact]
    public async Task UC_LEDGER_004_public_recording_prevents_a_currency_mismatch_from_entering_relationship_state()
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        _ = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 15);
        var input = TransactionInput(second.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 16);
        input["currencyCode"] = "USD";

        var result = await Run(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(input, "record-unsupported-currency"));

        AssertError(result, 3, "currency.unsupported");
        AssertNoRelationship(await DurableCounts());
    }

    [Fact]
    public async Task UC_LEDGER_004_active_roles_are_exclusive_across_relationships()
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var third = await CreateAccount("Third", "3333", "savings", "third-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 17);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 18);
        var competingInflow = await Record(third.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 19);
        await Confirm(
            outflow.GetProperty("transactionId").GetString()!,
            inflow.GetProperty("transactionId").GetString()!,
            "First relationship",
            "first-confirm");

        var conflict = await Run(
            ["ledger", "transfer", "confirm", "--input", "-"],
            TransferEnvelope(
                outflow.GetProperty("transactionId").GetString()!,
                competingInflow.GetProperty("transactionId").GetString()!,
                "Competing relationship",
                "second-confirm"));

        AssertError(conflict, 5, "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT");
        Assert.Equal(1, (await DurableCounts())["financial_relationship"]);
    }

    [Fact]
    public async Task UC_LEDGER_004_exact_and_cross_key_replay_are_stable_while_changed_reuse_conflicts()
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 20);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 21);
        var outflowId = outflow.GetProperty("transactionId").GetString()!;
        var inflowId = inflow.GetProperty("transactionId").GetString()!;
        var exactRequest = TransferEnvelope(outflowId, inflowId, "Owner confirmed", "same-key");

        var original = await Run(["ledger", "transfer", "confirm", "--input", "-"], exactRequest);
        var exactReplay = await Run(["ledger", "transfer", "confirm", "--input", "-"], exactRequest);
        var crossKeyReplay = await Run(
            ["ledger", "transfer", "confirm", "--input", "-"],
            TransferEnvelope(outflowId, inflowId, "Owner confirmed", "other-key"));
        var changed = await Run(
            ["ledger", "transfer", "confirm", "--input", "-"],
            TransferEnvelope(outflowId, inflowId, "Changed reason", "same-key"));

        AssertSuccess(original, "ledger.transfer.confirm");
        Assert.Equal(original.Stdout, exactReplay.Stdout);
        Assert.Equal(original.Stdout, crossKeyReplay.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(1, (await DurableCounts())["financial_relationship"]);
    }

    [Fact]
    public async Task UC_LEDGER_004_process_crash_commits_the_complete_relationship_or_none_and_replay_converges()
    {
        var first = await CreateAccount("First", "1111", "cheque", "first-account");
        var second = await CreateAccount("Second", "2222", "savings", "second-account");
        var outflow = await Record(first.GetProperty("accountId").GetString()!, "-12.34", "2026-07-01", 22);
        var inflow = await Record(second.GetProperty("accountId").GetString()!, "12.34", "2026-07-01", 23);
        var outflowId = outflow.GetProperty("transactionId").GetString()!;
        var inflowId = inflow.GetProperty("transactionId").GetString()!;
        var request = TransferEnvelope(outflowId, inflowId, "Crash-atomic transfer", "crash-transfer");

        Assert.True(await KillPublishedProcessDuringMutation(request), "The published process completed before the crash could be injected.");
        var interrupted = await DurableCounts();
        var committed = interrupted["financial_relationship"];
        Assert.True(committed is 0 or 1);
        Assert.Equal(0, interrupted["relationship_lifecycle_event"]);
        Assert.Equal(4 + committed, interrupted["idempotency_record"]);
        Assert.Equal(2 + committed, interrupted["logical_effect"]);

        var relationship = await Confirm(outflowId, inflowId, "Crash-atomic transfer", "crash-transfer");
        Assert.Equal("active", relationship.GetProperty("state").GetString());
        var recovered = await DurableCounts();
        Assert.Equal(1, recovered["financial_relationship"]);
        Assert.Equal(0, recovered["relationship_lifecycle_event"]);
        Assert.Equal(6, recovered["idempotency_record"]);
        Assert.Equal(4, recovered["logical_effect"]);
        var actuals = await QueryActuals("none");
        AssertTotals(actuals, "0", "0", "0");
        Assert.Equal(
            new[] { "transfer_inflow", "transfer_outflow" },
            actuals.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("relationshipState").GetString()).Order(StringComparer.Ordinal));
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

    private async Task<JsonElement> CreateAccount(string name, string suffix, string accountType, string key) => await Success(
        ["ledger", "account", "create", "--input", "-"],
        Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = name,
            ["accountType"] = accountType,
            ["maskedIdentifier"] = "****" + suffix,
            ["currencyCode"] = "ZAR"
        }, key),
        "ledger.account.create");

    private async Task<JsonElement> Record(string accountId, string amount, string date, int digestSeed) => await Success(
        ["ledger", "transaction", "record", "--input", "-"],
        Envelope(TransactionInput(accountId, amount, date, digestSeed), "record-" + digestSeed),
        "ledger.transaction.record");

    private async Task<JsonElement> CreateCategory() => await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "Bank movement", ["parentCategoryId"] = null }, "category-create"),
        "ledger.category.create");

    private async Task<JsonElement> CreatePool() => await Success(
        ["ledger", "pool", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "Personal" }, "pool-create"),
        "ledger.pool.create");

    private async Task AssignCategory(string transactionId, string categoryId, string key) => _ = await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classification" }, key),
        "ledger.transaction.category.assign");

    private async Task AssignPool(JsonElement transaction, string poolId, string key) => _ = await Success(
        ["ledger", "transaction", "pool", "assign", "--input", "-"],
        Envelope(new JsonObject
        {
            ["transactionId"] = transaction.GetProperty("transactionId").GetString(),
            ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
            ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId },
            ["reason"] = "Owner pool assignment"
        }, key),
        "ledger.transaction.pool.assign");

    private async Task<JsonElement> Confirm(string outflowId, string inflowId, string reason, string key) => await Success(
        ["ledger", "transfer", "confirm", "--input", "-"],
        TransferEnvelope(outflowId, inflowId, reason, key),
        "ledger.transfer.confirm");

    private Task<PublishedTallyResult> ConfirmResult(JsonElement outflow, JsonElement inflow, string reason, string key) => Run(
        ["ledger", "transfer", "confirm", "--input", "-"],
        TransferEnvelope(
            outflow.GetProperty("transactionId").GetString()!,
            inflow.GetProperty("transactionId").GetString()!,
            reason,
            key));

    private async Task<JsonElement> GetRelationship(string relationshipId) => await Success(
        ["ledger", "relationship", "get", "--input", "-"],
        Envelope(new JsonObject { ["relationshipId"] = relationshipId, ["includeHistory"] = true }),
        "ledger.relationship.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> QueryActuals(string groupBy) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject { ["groupBy"] = groupBy },
            ["pageSize"] = 100,
            ["cursor"] = null
        }),
        "ledger.actuals.query");

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

    private async Task<bool> KillPublishedProcessDuringMutation(string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "ledger", "transfer", "confirm", "--input", "-" }) start.ArgumentList.Add(argument);
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

    private static void AssertNoRelationship(IReadOnlyDictionary<string, long> counts)
    {
        Assert.Equal(0, counts["financial_relationship"]);
        Assert.Equal(0, counts["relationship_lifecycle_event"]);
    }

    private static void AssertTotals(JsonElement actuals, string net, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(net, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
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

    private static JsonObject TransactionInput(string accountId, string amount, string date, int digestSeed) => new()
    {
        ["accountId"] = accountId,
        ["signedAmount"] = amount,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = date,
        ["postingDate"] = null,
        ["originalDescription"] = "Owner-safe banking transaction",
        ["instrumentId"] = null,
        ["cardholderId"] = null,
        ["initialEvidence"] = new JsonObject
        {
            ["kind"] = "agent_capture",
            ["logicalIdentityDigest"] = Digest(digestSeed),
            ["opaqueExternalReference"] = "capture:" + digestSeed,
            ["contentFingerprint"] = null,
            ["observation"] = null
        }
    };

    private static string TransferEnvelope(string outflowId, string inflowId, string reason, string key) => Envelope(
        new JsonObject
        {
            ["outflowTransactionId"] = outflowId,
            ["inflowTransactionId"] = inflowId,
            ["reason"] = reason
        },
        key);

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject
            {
                ["kind"] = "automation",
                ["label"] = "uc004",
                ["runId"] = "published-e2e"
            },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static string Digest(int seed) => string.Concat(Enumerable.Repeat(
        seed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
        32));
}
