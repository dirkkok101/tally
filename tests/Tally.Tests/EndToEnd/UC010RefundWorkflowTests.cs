using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC010RefundWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc010-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UC_LEDGER_010_agent_discovers_full_refund_and_actuals_contracts()
    {
        var refund = (await Success(["schema", "show", "ledger.refund.confirm", "--input", "-"], Envelope(new JsonObject()), "system.schema.show"))
            .GetProperty("operation");
        var actuals = (await Success(["schema", "show", "ledger.actuals.query", "--input", "-"], Envelope(new JsonObject()), "system.schema.show"))
            .GetProperty("operation");

        Assert.EndsWith("ConfirmRefundInput", refund.GetProperty("requestType").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("FinancialRelationshipDetail", refund.GetProperty("resultType").GetString(), StringComparison.Ordinal);
        Assert.True(refund.GetProperty("requiresIdempotencyKey").GetBoolean());
        Assert.Contains(refund.GetProperty("errors").EnumerateArray(), error => error.GetProperty("code").GetString() == "LEDGER-REFUND-AMOUNT");
        Assert.EndsWith("QueryActualsInput", actuals.GetProperty("requestType").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UC_LEDGER_010_equal_active_same_account_zar_legs_create_immutable_attributable_full_refund()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 1);
        var credit = await Record(accountId, "12.34", "2026-07-20", 2);
        var originalBefore = await GetTransaction(Id(original));
        var creditBefore = await GetTransaction(Id(credit));

        var relationship = await Confirm(Id(original), Id(credit), "Owner confirmed full refund", "confirm");
        var fetched = await GetRelationship(relationship.GetProperty("relationshipId").GetString()!);

        Assert.Equal("refund", relationship.GetProperty("type").GetString());
        Assert.Equal(Id(original), relationship.GetProperty("sourceTransactionId").GetString());
        Assert.Equal("refund_original", relationship.GetProperty("sourceRole").GetString());
        Assert.Equal(Id(credit), relationship.GetProperty("targetTransactionId").GetString());
        Assert.Equal("refund_credit", relationship.GetProperty("targetRole").GetString());
        Assert.Equal("12.34", relationship.GetProperty("principalAmount").GetString());
        Assert.Equal("ZAR", relationship.GetProperty("currencyCode").GetString());
        Assert.Equal("active", relationship.GetProperty("state").GetString());
        Assert.Equal(relationship.GetRawText(), fetched.GetRawText());
        Assert.Equal(originalBefore.GetRawText(), (await GetTransaction(Id(original))).GetRawText());
        Assert.Equal(creditBefore.GetRawText(), (await GetTransaction(Id(credit))).GetRawText());
    }

    [Fact]
    public async Task UC_LEDGER_010_refund_credit_offsets_original_current_category_and_pool_in_credit_period_and_conserves_all_rollups()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-50", "2026-01-10", 3);
        var credit = await Record(accountId, "50", "2026-02-15", 4);
        var categoryId = await CreateCategory("Groceries", "category");
        var poolId = await CreatePool("Household", "pool");
        await AssignCategory(Id(original), categoryId, "category");
        await AssignPool(original, poolId, "pool");
        await Confirm(Id(original), Id(credit), "Later full refund", "confirm");

        foreach (var groupBy in new[] { "none", "pool", "category_direct", "pool_category" })
        {
            var actuals = await QueryActuals(groupBy, "2026-02-01", "2026-02-28");
            AssertTotals(actuals, "50", "-50", "-50");
            var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
            Assert.Equal(Id(credit), item.GetProperty("transactionId").GetString());
            Assert.Equal("refund_credit", item.GetProperty("relationshipState").GetString());
            Assert.Equal(categoryId, item.GetProperty("categoryId").GetString());
            Assert.Equal(poolId, item.GetProperty("poolId").GetString());
            Assert.Equal("-50", item.GetProperty("contribution").GetProperty("externalSpend").GetString());
            Assert.Equal("-50", item.GetProperty("contribution").GetProperty("budgetActual").GetString());
        }
    }

    [Fact]
    public async Task UC_LEDGER_010_later_category_and_pool_corrections_move_linked_offset_without_rewriting_facts_or_relationship()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-20", "2026-01-10", 5);
        var credit = await Record(accountId, "20", "2026-03-10", 6);
        var oldCategory = await CreateCategory("Old", "old-category");
        var newCategory = await CreateCategory("New", "new-category");
        var oldPool = await CreatePool("Old pool", "old-pool");
        var newPool = await CreatePool("New pool", "new-pool");
        await AssignCategory(Id(original), oldCategory, "category");
        await AssignPool(original, oldPool, "pool");
        var relationship = await Confirm(Id(original), Id(credit), "Full refund", "confirm");
        var originalBefore = await GetTransaction(Id(original));
        var creditBefore = await GetTransaction(Id(credit));

        await CorrectCategory(Id(original), newCategory, "category-correction");
        await CorrectPool(await GetTransaction(Id(original)), newPool, "pool-correction");

        var actuals = await QueryActuals("pool_category", "2026-03-01", "2026-03-31");
        AssertTotals(actuals, "20", "-20", "-20");
        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal(newCategory, item.GetProperty("categoryId").GetString());
        Assert.Equal(newPool, item.GetProperty("poolId").GetString());
        Assert.Equal("-20", item.GetProperty("contribution").GetProperty("budgetActual").GetString());
        Assert.Equal(relationship.GetRawText(), (await GetRelationship(relationship.GetProperty("relationshipId").GetString()!)).GetRawText());
        AssertFactUnchanged(originalBefore, await GetTransaction(Id(original)));
        Assert.Equal(creditBefore.GetRawText(), (await GetTransaction(Id(credit))).GetRawText());
    }

    [Theory]
    [InlineData("2026-02-01", "2026-02-28", "12.34", "-12.34", "-12.34")]
    [InlineData("2026-01-01", "2026-01-31", "-12.34", "12.34", "12.34")]
    public async Task UC_LEDGER_010_effective_periods_keep_later_credit_offset_and_original_negative_period_exact(string from, string to, string net, string spend, string budget)
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-01-15", 7);
        var credit = await Record(accountId, "12.34", "2026-02-15", 8);
        await Confirm(Id(original), Id(credit), "Different effective period", "confirm");

        AssertTotals(await QueryActuals("none", from, to), net, spend, budget);
    }

    [Fact]
    public async Task UC_LEDGER_010_missing_or_currency_incompatible_public_inputs_are_rejected_without_relationship()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 9);
        var missing = await Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(Id(original), "01J00000000000000000000000", "Missing", "missing"));
        AssertError(missing, 4, "LEDGER-TRANSACTION-NOT-FOUND");

        var unsupported = TransactionInput(accountId, "12.34", "2026-07-20", 10);
        unsupported["currencyCode"] = "USD";
        AssertError(await Run(["ledger", "transaction", "record", "--input", "-"], Envelope(unsupported, "unsupported-currency")), 3, "currency.unsupported");
        AssertNoRelationships(await QueryActuals("none", null, null));
    }

    [Fact]
    public async Task UC_LEDGER_010_different_account_is_rejected_without_mutation()
    {
        var originalAccount = await CreateAccount("Original", "1111", "original-account");
        var creditAccount = await CreateAccount("Credit", "2222", "credit-account");
        var original = await Record(originalAccount, "-12.34", "2026-07-01", 11);
        var credit = await Record(creditAccount, "12.34", "2026-07-20", 12);
        AssertError(await ConfirmResult(Id(original), Id(credit), "Wrong account", "confirm"), 3, "LEDGER-REFUND-ACCOUNT");
        AssertNoRelationships(await QueryActuals("none", null, null));
    }

    [Theory]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("12.34", "-12.34")]
    public async Task UC_LEDGER_010_invalid_refund_sign_roles_are_rejected_without_mutation(string originalAmount, string creditAmount)
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, originalAmount, "2026-07-01", 13);
        var credit = await Record(accountId, creditAmount, "2026-07-20", 14);
        AssertError(await ConfirmResult(Id(original), Id(credit), "Wrong signs", "confirm"), 3, "LEDGER-REFUND-SIGN");
        AssertNoRelationships(await QueryActuals("none", null, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UC_LEDGER_010_inactive_original_or_credit_is_rejected_without_mutation(bool voidOriginal)
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 15);
        var credit = await Record(accountId, "12.34", "2026-07-20", 16);
        await Success(["ledger", "transaction", "void", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = voidOriginal ? Id(original) : Id(credit), ["reason"] = "Voided" }, "void"), "ledger.transaction.void");
        AssertError(await ConfirmResult(Id(original), Id(credit), "Inactive", "confirm"), 6, "LEDGER-REFUND-TRANSACTION-INACTIVE");
        AssertNoRelationships(await QueryActuals("none", null, null));
    }

    [Theory]
    [InlineData("12.33")]
    [InlineData("12.35")]
    public async Task UC_LEDGER_010_partial_and_over_refunds_are_rejected_without_mutation(string amount)
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 17);
        var credit = await Record(accountId, amount, "2026-07-20", 18);
        AssertError(await ConfirmResult(Id(original), Id(credit), "Not full", "confirm"), 3, "LEDGER-REFUND-AMOUNT");
        AssertNoRelationships(await QueryActuals("none", null, null));
    }

    [Fact]
    public async Task UC_LEDGER_010_second_active_refund_and_credit_role_reuse_are_rejected_without_mutation()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var firstOriginal = await Record(accountId, "-12.34", "2026-07-01", 19);
        var secondOriginal = await Record(accountId, "-12.34", "2026-07-02", 20);
        var firstCredit = await Record(accountId, "12.34", "2026-07-20", 21);
        var secondCredit = await Record(accountId, "12.34", "2026-07-21", 22);
        await Confirm(Id(firstOriginal), Id(firstCredit), "First", "first");
        AssertError(await ConfirmResult(Id(firstOriginal), Id(secondCredit), "Second refund", "second"), 5, "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT");
        AssertError(await ConfirmResult(Id(secondOriginal), Id(firstCredit), "Reused credit", "reuse"), 5, "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT");
        Assert.Single((await QueryActuals("none", null, null)).GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() == "refund_original");
        Assert.Single((await QueryActuals("none", null, null)).GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() == "refund_credit");
    }

    [Fact]
    public async Task UC_LEDGER_010_identical_semantic_replay_returns_original_and_changed_replay_conflicts()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 23);
        var credit = await Record(accountId, "12.34", "2026-07-20", 24);
        var request = RefundEnvelope(Id(original), Id(credit), "Owner confirmed", "same-key");
        var first = await Run(["ledger", "refund", "confirm", "--input", "-"], request);
        var replay = await Run(["ledger", "refund", "confirm", "--input", "-"], request);
        var crossKey = await Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(Id(original), Id(credit), "Owner confirmed", "other-key"));
        var changed = await Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(Id(original), Id(credit), "Changed reason", "same-key"));

        AssertSuccess(first, "ledger.refund.confirm");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task UC_LEDGER_010_concurrent_competing_refunds_commit_exactly_one_active_relationship()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 25);
        var firstCredit = await Record(accountId, "12.34", "2026-07-20", 26);
        var secondCredit = await Record(accountId, "12.34", "2026-07-21", 27);
        var results = await Task.WhenAll(
            Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(Id(original), Id(firstCredit), "First", "first")),
            Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(Id(original), Id(secondCredit), "Second", "second")));

        var committed = AssertSuccess(Assert.Single(results, result => result.ExitCode == 0), "ledger.refund.confirm");
        AssertConcurrencyConflict(Assert.Single(results, result => result.ExitCode != 0));
        var losingCreditId = committed.GetProperty("targetTransactionId").GetString() == Id(firstCredit) ? Id(secondCredit) : Id(firstCredit);
        AssertError(await ConfirmResult(Id(original), losingCreditId, "Retry loser", "retry-loser"), 5, "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT");
        Assert.Single((await QueryActuals("none", null, null)).GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() == "refund_original");
    }

    [Fact]
    public async Task UC_LEDGER_010_injected_write_failure_leaves_complete_refund_or_none_and_retry_converges()
    {
        var accountId = await CreateAccount("Primary", "1111", "account");
        var original = await Record(accountId, "-12.34", "2026-07-01", 28);
        var credit = await Record(accountId, "12.34", "2026-07-20", 29);
        var request = RefundEnvelope(Id(original), Id(credit), "Crash atomic refund", "crash-refund");

        Assert.True(await KillPublishedProcessDuringMutation(request), "The published process completed before the write failure could be injected.");
        var interrupted = await QueryActuals("none", null, null);
        var interruptedOriginals = interrupted.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("relationshipState").GetString() == "refund_original");
        var interruptedCredits = interrupted.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("relationshipState").GetString() == "refund_credit");
        Assert.True(interruptedOriginals is 0 or 1);
        Assert.Equal(interruptedOriginals, interruptedCredits);

        var relationship = await Confirm(Id(original), Id(credit), "Crash atomic refund", "crash-refund");
        Assert.Equal("active", relationship.GetProperty("state").GetString());
        var actuals = await QueryActuals("none", null, null);
        Assert.Equal(1, actuals.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("relationshipState").GetString() == "refund_original"));
        Assert.Equal(1, actuals.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("relationshipState").GetString() == "refund_credit"));
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

    private async Task<string> CreateAccount(string name, string suffix, string key) =>
        (await Success(["ledger", "account", "create", "--input", "-"], Envelope(new JsonObject { ["institutionName"] = "Example Bank", ["displayName"] = name, ["accountType"] = "cheque", ["maskedIdentifier"] = "****" + suffix, ["currencyCode"] = "ZAR" }, key), "ledger.account.create")).GetProperty("accountId").GetString()!;

    private async Task<JsonElement> Record(string accountId, string amount, string date, int seed) =>
        await Success(["ledger", "transaction", "record", "--input", "-"], Envelope(TransactionInput(accountId, amount, date, seed), "record-" + seed), "ledger.transaction.record");

    private async Task<string> CreateCategory(string name, string key) =>
        (await Success(["ledger", "category", "create", "--input", "-"], Envelope(new JsonObject { ["name"] = name, ["parentCategoryId"] = null }, key), "ledger.category.create")).GetProperty("categoryId").GetString()!;

    private async Task<string> CreatePool(string name, string key) =>
        (await Success(["ledger", "pool", "create", "--input", "-"], Envelope(new JsonObject { ["name"] = name }, key), "ledger.pool.create")).GetProperty("poolId").GetString()!;

    private async Task AssignCategory(string transactionId, string categoryId, string key) => _ = await Success(["ledger", "transaction", "category", "assign", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classification" }, "category-assign-" + key), "ledger.transaction.category.assign");

    private async Task CorrectCategory(string transactionId, string categoryId, string key)
    {
        var current = await GetTransaction(transactionId);
        _ = await Success(["ledger", "transaction", "category", "correct", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner corrected classification" }, "category-correct-" + key), "ledger.transaction.category.correct");
    }

    private async Task AssignPool(JsonElement transaction, string poolId, string key) => _ = await Success(["ledger", "transaction", "pool", "assign", "--input", "-"], PoolEnvelope(transaction, poolId, "Owner pool assignment", "pool-assign-" + key), "ledger.transaction.pool.assign");

    private async Task CorrectPool(JsonElement transaction, string poolId, string key) => _ = await Success(["ledger", "transaction", "pool", "correct", "--input", "-"], PoolEnvelope(transaction, poolId, "Owner pool correction", "pool-correct-" + key), "ledger.transaction.pool.correct");

    private async Task<JsonElement> Confirm(string originalId, string creditId, string reason, string key) => await Success(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(originalId, creditId, reason, key), "ledger.refund.confirm");

    private Task<PublishedTallyResult> ConfirmResult(string originalId, string creditId, string reason, string key) => Run(["ledger", "refund", "confirm", "--input", "-"], RefundEnvelope(originalId, creditId, reason, key));

    private async Task<JsonElement> GetRelationship(string relationshipId) => await Success(["ledger", "relationship", "get", "--input", "-"], Envelope(new JsonObject { ["relationshipId"] = relationshipId, ["includeHistory"] = true }), "ledger.relationship.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(["ledger", "transaction", "get", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }), "ledger.transaction.get");

    private async Task<JsonElement> QueryActuals(string groupBy, string? from, string? to) => await Success(["ledger", "actuals", "query", "--input", "-"], Envelope(new JsonObject { ["filter"] = new JsonObject { ["groupBy"] = groupBy, ["effectiveFrom"] = from, ["effectiveTo"] = to }, ["pageSize"] = 100, ["cursor"] = null }), "ledger.actuals.query");

    private async Task<bool> KillPublishedProcessDuringMutation(string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "ledger", "refund", "confirm", "--input", "-" }) start.ArgumentList.Add(argument);
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

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertConcurrencyConflict(PublishedTallyResult result)
    {
        Assert.Equal(5, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var code = document.RootElement.GetProperty("error").GetProperty("code").GetString();
        Assert.Contains(code, new[] { "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT", "operation.conflict" });
        Assert.Equal("tally: " + code, result.Stderr);
    }

    private static void AssertTotals(JsonElement actuals, string net, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(net, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
    }

    private static void AssertNoRelationships(JsonElement actuals) => Assert.DoesNotContain(actuals.GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() is "refund_original" or "refund_credit");

    private static void AssertFactUnchanged(JsonElement before, JsonElement after)
    {
        foreach (var property in new[] { "transactionId", "accountId", "signedAmount", "currencyCode", "transactionDate", "postingDate", "effectiveDate", "originalDescription", "lifecycleStatus", "reconciliationState", "evidence", "paymentAttribution" }) Assert.Equal(before.GetProperty(property).GetRawText(), after.GetProperty(property).GetRawText());
    }

    private static string Id(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;

    private static JsonObject TransactionInput(string accountId, string amount, string date, int seed) => new()
    {
        ["accountId"] = accountId,
        ["signedAmount"] = amount,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = date,
        ["postingDate"] = null,
        ["originalDescription"] = "Owner-safe banking transaction",
        ["instrumentId"] = null,
        ["cardholderId"] = null,
        ["initialEvidence"] = new JsonObject { ["kind"] = "agent_capture", ["logicalIdentityDigest"] = Digest(seed), ["opaqueExternalReference"] = "capture:" + seed, ["contentFingerprint"] = null, ["observation"] = null }
    };

    private static string RefundEnvelope(string originalId, string creditId, string reason, string key) => Envelope(new JsonObject { ["originalTransactionId"] = originalId, ["refundTransactionId"] = creditId, ["reason"] = reason }, key);

    private static string PoolEnvelope(JsonElement transaction, string poolId, string reason, string action) => Envelope(new JsonObject { ["transactionId"] = Id(transaction), ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(), ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId }, ["reason"] = reason }, action);

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject { ["contractVersion"] = "1.0", ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc010", ["runId"] = "published-e2e" }, ["input"] = input };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static string Digest(int seed) => string.Concat(Enumerable.Repeat(seed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
}
