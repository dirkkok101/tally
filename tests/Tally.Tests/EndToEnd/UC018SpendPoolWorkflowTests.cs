using System.Diagnostics;
using System.Globalization;
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
public sealed class UC018SpendPoolWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc018-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_018_agent_discovers_pool_catalogue_assignment_and_actuals_contracts()
    {
        foreach (var (operationId, requestType, resultType, mutates) in new[]
                 {
                     ("ledger.pool.create", "CreateSpendPoolInput", "SpendPoolDetail", true),
                     ("ledger.pool.get", "GetSpendPoolInput", "SpendPoolDetail", false),
                     ("ledger.pool.list", "ListSpendPoolsInput", "SpendPoolListResult", false),
                     ("ledger.pool.rename", "RenameSpendPoolInput", "SpendPoolLifecycleResult", true),
                     ("ledger.pool.archive", "ArchiveSpendPoolInput", "SpendPoolLifecycleResult", true),
                     ("ledger.pool.reactivate", "ReactivateSpendPoolInput", "SpendPoolLifecycleResult", true),
                     ("ledger.transaction.pool.assign", "AssignPoolInput", "PoolAssignmentResult", true),
                     ("ledger.transaction.pool.correct", "CorrectPoolInput", "PoolAssignmentResult", true),
                     ("ledger.actuals.query", "QueryActualsInput", "ActualsQueryResult", false)
                 })
        {
            var operation = (await Success(["schema", "show", operationId, "--input", "-"], Envelope(new JsonObject()), "system.schema.show"))
                .GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(resultType, operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.Equal(mutates, operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_lifecycle_preserves_identity_name_and_history()
    {
        var created = await CreatePool("Personal after-tax", Key("create"));
        var poolId = PoolId(created);
        var renamed = await Lifecycle("rename", poolId, "Personal spending", "Owner clarified pool", Key("rename"));
        var archived = await Lifecycle("archive", poolId, null, "Owner archived pool", Key("archive"));
        var reactivated = await Lifecycle("reactivate", poolId, null, "Owner restored pool", Key("reactivate"));
        var current = await GetPool(poolId, true);

        Assert.Equal(poolId, PoolId(renamed.GetProperty("pool")));
        Assert.Equal(poolId, PoolId(archived.GetProperty("pool")));
        Assert.Equal(poolId, PoolId(reactivated.GetProperty("pool")));
        Assert.Equal("Personal spending", current.GetProperty("name").GetString());
        Assert.Equal("active", current.GetProperty("status").GetString());
        Assert.Equal(
            ["create", "rename", "archive", "reactivate"],
            current.GetProperty("lifecycleHistory").EnumerateArray().Select(item => item.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_list_filters_are_explicit_and_other_dimensions_do_not_create_pools()
    {
        var accountId = await CreateAccount("Independent dimensions");
        _ = await CreateCategory("Independent category");
        _ = await CreateInstrument(accountId, "Independent instrument");
        _ = await CreateCardholder("Independent owner");
        var active = await CreatePool("Active pool", Key("active"));
        var archived = await CreatePool("Archived pool", Key("archived"));
        await Lifecycle("archive", PoolId(archived), null, "Archive for filter", Key("archive"));

        var activeItems = (await ListPools("active")).GetProperty("items").EnumerateArray().ToArray();
        var archivedItems = (await ListPools("archived")).GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([PoolId(active)], activeItems.Select(PoolId));
        Assert.Equal([PoolId(archived)], archivedItems.Select(PoolId));
        Assert.Equal(2, (await ListPools()).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_duplicate_and_idempotent_create_are_stable()
    {
        var input = new JsonObject { ["name"] = "Company discretionary" };
        var request = Envelope(input, "pool-replay-key");
        var first = await Run(["ledger", "pool", "create", "--input", "-"], request);
        var replay = await Run(["ledger", "pool", "create", "--input", "-"], request);
        var changed = await Run(
            ["ledger", "pool", "create", "--input", "-"],
            Envelope(new JsonObject { ["name"] = "Personal after-tax" }, "pool-replay-key"));
        var duplicate = await Run(
            ["ledger", "pool", "create", "--input", "-"],
            Envelope(new JsonObject { ["name"] = " company DISCRETIONARY " }, Key("duplicate")));

        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        AssertError(duplicate, 5, "LEDGER-SPEND-POOL-DUPLICATE");
        Assert.Single((await ListPools()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_SPEND_POOL_CATALOGUE_archival_blocks_assignment_until_same_identity_reactivates()
    {
        var accountId = await CreateAccount("Archived assignment");
        var transaction = await Record(accountId, "-10", "2026-07-01", "Archived pool purchase");
        var pool = await CreatePool("Temporarily archived", Key("pool"));
        var poolId = PoolId(pool);
        await Lifecycle("archive", poolId, null, "Temporarily unavailable", Key("archive"));

        AssertError(await AssignResult(transaction, Assigned(poolId), "Owner selected archived pool", Key("blocked")), 6, "LEDGER-SPEND-POOL-ARCHIVED");
        Assert.Single((await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").EnumerateArray());

        await Lifecycle("reactivate", poolId, null, "Owner restored pool", Key("reactivate"));
        var assigned = AssertSuccess(await AssignResult(transaction, Assigned(poolId), "Owner selected restored pool", Key("assign")), "ledger.transaction.pool.assign");
        Assert.Equal(poolId, assigned.GetProperty("transaction").GetProperty("pool").GetProperty("poolId").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_assigns_and_corrects_independently_with_attributable_history()
    {
        var accountId = await CreateAccount("Independent assignment");
        var instrumentId = await CreateInstrument(accountId, "Daily card");
        var cardholderId = await CreateCardholder("Owner");
        var categoryId = await CreateCategory("Groceries");
        var firstPool = PoolId(await CreatePool("Company", Key("first-pool")));
        var secondPool = PoolId(await CreatePool("Personal", Key("second-pool")));
        var transaction = await Record(accountId, "-12.34", "2026-07-02", "Independent purchase", instrumentId, cardholderId);
        await AssignCategory(TransactionId(transaction), categoryId);
        var before = await GetTransaction(TransactionId(transaction));

        var assigned = await Assign(before, Assigned(firstPool), "Owner selected company pool", Key("assign"));
        var corrected = await Correct(assigned.GetProperty("transaction"), Assigned(secondPool), "Owner corrected pool", Key("correct"));
        var current = corrected.GetProperty("transaction");

        Assert.Equal(secondPool, current.GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal(categoryId, current.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(instrumentId, current.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal(cardholderId, current.GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        Assert.Equal(accountId, current.GetProperty("accountId").GetString());
        Assert.Equal(["initialize", "assign", "correct"], current.GetProperty("history").GetProperty("poolAssignments").EnumerateArray().Select(item => item.GetProperty("action").GetString()));
        Assert.Equal(0, (await GetPool(firstPool, false)).GetProperty("currentAssignmentCount").GetInt64());
        Assert.Equal(1, (await GetPool(firstPool, false)).GetProperty("historicalAssignmentCount").GetInt64());
        Assert.Equal(1, (await GetPool(secondPool, false)).GetProperty("currentAssignmentCount").GetInt64());
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_correction_to_explicit_unassigned_is_visible_and_conserved()
    {
        var accountId = await CreateAccount("Explicit unassigned");
        var categoryId = await CreateCategory("Unassigned category");
        var poolId = PoolId(await CreatePool("Temporary pool", Key("pool")));
        var transaction = await Record(accountId, "-7.50", "2026-07-03", "Unassigned correction");
        await AssignCategory(TransactionId(transaction), categoryId);
        var assigned = await Assign(await GetTransaction(TransactionId(transaction)), Assigned(poolId), "Initial pool", Key("assign"));

        var corrected = await Correct(assigned.GetProperty("transaction"), Unassigned(), "Owner removed pool", Key("correct"));
        var actuals = await Actuals([accountId]);
        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        var group = Assert.Single(actuals.GetProperty("groups").EnumerateArray());

        Assert.Equal("unassigned", corrected.GetProperty("transaction").GetProperty("pool").GetProperty("state").GetString());
        Assert.Null(corrected.GetProperty("transaction").GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal("unassigned", item.GetProperty("poolState").GetString());
        Assert.Null(item.GetProperty("poolId").GetString());
        Assert.Equal("unassigned", group.GetProperty("poolState").GetString());
        Assert.Equal(categoryId, group.GetProperty("categoryId").GetString());
        AssertConservation(actuals);
    }

    [Theory]
    [InlineData("missing", 4, "LEDGER-SPEND-POOL-NOT-FOUND")]
    [InlineData("archived", 6, "LEDGER-SPEND-POOL-ARCHIVED")]
    [InlineData("inactive", 6, "LEDGER-POOL-ASSIGNMENT-TRANSACTION-INACTIVE")]
    [InlineData("blank-reason", 3, "LEDGER-POOL-ASSIGNMENT-INVALID")]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_invalid_assignment_is_atomic(string scenario, int exitCode, string errorCode)
    {
        var accountId = await CreateAccount("Invalid assignment");
        var transaction = await Record(accountId, "-9", "2026-07-04", "Invalid assignment");
        var poolId = PoolId(await CreatePool("Candidate pool", Key("pool")));
        if (scenario == "archived") await Lifecycle("archive", poolId, null, "Archive test pool", Key("archive"));
        if (scenario == "inactive") await Void(TransactionId(transaction), "Owner voided transaction");
        var selectedPoolId = scenario == "missing" ? "01J00000000000000000000000" : poolId;
        var before = await Actuals([accountId], lifecycleStates: scenario == "inactive" ? ["voided"] : null);

        var result = await AssignResult(transaction, Assigned(selectedPoolId), scenario == "blank-reason" ? "" : "Owner selected pool", Key("invalid"));

        AssertError(result, exitCode, errorCode);
        Assert.Single((await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").EnumerateArray());
        AssertActualsEqual(before, await Actuals([accountId], lifecycleStates: scenario == "inactive" ? ["voided"] : null));
    }

    [Theory]
    [InlineData("stale", "LEDGER-POOL-ASSIGNMENT-STALE")]
    [InlineData("unchanged", "LEDGER-POOL-ASSIGNMENT-UNCHANGED")]
    [InlineData("assign-again", "LEDGER-POOL-ASSIGNMENT-ALREADY-ASSIGNED")]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_stale_or_repeated_state_is_rejected(string scenario, string errorCode)
    {
        var transaction = await Record(await CreateAccount("State conflict"), "-8", "2026-07-05", "State conflict");
        var poolId = PoolId(await CreatePool("Assigned pool", Key("pool")));
        var assigned = await Assign(transaction, Assigned(poolId), "Initial assignment", Key("assign"));
        var current = assigned.GetProperty("transaction");
        PublishedTallyResult result;
        if (scenario == "stale") result = await CorrectResult(transaction, Unassigned(), "Stale correction", Key("stale"));
        else if (scenario == "unchanged") result = await CorrectResult(current, Assigned(poolId), "No state change", Key("unchanged"));
        else result = await AssignResult(current, Unassigned(), "Second assignment", Key("again"));

        AssertError(result, 5, errorCode);
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").GetArrayLength());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_assignment_replay_converges_and_changed_input_conflicts()
    {
        var transaction = await Record(await CreateAccount("Replay assignment"), "-6", "2026-07-06", "Replay assignment");
        var poolId = PoolId(await CreatePool("Replay pool", Key("pool")));
        var input = PoolInput(transaction, Assigned(poolId), "Owner selected pool");
        var request = Envelope(input, "pool-assignment-replay");

        var first = await Run(["ledger", "transaction", "pool", "assign", "--input", "-"], request);
        var replay = await Run(["ledger", "transaction", "pool", "assign", "--input", "-"], request);
        var changed = input.DeepClone().AsObject();
        changed["reason"] = "Changed reason";
        var conflict = await Run(["ledger", "transaction", "pool", "assign", "--input", "-"], Envelope(changed, "pool-assignment-replay"));

        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").GetArrayLength());
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_full_refund_tracks_original_current_pool_and_category_in_credit_period()
    {
        var accountId = await CreateAccount("Refund");
        var original = await Record(accountId, "-40", "2026-01-10", "Refunded purchase");
        var credit = await Record(accountId, "40", "2026-02-15", "Full refund");
        var oldCategory = await CreateCategory("Old refund category");
        var newCategory = await CreateCategory("New refund category");
        var oldPool = PoolId(await CreatePool("Old refund pool", Key("old-pool")));
        var newPool = PoolId(await CreatePool("New refund pool", Key("new-pool")));
        await AssignCategory(TransactionId(original), oldCategory);
        await Assign(await GetTransaction(TransactionId(original)), Assigned(oldPool), "Initial refund pool", Key("assign"));
        await Success(
            ["ledger", "refund", "confirm", "--input", "-"],
            Envelope(new JsonObject { ["originalTransactionId"] = TransactionId(original), ["refundTransactionId"] = TransactionId(credit), ["reason"] = "Owner confirmed full refund" }, Key("refund")),
            "ledger.refund.confirm");
        await CorrectCategory(TransactionId(original), newCategory);
        await Correct(await GetTransaction(TransactionId(original)), Assigned(newPool), "Owner corrected refund pool", Key("correct-pool"));

        var actuals = await Actuals([accountId], "2026-02-01", "2026-02-28");
        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal("refund_credit", item.GetProperty("relationshipState").GetString());
        Assert.Equal(newCategory, item.GetProperty("categoryId").GetString());
        Assert.Equal(newPool, item.GetProperty("poolId").GetString());
        AssertTotals(actuals, "40", "-40", "-40");
        AssertConservation(actuals);
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_transfer_principal_is_zero_in_every_pool_while_fee_remains_spend()
    {
        var outAccount = await CreateAccount("Transfer out");
        var inAccount = await CreateAccount("Transfer in");
        var outflow = await Record(outAccount, "-100", "2026-03-01", "Transfer out");
        var inflow = await Record(inAccount, "100", "2026-03-01", "Transfer in");
        var fee = await Record(outAccount, "-2", "2026-03-01", "Transfer fee");
        var outPool = PoolId(await CreatePool("Outflow pool", Key("out-pool")));
        var inPool = PoolId(await CreatePool("Inflow pool", Key("in-pool")));
        var feePool = PoolId(await CreatePool("Fee pool", Key("fee-pool")));
        var categoryId = await CreateCategory("Bank fees");
        await Assign(await GetTransaction(TransactionId(outflow)), Assigned(outPool), "Historical outflow pool", Key("assign-out"));
        await Assign(await GetTransaction(TransactionId(inflow)), Assigned(inPool), "Historical inflow pool", Key("assign-in"));
        await AssignCategory(TransactionId(fee), categoryId);
        await Assign(await GetTransaction(TransactionId(fee)), Assigned(feePool), "Fee pool", Key("assign-fee"));
        await Success(
            ["ledger", "transfer", "confirm", "--input", "-"],
            Envelope(new JsonObject { ["outflowTransactionId"] = TransactionId(outflow), ["inflowTransactionId"] = TransactionId(inflow), ["reason"] = "Owner confirmed owned transfer" }, Key("transfer")),
            "ledger.transfer.confirm");

        var actuals = await Actuals([outAccount, inAccount]);
        Assert.Equal(3, actuals.GetProperty("totalCount").GetInt32());
        Assert.All(
            actuals.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("relationshipState").GetString()!.StartsWith("transfer_", StringComparison.Ordinal)),
            item =>
            {
                Assert.Equal("0", item.GetProperty("contribution").GetProperty("externalSpend").GetString());
                Assert.Equal("0", item.GetProperty("contribution").GetProperty("budgetActual").GetString());
            });
        var feeItem = Assert.Single(actuals.GetProperty("items").EnumerateArray(), item => item.GetProperty("transactionId").GetString() == TransactionId(fee));
        Assert.Equal(feePool, feeItem.GetProperty("poolId").GetString());
        AssertTotals(actuals, "-2", "2", "2");
        AssertConservation(actuals);
    }

    [Fact]
    public async Task TC_LEDGER_DIMENSIONAL_ACTUALS_pool_and_category_corrections_move_one_exact_cell_without_changing_totals()
    {
        var accountId = await CreateAccount("Cell correction");
        var oldCategory = await CreateCategory("Old cell category");
        var newCategory = await CreateCategory("New cell category");
        var oldPool = PoolId(await CreatePool("Old cell pool", Key("old-pool")));
        var newPool = PoolId(await CreatePool("New cell pool", Key("new-pool")));
        var transaction = await Record(accountId, "-15", "2026-04-01", "Cell correction");
        await AssignCategory(TransactionId(transaction), oldCategory);
        await Assign(await GetTransaction(TransactionId(transaction)), Assigned(oldPool), "Initial cell", Key("assign"));
        var before = await Actuals([accountId]);

        await CorrectCategory(TransactionId(transaction), newCategory);
        await Correct(await GetTransaction(TransactionId(transaction)), Assigned(newPool), "Owner moved pool", Key("correct"));
        var after = await Actuals([accountId]);
        var item = Assert.Single(after.GetProperty("items").EnumerateArray());

        Assert.Equal(oldPool, Assert.Single(before.GetProperty("groups").EnumerateArray()).GetProperty("poolId").GetString());
        Assert.Equal(newPool, item.GetProperty("poolId").GetString());
        Assert.Equal(newCategory, item.GetProperty("categoryId").GetString());
        Assert.Equal(before.GetProperty("totals").GetRawText(), after.GetProperty("totals").GetRawText());
        AssertConservation(before);
        AssertConservation(after);
    }

    [Theory]
    [InlineData("void")]
    [InlineData("supersede")]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_transaction_correction_preserves_history_and_exact_current_actuals(string scenario)
    {
        var accountId = await CreateAccount("Transaction correction");
        var categoryId = await CreateCategory("Correction category");
        var poolId = PoolId(await CreatePool("Correction pool", Key("pool")));
        var original = await Record(accountId, "-11", "2026-05-01", "Correction source");
        await AssignCategory(TransactionId(original), categoryId);
        await Assign(await GetTransaction(TransactionId(original)), Assigned(poolId), "Original pool", Key("assign"));

        if (scenario == "void")
        {
            await Void(TransactionId(original), "Owner voided source");
            var actuals = await Actuals([accountId]);
            Assert.Empty(actuals.GetProperty("items").EnumerateArray());
            AssertTotals(actuals, "0", "0", "0");
            AssertConservation(actuals);
        }
        else
        {
            var replacement = TransactionInput(accountId, "-12", "2026-05-01", "Corrected replacement");
            var result = await Success(
                ["ledger", "transaction", "supersede", "--input", "-"],
                Envelope(new JsonObject { ["transactionId"] = TransactionId(original), ["replacement"] = replacement, ["reason"] = "Owner corrected transaction" }, Key("supersede")),
                "ledger.transaction.supersede");
            var replacementId = result.GetProperty("replacement").GetProperty("transactionId").GetString()!;
            var actuals = await Actuals([accountId]);
            var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
            Assert.Equal(replacementId, item.GetProperty("transactionId").GetString());
            Assert.Equal("unassigned", item.GetProperty("poolState").GetString());
            Assert.Equal("uncategorized", item.GetProperty("categoryState").GetString());
            AssertTotals(actuals, "-12", "12", "12");
            AssertConservation(actuals);
        }

        var prior = await GetTransaction(TransactionId(original));
        Assert.Equal(2, prior.GetProperty("history").GetProperty("poolAssignments").GetArrayLength());
        Assert.Equal(poolId, prior.GetProperty("pool").GetProperty("poolId").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_POOL_ASSIGNMENT_interrupted_write_commits_none_or_one_and_same_request_converges()
    {
        var transaction = await Record(await CreateAccount("Interrupted assignment"), "-5", "2026-06-01", "Interrupted assignment");
        var poolId = PoolId(await CreatePool("Interrupted pool", Key("pool")));
        var request = Envelope(PoolInput(transaction, Assigned(poolId), "Crash-atomic pool assignment"), "pool-crash-key");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "transaction", "pool", "assign", "--input", "-"], request),
            "The published process completed before interruption.");
        Assert.InRange((await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").GetArrayLength(), 1, 2);

        var converged = AssertSuccess(await Run(["ledger", "transaction", "pool", "assign", "--input", "-"], request), "ledger.transaction.pool.assign");
        Assert.Equal(poolId, converged.GetProperty("transaction").GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal(2, (await GetTransaction(TransactionId(transaction))).GetProperty("history").GetProperty("poolAssignments").GetArrayLength());
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
        var suffix = (++sequence).ToString("D4", CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC018 " + label + " " + suffix,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> CreatePool(string name, string key) => await Success(
        ["ledger", "pool", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name }, key),
        "ledger.pool.create");

    private async Task<JsonElement> GetPool(string poolId, bool includeHistory) => await Success(
        ["ledger", "pool", "get", "--input", "-"],
        Envelope(new JsonObject { ["poolId"] = poolId, ["includeHistory"] = includeHistory }),
        "ledger.pool.get");

    private async Task<JsonElement> ListPools(string? status = null) => await Success(
        ["ledger", "pool", "list", "--input", "-"],
        Envelope(new JsonObject { ["status"] = status }),
        "ledger.pool.list");

    private async Task<JsonElement> Lifecycle(string action, string poolId, string? newName, string reason, string key)
    {
        var input = new JsonObject { ["poolId"] = poolId, ["reason"] = reason };
        if (action == "rename") input["newName"] = newName;
        return await Success(["ledger", "pool", action, "--input", "-"], Envelope(input, key), $"ledger.pool.{action}");
    }

    private async Task<string> CreateCategory(string name) => (await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name + " " + Key("category-name"), ["parentCategoryId"] = null }, Key("category")),
        "ledger.category.create")).GetProperty("categoryId").GetString()!;

    private async Task<string> CreateInstrument(string accountId, string label) => (await Success(
        ["ledger", "instrument", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label, ["accountId"] = accountId, ["maskedSuffix"] = "1818" }, Key("instrument")),
        "ledger.instrument.create")).GetProperty("instrumentId").GetString()!;

    private async Task<string> CreateCardholder(string label) => (await Success(
        ["ledger", "cardholder", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label + " " + Key("cardholder-name") }, Key("cardholder")),
        "ledger.cardholder.create")).GetProperty("cardholderId").GetString()!;

    private async Task<JsonElement> Record(
        string accountId,
        string amount,
        string date,
        string description,
        string? instrumentId = null,
        string? cardholderId = null) => await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(TransactionInput(accountId, amount, date, description, instrumentId, cardholderId), Key("record")),
            "ledger.transaction.record");

    private JsonObject TransactionInput(
        string accountId,
        string amount,
        string date,
        string description,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var token = Key("evidence");
        return new JsonObject
        {
            ["accountId"] = accountId,
            ["signedAmount"] = amount,
            ["currencyCode"] = "ZAR",
            ["transactionDate"] = date,
            ["postingDate"] = null,
            ["originalDescription"] = description,
            ["instrumentId"] = instrumentId,
            ["cardholderId"] = cardholderId,
            ["initialEvidence"] = new JsonObject
            {
                ["kind"] = "agent_capture",
                ["logicalIdentityDigest"] = Digest(token),
                ["opaqueExternalReference"] = "capture:" + token,
                ["contentFingerprint"] = null,
                ["observation"] = null
            }
        };
    }

    private async Task AssignCategory(string transactionId, string categoryId) => _ = await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classified transaction" }, Key("category-assign")),
        "ledger.transaction.category.assign");

    private async Task CorrectCategory(string transactionId, string categoryId) => _ = await Success(
        ["ledger", "transaction", "category", "correct", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner corrected category" }, Key("category-correct")),
        "ledger.transaction.category.correct");

    private async Task<JsonElement> Assign(JsonElement transaction, JsonObject assignment, string reason, string key) =>
        AssertSuccess(await AssignResult(transaction, assignment, reason, key), "ledger.transaction.pool.assign");

    private Task<PublishedTallyResult> AssignResult(JsonElement transaction, JsonObject assignment, string reason, string key) => Run(
        ["ledger", "transaction", "pool", "assign", "--input", "-"],
        Envelope(PoolInput(transaction, assignment, reason), key));

    private async Task<JsonElement> Correct(JsonElement transaction, JsonObject assignment, string reason, string key) =>
        AssertSuccess(await CorrectResult(transaction, assignment, reason, key), "ledger.transaction.pool.correct");

    private Task<PublishedTallyResult> CorrectResult(JsonElement transaction, JsonObject assignment, string reason, string key) => Run(
        ["ledger", "transaction", "pool", "correct", "--input", "-"],
        Envelope(PoolInput(transaction, assignment, reason), key));

    private async Task Void(string transactionId, string reason) => _ = await Success(
        ["ledger", "transaction", "void", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["reason"] = reason }, Key("void")),
        "ledger.transaction.void");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> Actuals(
        string[] accountIds,
        string? from = null,
        string? to = null,
        string[]? lifecycleStates = null) => await Success(
            ["ledger", "actuals", "query", "--input", "-"],
            Envelope(new JsonObject
            {
                ["filter"] = new JsonObject
                {
                    ["accountIds"] = Array(accountIds),
                    ["effectiveFrom"] = from,
                    ["effectiveTo"] = to,
                    ["lifecycleStates"] = lifecycleStates is null ? null : Array(lifecycleStates),
                    ["groupBy"] = "pool_category"
                },
                ["pageSize"] = 100,
                ["cursor"] = null
            }),
            "ledger.actuals.query");

    private async Task<bool> KillPublishedProcessDuringMutation(IReadOnlyList<string> arguments, string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
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
    private string Key(string purpose) => "uc018-" + purpose + "-" + Interlocked.Increment(ref sequence);
    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;
    private static string PoolId(JsonElement pool) => pool.GetProperty("poolId").GetString()!;
    private static JsonObject Assigned(string poolId) => new() { ["state"] = "assigned", ["poolId"] = poolId };
    private static JsonObject Unassigned() => new() { ["state"] = "unassigned", ["poolId"] = null };

    private static JsonObject PoolInput(JsonElement transaction, JsonObject assignment, string reason) => new()
    {
        ["transactionId"] = TransactionId(transaction),
        ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
        ["assignment"] = assignment.DeepClone(),
        ["reason"] = reason
    };

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertActualsEqual(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.GetProperty("totalCount").GetInt32(), actual.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.GetProperty("items").GetRawText(), actual.GetProperty("items").GetRawText());
        Assert.Equal(expected.GetProperty("totals").GetRawText(), actual.GetProperty("totals").GetRawText());
        Assert.Equal(expected.GetProperty("groups").GetRawText(), actual.GetProperty("groups").GetRawText());
    }

    private static void AssertTotals(JsonElement actuals, string net, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(net, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
    }

    private static void AssertConservation(JsonElement actuals)
    {
        var items = actuals.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
        var groups = actuals.GetProperty("groups").EnumerateArray().Select(group => group.Clone()).ToArray();
        var itemTotal = Sum(items.Select(item => item.GetProperty("contribution")));
        AssertMoney(actuals.GetProperty("totals"), itemTotal);

        var expectedCells = items
            .GroupBy(CellKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Sum(group.Select(item => item.GetProperty("contribution"))), StringComparer.Ordinal);
        Assert.Equal(expectedCells.Count, groups.Length);
        foreach (var group in groups)
        {
            Assert.True(expectedCells.Remove(CellKey(group), out var expected), CellKey(group));
            AssertMoney(group.GetProperty("totals"), expected);
        }
        Assert.Empty(expectedCells);
        Assert.Equal(itemTotal, Sum(groups.Select(group => group.GetProperty("totals"))));
    }

    private static string CellKey(JsonElement value) => string.Join('|', new[]
    {
        value.GetProperty("poolState").GetString(),
        value.GetProperty("poolId").GetString(),
        value.GetProperty("categoryState").GetString(),
        value.GetProperty("categoryId").GetString()
    });

    private static Money Sum(IEnumerable<JsonElement> totals)
    {
        var result = new Money(0, 0, 0);
        foreach (var total in totals)
            result = new(
                result.Net + Parse(total.GetProperty("netAccountMovement")),
                result.Spend + Parse(total.GetProperty("externalSpend")),
                result.Budget + Parse(total.GetProperty("budgetActual")));
        return result;
    }

    private static void AssertMoney(JsonElement actual, Money expected)
    {
        Assert.Equal(expected.Net, Parse(actual.GetProperty("netAccountMovement")));
        Assert.Equal(expected.Spend, Parse(actual.GetProperty("externalSpend")));
        Assert.Equal(expected.Budget, Parse(actual.GetProperty("budgetActual")));
    }

    private static decimal Parse(JsonElement value) => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    private static string Envelope(JsonNode input, string? key = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc018", ["runId"] = "published-e2e" },
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

    private readonly record struct Money(decimal Net, decimal Spend, decimal Budget);
}
