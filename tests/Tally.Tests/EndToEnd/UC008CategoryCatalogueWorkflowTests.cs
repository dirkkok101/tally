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
public sealed class UC008CategoryCatalogueWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc008-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_008_discovers_every_typed_category_catalogue_operation()
    {
        foreach (var (operationId, requestType, resultType, mutation) in new[]
                 {
                     ("ledger.category.create", "CreateCategoryInput", "CategoryDetail", true),
                     ("ledger.category.get", "GetCategoryInput", "CategoryDetail", false),
                     ("ledger.category.list", "ListCategoriesInput", "CategoryListResult", false),
                     ("ledger.category.rename", "RenameCategoryInput", "CategoryLifecycleResult", true),
                     ("ledger.category.reparent", "ReparentCategoryInput", "CategoryReparentResult", true),
                     ("ledger.category.archive", "ArchiveCategoryInput", "CategoryLifecycleResult", true),
                     ("ledger.category.reactivate", "ReactivateCategoryInput", "CategoryLifecycleResult", true)
                 })
        {
            var schema = (await Success(
                ["schema", "show", operationId],
                null,
                "system.schema.show")).GetProperty("operation");

            Assert.EndsWith(requestType, schema.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(resultType, schema.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.Equal(mutation, schema.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_rename_and_reparent_preserve_identity_history_and_current_rollups()
    {
        var oldParent = await CreateCategory("Old parent", null, Key("old-parent"));
        var newParent = await CreateCategory("New parent", null, Key("new-parent"));
        var child = await CreateCategory("Child", oldParent.GetProperty("categoryId").GetString(), Key("child"));
        var transaction = await RecordTransaction();
        var assigned = await AssignCategory(
            transaction.GetProperty("transactionId").GetString()!,
            child.GetProperty("categoryId").GetString()!,
            Key("assign"));
        var categoryId = child.GetProperty("categoryId").GetString()!;
        var allocationEventId = assigned.GetProperty("allocationEventId").GetString();

        var renamed = await Success(
            ["ledger", "category", "rename", "--input", "-"],
            Envelope(new JsonObject
            {
                ["categoryId"] = categoryId,
                ["newName"] = "Renamed child",
                ["reason"] = "Owner clarified category"
            }, Key("rename")),
            "ledger.category.rename");
        var moved = await Success(
            ["ledger", "category", "reparent", "--input", "-"],
            Envelope(new JsonObject
            {
                ["categoryId"] = categoryId,
                ["parentCategoryId"] = newParent.GetProperty("categoryId").GetString(),
                ["reason"] = "Owner changed rollup"
            }, Key("reparent")),
            "ledger.category.reparent");

        Assert.Equal(categoryId, renamed.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(categoryId, moved.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal("Renamed child", moved.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal(1, moved.GetProperty("category").GetProperty("depth").GetInt32());
        Assert.Equal(
            new[] { newParent.GetProperty("categoryId").GetString(), categoryId },
            moved.GetProperty("category").GetProperty("ancestryIds").EnumerateArray().Select(item => item.GetString()));

        var fetched = await GetCategory(categoryId, includeHistory: true);
        Assert.Equal(2, fetched.GetProperty("lifecycleHistory").GetArrayLength());
        Assert.Equal(2, fetched.GetProperty("parentHistory").GetArrayLength());
        Assert.Equal("initialize", fetched.GetProperty("parentHistory")[0].GetProperty("action").GetString());
        Assert.Equal("reparent", fetched.GetProperty("parentHistory")[1].GetProperty("action").GetString());
        var currentTransaction = await GetTransaction(transaction.GetProperty("transactionId").GetString()!);
        Assert.Equal(allocationEventId, currentTransaction.GetProperty("category").GetProperty("allocationEventId").GetString());
        Assert.Equal(categoryId, currentTransaction.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(
            new[] { newParent.GetProperty("categoryId").GetString(), categoryId },
            currentTransaction.GetProperty("category").GetProperty("currentAncestryIds").EnumerateArray().Select(item => item.GetString()));

        Assert.Equal(0, (await QuerySubtree(oldParent.GetProperty("categoryId").GetString()!)).GetProperty("totalCount").GetInt32());
        var newRollup = await QuerySubtree(newParent.GetProperty("categoryId").GetString()!);
        Assert.Equal(1, newRollup.GetProperty("totalCount").GetInt32());
        Assert.Equal("12.34", newRollup.GetProperty("totals").GetProperty("budgetActual").GetString());
        Assert.Equal("unassigned", currentTransaction.GetProperty("pool").GetProperty("state").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_list_returns_deterministic_explicit_ancestry()
    {
        var root = await CreateCategory("List root", null, Key("root"));
        var rootId = root.GetProperty("categoryId").GetString()!;
        var alpha = await CreateCategory("Alpha", rootId, Key("alpha"));
        var beta = await CreateCategory("Beta", rootId, Key("beta"));
        var leaf = await CreateCategory("Leaf", alpha.GetProperty("categoryId").GetString(), Key("leaf"));

        var children = await ListCategories(rootId, "children");
        var subtree = await ListCategories(rootId, "subtree");

        Assert.Equal(
            new[] { alpha.GetProperty("categoryId").GetString(), beta.GetProperty("categoryId").GetString() },
            children.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("categoryId").GetString()));
        Assert.Equal(4, subtree.GetProperty("items").GetArrayLength());
        var leafSummary = Assert.Single(subtree.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("categoryId").GetString() == leaf.GetProperty("categoryId").GetString());
        Assert.Equal(2, leafSummary.GetProperty("depth").GetInt32());
        Assert.Equal(
            new[] { rootId, alpha.GetProperty("categoryId").GetString(), leaf.GetProperty("categoryId").GetString() },
            leafSummary.GetProperty("ancestryIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_sibling_names_are_unique_but_other_parents_are_independent()
    {
        var firstParent = await CreateCategory("First scope", null, Key("first"));
        var secondParent = await CreateCategory("Second scope", null, Key("second"));
        var firstParentId = firstParent.GetProperty("categoryId").GetString()!;
        await CreateCategory("Shared name", firstParentId, Key("original"));

        AssertError(
            await Run(
                ["ledger", "category", "create", "--input", "-"],
                Envelope(new JsonObject { ["name"] = " shared NAME ", ["parentCategoryId"] = firstParentId }, Key("duplicate"))),
            5,
            "LEDGER-CATEGORY-DUPLICATE-SIBLING");
        var independent = await CreateCategory(
            "Shared name",
            secondParent.GetProperty("categoryId").GetString(),
            Key("independent"));
        Assert.Equal("Shared name", independent.GetProperty("name").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_self_parent_and_descendant_cycle_preserve_hierarchy()
    {
        var root = await CreateCategory("Cycle root", null, Key("root"));
        var rootId = root.GetProperty("categoryId").GetString()!;
        var child = await CreateCategory("Cycle child", rootId, Key("child"));
        var childId = child.GetProperty("categoryId").GetString()!;

        AssertError(await Reparent(rootId, rootId, "Self parent", Key("self")), 3, "LEDGER-CATEGORY-SELF-PARENT");
        AssertError(await Reparent(rootId, childId, "Create cycle", Key("cycle")), 6, "LEDGER-CATEGORY-CYCLE");
        var unchangedRoot = await GetCategory(rootId, includeHistory: true);
        var unchangedChild = await GetCategory(childId, includeHistory: true);
        Assert.Equal(JsonValueKind.Null, unchangedRoot.GetProperty("parentCategoryId").ValueKind);
        Assert.Equal(rootId, unchangedChild.GetProperty("parentCategoryId").GetString());
        Assert.Single(unchangedRoot.GetProperty("parentHistory").EnumerateArray());
        Assert.Single(unchangedChild.GetProperty("parentHistory").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_archived_parent_rejects_new_child_move_and_assignment()
    {
        var archived = await CreateCategory("Archived parent", null, Key("archived"));
        var archivedId = archived.GetProperty("categoryId").GetString()!;
        await Archive(archivedId, Key("archive"));
        var movable = await CreateCategory("Movable", null, Key("movable"));

        AssertError(
            await Run(
                ["ledger", "category", "create", "--input", "-"],
                Envelope(new JsonObject { ["name"] = "Blocked child", ["parentCategoryId"] = archivedId }, Key("blocked-child"))),
            6,
            "LEDGER-CATEGORY-PARENT-ARCHIVED");
        AssertError(
            await Reparent(movable.GetProperty("categoryId").GetString()!, archivedId, "Archived target", Key("blocked-move")),
            6,
            "LEDGER-CATEGORY-PARENT-ARCHIVED");
        var transaction = await RecordTransaction();
        AssertError(
            await Run(
                ["ledger", "transaction", "category", "assign", "--input", "-"],
                CategoryAssignmentEnvelope(transaction.GetProperty("transactionId").GetString()!, archivedId, Key("blocked-assignment"))),
            6,
            "LEDGER-CATEGORY-ARCHIVED");
        Assert.Equal("uncategorized", (await GetTransaction(transaction.GetProperty("transactionId").GetString()!)).GetProperty("category").GetProperty("state").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_active_child_blocks_archive_and_reactivation_preserves_identity()
    {
        var parent = await CreateCategory("Lifecycle parent", null, Key("parent"));
        var parentId = parent.GetProperty("categoryId").GetString()!;
        var child = await CreateCategory("Lifecycle child", parentId, Key("child"));
        var childId = child.GetProperty("categoryId").GetString()!;

        AssertError(
            await Run(
                ["ledger", "category", "archive", "--input", "-"],
                Envelope(new JsonObject { ["categoryId"] = parentId, ["reason"] = "Blocked while child active" }, Key("blocked-parent"))),
            6,
            "LEDGER-CATEGORY-ACTIVE-CHILDREN");
        await Archive(childId, Key("archive-child"));
        await Archive(parentId, Key("archive-parent"));
        AssertError(
            await Run(
                ["ledger", "category", "reactivate", "--input", "-"],
                Envelope(new JsonObject { ["categoryId"] = childId, ["reason"] = "Ancestor still archived" }, Key("blocked-reactivate"))),
            6,
            "LEDGER-CATEGORY-ANCESTOR-ARCHIVED");
        await Reactivate(parentId, Key("reactivate-parent"));
        var reactivated = await Reactivate(childId, Key("reactivate-child"));

        Assert.Equal(childId, reactivated.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal("active", reactivated.GetProperty("category").GetProperty("status").GetString());
        Assert.Equal(3, (await GetCategory(childId, includeHistory: true)).GetProperty("lifecycleHistory").GetArrayLength());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_physical_delete_is_not_public_and_preserves_history()
    {
        var category = await CreateCategory("Never delete", null, Key("category"));
        var categoryId = category.GetProperty("categoryId").GetString()!;
        await Archive(categoryId, Key("archive"));

        AssertError(
            await Run(
                ["ledger", "category", "delete", "--input", "-"],
                Envelope(new JsonObject { ["categoryId"] = categoryId }, Key("delete"))),
            2,
            "operation.unknown");
        var retained = await GetCategory(categoryId, includeHistory: true);
        Assert.Equal("archived", retained.GetProperty("status").GetString());
        Assert.Equal(2, retained.GetProperty("lifecycleHistory").GetArrayLength());
        Assert.Single(retained.GetProperty("parentHistory").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_replay_is_stable_and_stale_changed_move_is_rejected()
    {
        var firstParent = await CreateCategory("Replay first", null, Key("first"));
        var secondParent = await CreateCategory("Replay second", null, Key("second"));
        var child = await CreateCategory("Replay child", firstParent.GetProperty("categoryId").GetString(), Key("child"));
        var key = Key("move");
        var request = ReparentEnvelope(
            child.GetProperty("categoryId").GetString()!,
            secondParent.GetProperty("categoryId").GetString(),
            "Owner move",
            key);

        var first = await Run(["ledger", "category", "reparent", "--input", "-"], request);
        var replay = await Run(["ledger", "category", "reparent", "--input", "-"], request);
        var changed = await Reparent(
            child.GetProperty("categoryId").GetString()!,
            firstParent.GetProperty("categoryId").GetString(),
            "Stale changed move",
            key);

        AssertSuccess(first, "ledger.category.reparent");
        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        var current = await GetCategory(child.GetProperty("categoryId").GetString()!, includeHistory: true);
        Assert.Equal(secondParent.GetProperty("categoryId").GetString(), current.GetProperty("parentCategoryId").GetString());
        Assert.Equal(2, current.GetProperty("parentHistory").GetArrayLength());
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_CATALOGUE_catalogue_changes_create_no_pool_budget_or_automatic_classification()
    {
        await CreateCategory("Catalogue only", null, Key("category"));
        var pools = await Success(
            ["ledger", "pool", "list", "--input", "-"],
            Envelope(new JsonObject()),
            "ledger.pool.list");
        var schemas = await Success(["schema", "list"], null, "system.schema.list");
        var counts = await DurableCounts();

        Assert.Empty(pools.GetProperty("items").EnumerateArray());
        Assert.All(
            schemas.GetProperty("operations").EnumerateArray().Where(operation =>
                operation.GetProperty("operationId").GetString()!.StartsWith("ledger.category.", StringComparison.Ordinal)),
            operation =>
            {
                var requestSchema = operation.GetProperty("requestSchema").GetString()!;
                Assert.DoesNotContain("budgetAmount", requestSchema, StringComparison.Ordinal);
                Assert.DoesNotContain("poolId", requestSchema, StringComparison.Ordinal);
                Assert.DoesNotContain("classificationRule", requestSchema, StringComparison.Ordinal);
            });
        Assert.Equal(0, counts["transaction_fact"]);
        Assert.Equal(0, counts["category_allocation_event"]);
        Assert.Equal(0, counts["spend_pool"]);
        Assert.Equal(0, counts["pool_assignment_event"]);
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

    private async Task<JsonElement> CreateCategory(string name, string? parentCategoryId, string key) => await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name, ["parentCategoryId"] = parentCategoryId }, key),
        "ledger.category.create");

    private async Task<JsonElement> GetCategory(string categoryId, bool includeHistory) => await Success(
        ["ledger", "category", "get", "--input", "-"],
        Envelope(new JsonObject { ["categoryId"] = categoryId, ["includeHistory"] = includeHistory }),
        "ledger.category.get");

    private async Task<JsonElement> ListCategories(string parentCategoryId, string scope) => await Success(
        ["ledger", "category", "list", "--input", "-"],
        Envelope(new JsonObject { ["status"] = "active", ["parentCategoryId"] = parentCategoryId, ["scope"] = scope }),
        "ledger.category.list");

    private Task<PublishedTallyResult> Reparent(string categoryId, string? parentCategoryId, string reason, string key) => Run(
        ["ledger", "category", "reparent", "--input", "-"],
        ReparentEnvelope(categoryId, parentCategoryId, reason, key));

    private static string ReparentEnvelope(string categoryId, string? parentCategoryId, string reason, string key) => Envelope(
        new JsonObject { ["categoryId"] = categoryId, ["parentCategoryId"] = parentCategoryId, ["reason"] = reason },
        key);

    private async Task<JsonElement> Archive(string categoryId, string key) => await Success(
        ["ledger", "category", "archive", "--input", "-"],
        Envelope(new JsonObject { ["categoryId"] = categoryId, ["reason"] = "Owner archived category" }, key),
        "ledger.category.archive");

    private async Task<JsonElement> Reactivate(string categoryId, string key) => await Success(
        ["ledger", "category", "reactivate", "--input", "-"],
        Envelope(new JsonObject { ["categoryId"] = categoryId, ["reason"] = "Owner reactivated category" }, key),
        "ledger.category.reactivate");

    private async Task<JsonElement> RecordTransaction()
    {
        var token = Key("transaction");
        var account = await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "Account " + token,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + sequence.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..],
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create");
        return await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = account.GetProperty("accountId").GetString(),
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
                    ["logicalIdentityDigest"] = Digest(token),
                    ["opaqueExternalReference"] = "capture:" + token,
                    ["contentFingerprint"] = null,
                    ["observation"] = null
                }
            }, Key("record")),
            "ledger.transaction.record");
    }

    private async Task<JsonElement> AssignCategory(string transactionId, string categoryId, string key) => await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        CategoryAssignmentEnvelope(transactionId, categoryId, key),
        "ledger.transaction.category.assign");

    private static string CategoryAssignmentEnvelope(string transactionId, string categoryId, string key) => Envelope(
        new JsonObject
        {
            ["transactionId"] = transactionId,
            ["categoryId"] = categoryId,
            ["reason"] = "Owner classification"
        },
        key);

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> QuerySubtree(string categoryId) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject
            {
                ["categoryIds"] = new JsonArray(JsonValue.Create(categoryId)),
                ["categoryScope"] = "subtree",
                ["groupBy"] = "category_subtree"
            },
            ["pageSize"] = 100,
            ["cursor"] = null
        }),
        "ledger.actuals.query");

    private async Task<IReadOnlyDictionary<string, long>> DurableCounts()
    {
        var backupRoot = Path.Combine(dataRoot, "verification-artifacts");
        Directory.CreateDirectory(backupRoot);
        File.SetUnixFileMode(backupRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(backupRoot, "catalogue.tally-backup");
        var receipt = await Success(
            ["ledger", "backup", "create", "--input", "-"],
            Envelope(new JsonObject { ["targetPath"] = target }, Key("backup")),
            "ledger.backup.create");
        return receipt.GetProperty("manifest").GetProperty("types").EnumerateArray()
            .ToDictionary(
                type => type.GetProperty("name").GetString()!,
                type => type.GetProperty("rowCount").GetInt64(),
                StringComparer.Ordinal);
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) =>
        fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string? input, string operationId) =>
        AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc008-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject
            {
                ["kind"] = "automation",
                ["label"] = "uc008",
                ["runId"] = "published-e2e"
            },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
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
}
