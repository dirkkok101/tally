using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC003CategoryAllocationWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc003-" + Guid.NewGuid().ToString("N"));
    private int backupSequence;

    [Fact]
    public async Task UC_LEDGER_003_agent_discovers_assign_and_correct_contracts()
    {
        foreach (var (operationId, requestType) in new[]
                 {
                     ("ledger.transaction.category.assign", "AssignCategoryInput"),
                     ("ledger.transaction.category.correct", "CorrectCategoryInput")
                 })
        {
            var schema = await Success(
                ["schema", "show", operationId, "--input", "-"],
                Envelope(new JsonObject()),
                "system.schema.show");
            var operation = schema.GetProperty("operation");

            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("CategoryAllocationResult", operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.True(operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UC_LEDGER_003_assigns_root_intermediate_or_leaf_once_without_changing_other_dimensions(int targetIndex)
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transaction = await RecordTransaction(accountId, 'a');
        var root = await CreateCategory("Living", null, "root");
        var intermediate = await CreateCategory("Food", root.GetProperty("categoryId").GetString(), "intermediate");
        var leaf = await CreateCategory("Groceries", intermediate.GetProperty("categoryId").GetString(), "leaf");
        var target = new[] { root, intermediate, leaf }[targetIndex];

        var allocation = await Assign(
            transaction.GetProperty("transactionId").GetString()!,
            target.GetProperty("categoryId").GetString()!,
            "Owner classification",
            "assign-category");
        var current = allocation.GetProperty("transaction");

        Assert.Equal("categorized", current.GetProperty("category").GetProperty("state").GetString());
        Assert.Equal(target.GetProperty("categoryId").GetString(), current.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(
            target.GetProperty("ancestryIds").EnumerateArray().Select(item => item.GetString()),
            current.GetProperty("category").GetProperty("currentAncestryIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(allocation.GetProperty("allocationEventId").GetString(), current.GetProperty("category").GetProperty("allocationEventId").GetString());
        AssertNonCategoryDimensionsUnchanged(transaction, current);
        Assert.Single(current.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_correction_appends_history_and_changes_only_the_category()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transaction = await RecordTransaction(accountId, 'b');
        var root = await CreateCategory("Travel", null, "root");
        var leaf = await CreateCategory("Work travel", root.GetProperty("categoryId").GetString(), "leaf");
        var assigned = await Assign(
            transaction.GetProperty("transactionId").GetString()!,
            root.GetProperty("categoryId").GetString()!,
            "Initial choice",
            "assign-root");

        var corrected = await Correct(
            transaction.GetProperty("transactionId").GetString()!,
            leaf.GetProperty("categoryId").GetString()!,
            "Owner corrected classification",
            "correct-leaf");
        var current = corrected.GetProperty("transaction");

        Assert.NotEqual(assigned.GetProperty("allocationEventId").GetString(), corrected.GetProperty("allocationEventId").GetString());
        Assert.Equal(leaf.GetProperty("categoryId").GetString(), current.GetProperty("category").GetProperty("categoryId").GetString());
        AssertNonCategoryDimensionsUnchanged(transaction, current);
        var history = current.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.Equal("assign", history[0].GetProperty("action").GetString());
        Assert.Equal("Initial choice", history[0].GetProperty("reason").GetString());
        Assert.Equal("correct", history[1].GetProperty("action").GetString());
        Assert.Equal(assigned.GetProperty("allocationEventId").GetString(), history[1].GetProperty("previousEventId").GetString());
        Assert.Equal("Owner corrected classification", history[1].GetProperty("reason").GetString());
        Assert.Equal(2, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_archived_target_is_rejected_without_consuming_the_request_identity()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'c')).GetProperty("transactionId").GetString()!;
        var archived = await CreateCategory("Archived", null, "archived");
        var archivedId = archived.GetProperty("categoryId").GetString()!;
        await Success(
            ["ledger", "category", "archive", "--input", "-"],
            Envelope(new JsonObject { ["categoryId"] = archivedId, ["reason"] = "No longer used" }, "archive-category"),
            "ledger.category.archive");

        var rejected = await Run(
            ["ledger", "transaction", "category", "assign", "--input", "-"],
            AllocationEnvelope(transactionId, archivedId, "Late classification", "category-request"));
        AssertError(rejected, 6, "LEDGER-CATEGORY-ARCHIVED");

        var replacement = await CreateCategory("Replacement", null, "replacement");
        var accepted = await Assign(transactionId, replacement.GetProperty("categoryId").GetString()!, "Corrected target", "category-request");
        Assert.Equal(replacement.GetProperty("categoryId").GetString(), accepted.GetProperty("transaction").GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_inactive_transaction_rejects_assignment_without_mutation()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'd')).GetProperty("transactionId").GetString()!;
        var categoryId = (await CreateCategory("Late", null, "category")).GetProperty("categoryId").GetString()!;
        await Success(
            ["ledger", "transaction", "void", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = transactionId, ["reason"] = "Owner voided transaction" }, "void-transaction"),
            "ledger.transaction.void");

        var rejected = await Run(
            ["ledger", "transaction", "category", "assign", "--input", "-"],
            AllocationEnvelope(transactionId, categoryId, "Late classification", "late-assignment"));

        AssertError(rejected, 6, "LEDGER-TRANSACTION-INACTIVE");
        Assert.Equal(0, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_simultaneous_split_requests_commit_exactly_one_assignment()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'e')).GetProperty("transactionId").GetString()!;
        var firstId = (await CreateCategory("First", null, "first")).GetProperty("categoryId").GetString()!;
        var secondId = (await CreateCategory("Second", null, "second")).GetProperty("categoryId").GetString()!;

        var results = await Task.WhenAll(
            Run(["ledger", "transaction", "category", "assign", "--input", "-"], AllocationEnvelope(transactionId, firstId, "First concurrent choice", "split-first")),
            Run(["ledger", "transaction", "category", "assign", "--input", "-"], AllocationEnvelope(transactionId, secondId, "Second concurrent choice", "split-second")));

        var success = Assert.Single(results, result => result.ExitCode == 0);
        AssertSuccess(success, "ledger.transaction.category.assign");
        AssertError(Assert.Single(results, result => result.ExitCode != 0), 5, "LEDGER-CATEGORY-ALLOCATION-CARDINALITY");
        var current = await GetTransaction(transactionId);
        Assert.Contains(current.GetProperty("category").GetProperty("categoryId").GetString(), new[] { firstId, secondId });
        Assert.Single(current.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_missing_or_unchanged_correction_projection_is_rejected_without_history()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'f')).GetProperty("transactionId").GetString()!;
        var categoryId = (await CreateCategory("Current", null, "category")).GetProperty("categoryId").GetString()!;

        var missing = await Run(
            ["ledger", "transaction", "category", "correct", "--input", "-"],
            AllocationEnvelope(transactionId, categoryId, "No current projection", "missing-correction"));
        AssertError(missing, 6, "LEDGER-CATEGORY-ALLOCATION-NOT-ASSIGNED");

        await Assign(transactionId, categoryId, "Initial classification", "assign-current");
        var unchanged = await Run(
            ["ledger", "transaction", "category", "correct", "--input", "-"],
            AllocationEnvelope(transactionId, categoryId, "Stale current projection", "stale-correction"));

        AssertError(unchanged, 5, "LEDGER-CATEGORY-ALLOCATION-UNCHANGED");
        Assert.Single((await GetTransaction(transactionId)).GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_replay_is_stable_and_conflicting_reuse_preserves_the_original()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'g')).GetProperty("transactionId").GetString()!;
        var firstId = (await CreateCategory("Original", null, "first")).GetProperty("categoryId").GetString()!;
        var secondId = (await CreateCategory("Changed", null, "second")).GetProperty("categoryId").GetString()!;
        var request = AllocationEnvelope(transactionId, firstId, "Owner choice", "same-key");

        var first = await Run(["ledger", "transaction", "category", "assign", "--input", "-"], request);
        var replay = await Run(["ledger", "transaction", "category", "assign", "--input", "-"], request);
        var conflict = await Run(
            ["ledger", "transaction", "category", "assign", "--input", "-"],
            AllocationEnvelope(transactionId, secondId, "Changed choice", "same-key"));

        AssertSuccess(first, "ledger.transaction.category.assign");
        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
        var current = await GetTransaction(transactionId);
        Assert.Equal(firstId, current.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Single(current.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
    }

    [Fact]
    public async Task UC_LEDGER_003_rename_and_reparent_preserve_assignment_while_current_rollups_change_exactly_once()
    {
        var accountId = (await CreateAccount()).GetProperty("accountId").GetString()!;
        var transactionId = (await RecordTransaction(accountId, 'h')).GetProperty("transactionId").GetString()!;
        var oldParentId = (await CreateCategory("Old parent", null, "old-parent")).GetProperty("categoryId").GetString()!;
        var newParentId = (await CreateCategory("New parent", null, "new-parent")).GetProperty("categoryId").GetString()!;
        var childId = (await CreateCategory("Child", oldParentId, "child")).GetProperty("categoryId").GetString()!;
        var siblingId = (await CreateCategory("Sibling", oldParentId, "sibling")).GetProperty("categoryId").GetString()!;
        var assigned = await Assign(transactionId, childId, "Owner classification", "assign-child");
        var allocationEventId = assigned.GetProperty("allocationEventId").GetString();

        var before = await QueryActuals(null, "exact", "category_subtree");
        AssertCategoryGroups(before, oldParentId, childId);

        await Success(
            ["ledger", "category", "rename", "--input", "-"],
            Envelope(new JsonObject { ["categoryId"] = childId, ["newName"] = "Renamed child", ["reason"] = "Owner clarified label" }, "rename-child"),
            "ledger.category.rename");
        await Success(
            ["ledger", "category", "reparent", "--input", "-"],
            Envelope(new JsonObject { ["categoryId"] = childId, ["parentCategoryId"] = newParentId, ["reason"] = "Owner moved hierarchy" }, "reparent-child"),
            "ledger.category.reparent");

        var current = await GetTransaction(transactionId);
        Assert.Equal(allocationEventId, current.GetProperty("category").GetProperty("allocationEventId").GetString());
        Assert.Equal(new[] { newParentId, childId }, current.GetProperty("category").GetProperty("currentAncestryIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Single(current.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());

        Assert.Empty((await QueryActuals(oldParentId, "subtree", "none")).GetProperty("items").EnumerateArray());
        Assert.Empty((await QueryActuals(siblingId, "subtree", "none")).GetProperty("items").EnumerateArray());
        AssertSingleActual((await QueryActuals(childId, "exact", "category_direct")), transactionId, childId);
        AssertSingleActual((await QueryActuals(newParentId, "subtree", "category_subtree")), transactionId, childId);
        var after = await QueryActuals(null, "exact", "category_subtree");
        AssertCategoryGroups(after, newParentId, childId);
        Assert.Equal(1, (await DurableCounts())["category_allocation_event"]);
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

    private async Task<JsonElement> CreateAccount() => await Success(
        ["ledger", "account", "create", "--input", "-"],
        Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = "Daily",
            ["accountType"] = "cheque",
            ["maskedIdentifier"] = "****1234",
            ["currencyCode"] = "ZAR"
        }, "account-create"),
        "ledger.account.create");

    private async Task<JsonElement> CreateCategory(string name, string? parentId, string key) => await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name, ["parentCategoryId"] = parentId }, "category-" + key),
        "ledger.category.create");

    private async Task<JsonElement> RecordTransaction(string accountId, char digestCharacter) => await Success(
        ["ledger", "transaction", "record", "--input", "-"],
        Envelope(new JsonObject
        {
            ["accountId"] = accountId,
            ["signedAmount"] = "-12.34",
            ["currencyCode"] = "ZAR",
            ["transactionDate"] = "2026-07-01",
            ["postingDate"] = null,
            ["originalDescription"] = "Owner-safe purchase",
            ["instrumentId"] = null,
            ["cardholderId"] = null,
            ["initialEvidence"] = new JsonObject
            {
                ["kind"] = "agent_capture",
                ["logicalIdentityDigest"] = Digest(digestCharacter),
                ["opaqueExternalReference"] = "capture:" + digestCharacter,
                ["contentFingerprint"] = null,
                ["observation"] = null
            }
        }, "record-" + digestCharacter),
        "ledger.transaction.record");

    private async Task<JsonElement> Assign(string transactionId, string categoryId, string reason, string key) => await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        AllocationEnvelope(transactionId, categoryId, reason, key),
        "ledger.transaction.category.assign");

    private async Task<JsonElement> Correct(string transactionId, string categoryId, string reason, string key) => await Success(
        ["ledger", "transaction", "category", "correct", "--input", "-"],
        AllocationEnvelope(transactionId, categoryId, reason, key),
        "ledger.transaction.category.correct");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> QueryActuals(string? categoryId, string categoryScope, string groupBy)
    {
        var filter = new JsonObject { ["categoryScope"] = categoryScope, ["groupBy"] = groupBy };
        if (categoryId is not null) filter["categoryIds"] = new JsonArray(JsonValue.Create(categoryId));
        return await Success(
            ["ledger", "actuals", "query", "--input", "-"],
            Envelope(new JsonObject { ["filter"] = filter, ["pageSize"] = 100, ["cursor"] = null }),
            "ledger.actuals.query");
    }

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

    private static void AssertNonCategoryDimensionsUnchanged(JsonElement before, JsonElement after)
    {
        foreach (var property in new[]
                 {
                     "transactionId", "accountId", "signedAmount", "currencyCode", "transactionDate", "postingDate",
                     "effectiveDate", "originalDescription", "lifecycleStatus", "reconciliationState"
                 })
        {
            Assert.Equal(before.GetProperty(property).GetRawText(), after.GetProperty(property).GetRawText());
        }

        Assert.Equal(before.GetProperty("pool").GetRawText(), after.GetProperty("pool").GetRawText());
        Assert.Equal(before.GetProperty("paymentAttribution").GetRawText(), after.GetProperty("paymentAttribution").GetRawText());
        Assert.Equal(before.GetProperty("evidence").GetRawText(), after.GetProperty("evidence").GetRawText());
    }

    private static void AssertSingleActual(JsonElement actuals, string transactionId, string categoryId)
    {
        Assert.Equal(1, actuals.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal(transactionId, item.GetProperty("transactionId").GetString());
        Assert.Equal(categoryId, item.GetProperty("categoryId").GetString());
        Assert.Equal("12.34", actuals.GetProperty("totals").GetProperty("budgetActual").GetString());
    }

    private static void AssertCategoryGroups(JsonElement actuals, params string[] expectedCategoryIds)
    {
        Assert.Equal(1, actuals.GetProperty("totalCount").GetInt32());
        Assert.Equal("12.34", actuals.GetProperty("totals").GetProperty("budgetActual").GetString());
        var groups = actuals.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(expectedCategoryIds.Order(StringComparer.Ordinal), groups.Select(group => group.GetProperty("categoryId").GetString()).Order(StringComparer.Ordinal));
        Assert.All(groups, group => Assert.Equal("12.34", group.GetProperty("totals").GetProperty("budgetActual").GetString()));
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

    private static string AllocationEnvelope(string transactionId, string categoryId, string reason, string key) =>
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = reason }, key);

    private static string Digest(char value) => string.Concat(Enumerable.Repeat(
        ((byte)value).ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
        32));

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject
            {
                ["kind"] = "automation",
                ["label"] = "uc003",
                ["runId"] = "published-e2e"
            },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }
}
