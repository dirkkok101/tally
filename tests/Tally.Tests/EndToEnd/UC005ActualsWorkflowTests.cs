using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC005ActualsWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc005-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_005_agent_discovers_the_closed_filter_grouping_and_snapshot_contract()
    {
        var operation = (await Success(
            ["schema", "show", "ledger.actuals.query", "--input", "-"],
            Envelope(new JsonObject()),
            "system.schema.show")).GetProperty("operation");
        using var request = JsonDocument.Parse(operation.GetProperty("requestSchema").GetString()!);
        using var result = JsonDocument.Parse(operation.GetProperty("resultSchema").GetString()!);
        var filter = request.RootElement.GetProperty("properties").GetProperty("filter").GetProperty("properties");

        foreach (var property in new[]
                 {
                     "accountIds", "effectiveFrom", "effectiveTo", "categoryIds", "categoryScope",
                     "categorizationStates", "poolIds", "poolStates", "instrumentIds", "instrumentStates",
                     "cardholderIds", "cardholderStates", "evidenceKinds", "reconciliationStates",
                     "relationshipStates", "lifecycleStates", "groupBy"
                 })
            Assert.True(filter.TryGetProperty(property, out _), property);
        foreach (var property in new[] { "snapshotId", "totalCount", "items", "totals", "groups", "cursor" })
            Assert.True(result.RootElement.GetProperty("properties").TryGetProperty(property, out _), property);
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_no_matches_returns_empty_membership_and_exact_zero_totals()
    {
        var accountId = await CreateAccount("Empty");

        var actuals = await Query(new JsonObject
        {
            ["accountIds"] = Array(accountId),
            ["effectiveFrom"] = "2026-01-01",
            ["effectiveTo"] = "2026-01-31",
            ["groupBy"] = "pool_category"
        });

        Assert.Equal(0, actuals.GetProperty("totalCount").GetInt32());
        Assert.Empty(actuals.GetProperty("items").EnumerateArray());
        Assert.Empty(actuals.GetProperty("groups").EnumerateArray());
        AssertTotals(actuals, "0", "0", "0");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_all_named_filters_are_conjunctive_and_preserve_exact_membership()
    {
        var accountId = await CreateAccount("Filtered");
        var otherAccountId = await CreateAccount("Other");
        var categoryId = await CreateCategory("Filtered category");
        var poolId = await CreatePool("Filtered pool");
        var instrumentId = await CreateInstrument(accountId, "Filtered instrument");
        var cardholderId = await CreateCardholder("Filtered owner");
        var target = await Record(accountId, "-12.34", "2026-04-10", "Target", instrumentId, cardholderId);
        var other = await Record(otherAccountId, "-99", "2026-04-10", "Other");
        await AssignCategory(target, categoryId);
        await AssignPool(await GetTransaction(target), poolId);

        var actuals = await Query(new JsonObject
        {
            ["accountIds"] = Array(accountId),
            ["effectiveFrom"] = "2026-04-01",
            ["effectiveTo"] = "2026-04-30",
            ["categoryIds"] = Array(categoryId),
            ["categoryScope"] = "exact",
            ["categorizationStates"] = Array("categorized"),
            ["poolIds"] = Array(poolId),
            ["poolStates"] = Array("assigned"),
            ["instrumentIds"] = Array(instrumentId),
            ["instrumentStates"] = Array("known"),
            ["cardholderIds"] = Array(cardholderId),
            ["cardholderStates"] = Array("known"),
            ["evidenceKinds"] = Array("agent_capture"),
            ["reconciliationStates"] = Array("recorded_unreconciled"),
            ["relationshipStates"] = Array("none"),
            ["lifecycleStates"] = Array("active"),
            ["groupBy"] = "pool_category"
        });

        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal(target, item.GetProperty("transactionId").GetString());
        Assert.DoesNotContain(actuals.GetProperty("items").EnumerateArray(), value => value.GetProperty("transactionId").GetString() == other);
        AssertTotals(actuals, "-12.34", "12.34", "12.34");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_repeated_filters_preserve_order_membership_states_and_exact_totals()
    {
        var accountId = await CreateAccount("Repeated");
        await Record(accountId, "-10.01", "2026-05-01", "First");
        await Record(accountId, "25.50", "2026-05-02", "Second");
        await Record(accountId, "-2.49", "2026-05-03", "Third");
        var filter = new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "category_direct" };

        var first = await Query(filter);
        var second = await Query(filter);

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            first.GetProperty("items").EnumerateArray().Select(ItemDigest),
            second.GetProperty("items").EnumerateArray().Select(ItemDigest));
        Assert.Equal(first.GetProperty("totals").GetRawText(), second.GetProperty("totals").GetRawText());
        Assert.Equal(first.GetProperty("groups").GetRawText(), second.GetProperty("groups").GetRawText());
        AssertTotals(first, "13", "12.50", "12.50");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_default_lifecycle_excludes_voided_facts()
    {
        var accountId = await CreateAccount("Lifecycle");
        var active = await Record(accountId, "-8", "2026-05-10", "Active");
        var voided = await Record(accountId, "-9", "2026-05-11", "Voided");
        await Success(
            ["ledger", "transaction", "void", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = voided, ["reason"] = "Owner voided" }, Key("void")),
            "ledger.transaction.void");

        var current = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "none" });
        var history = await Query(new JsonObject
        {
            ["accountIds"] = Array(accountId),
            ["lifecycleStates"] = Array("voided"),
            ["groupBy"] = "none"
        });

        Assert.Equal(active, Assert.Single(current.GetProperty("items").EnumerateArray()).GetProperty("transactionId").GetString());
        Assert.Equal(voided, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("transactionId").GetString());
        AssertTotals(current, "-8", "8", "8");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_owned_transfer_principal_is_zero_spend_while_separate_fee_remains_spend()
    {
        var fromAccount = await CreateAccount("Transfer from");
        var toAccount = await CreateAccount("Transfer to");
        var outflow = await Record(fromAccount, "-100", "2026-06-01", "Transfer out");
        var inflow = await Record(toAccount, "100", "2026-06-01", "Transfer in");
        await Record(fromAccount, "-2", "2026-06-01", "Transfer fee");
        await Success(
            ["ledger", "transfer", "confirm", "--input", "-"],
            Envelope(new JsonObject { ["outflowTransactionId"] = outflow, ["inflowTransactionId"] = inflow, ["reason"] = "Owned transfer" }, Key("transfer")),
            "ledger.transfer.confirm");

        var actuals = await Query(new JsonObject
        {
            ["accountIds"] = new JsonArray(JsonValue.Create(fromAccount), JsonValue.Create(toAccount)),
            ["groupBy"] = "none"
        });

        Assert.Equal(3, actuals.GetProperty("totalCount").GetInt32());
        AssertTotals(actuals, "-2", "2", "2");
        Assert.Equal(2, actuals.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("relationshipState").GetString()!.StartsWith("transfer_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OQ_LEDGER_16_cash_withdrawal_and_separate_fee_are_immediate_spend()
    {
        var accountId = await CreateAccount("Cash withdrawal");
        await Record(accountId, "-20", "2026-06-02", "ATM cash withdrawal");
        await Record(accountId, "-2", "2026-06-02", "Cash withdrawal fee");

        var actuals = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "none" });

        Assert.Equal(2, actuals.GetProperty("totalCount").GetInt32());
        Assert.All(actuals.GetProperty("items").EnumerateArray(), item => Assert.Equal("none", item.GetProperty("relationshipState").GetString()));
        AssertTotals(actuals, "-22", "22", "22");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_full_refund_offsets_the_original_current_category_and_pool_on_the_credit_date()
    {
        var accountId = await CreateAccount("Refund");
        var categoryId = await CreateCategory("Refund category");
        var poolId = await CreatePool("Refund pool");
        var original = await Record(accountId, "-30", "2026-01-05", "Purchase");
        var credit = await Record(accountId, "30", "2026-02-05", "Refund");
        await AssignCategory(original, categoryId);
        await AssignPool(await GetTransaction(original), poolId);
        await Success(
            ["ledger", "refund", "confirm", "--input", "-"],
            Envelope(new JsonObject { ["originalTransactionId"] = original, ["refundTransactionId"] = credit, ["reason"] = "Full refund" }, Key("refund")),
            "ledger.refund.confirm");

        var actuals = await Query(new JsonObject
        {
            ["accountIds"] = Array(accountId),
            ["effectiveFrom"] = "2026-02-01",
            ["effectiveTo"] = "2026-02-28",
            ["groupBy"] = "pool_category"
        });

        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal("refund_credit", item.GetProperty("relationshipState").GetString());
        Assert.Equal(categoryId, item.GetProperty("categoryId").GetString());
        Assert.Equal(poolId, item.GetProperty("poolId").GetString());
        AssertTotals(actuals, "30", "-30", "-30");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_subtree_groups_reconcile_once_and_follow_current_hierarchy()
    {
        var accountId = await CreateAccount("Hierarchy");
        var oldParent = await CreateCategory("Old parent");
        var newParent = await CreateCategory("New parent");
        var child = await CreateCategory("Child", oldParent);
        var transaction = await Record(accountId, "-11", "2026-03-01", "Hierarchy purchase");
        await AssignCategory(transaction, child);

        var before = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "category_subtree" });
        Assert.Equal(2, before.GetProperty("groups").GetArrayLength());
        Assert.All(before.GetProperty("groups").EnumerateArray(), group => Assert.Equal("11", group.GetProperty("totals").GetProperty("budgetActual").GetString()));
        await Success(
            ["ledger", "category", "reparent", "--input", "-"],
            Envelope(new JsonObject { ["categoryId"] = child, ["parentCategoryId"] = newParent, ["reason"] = "Owner moved category" }, Key("reparent")),
            "ledger.category.reparent");

        var after = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "category_subtree" });
        var groupIds = after.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("categoryId").GetString()).ToArray();
        Assert.Equal(2, groupIds.Length);
        Assert.Contains(newParent, groupIds);
        Assert.Contains(child, groupIds);
        Assert.DoesNotContain(oldParent, groupIds);
        AssertTotals(after, "-11", "11", "11");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_unknown_uncategorized_and_unassigned_buckets_are_explicit()
    {
        var accountId = await CreateAccount("Unknown buckets");
        var transaction = await Record(accountId, "-7.50", "2026-03-02", "Unknown dimensions");

        var actuals = await Query(new JsonObject
        {
            ["accountIds"] = Array(accountId),
            ["categorizationStates"] = Array("uncategorized"),
            ["poolStates"] = Array("unassigned"),
            ["instrumentStates"] = Array("unknown"),
            ["cardholderStates"] = Array("unknown"),
            ["groupBy"] = "pool_category"
        });

        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal(transaction, item.GetProperty("transactionId").GetString());
        Assert.Equal("uncategorized", item.GetProperty("categoryState").GetString());
        Assert.Equal("unassigned", item.GetProperty("poolState").GetString());
        Assert.Equal("unknown", item.GetProperty("instrumentState").GetString());
        Assert.Equal("unknown", item.GetProperty("cardholderState").GetString());
        var group = Assert.Single(actuals.GetProperty("groups").EnumerateArray());
        Assert.Equal("uncategorized", group.GetProperty("categoryState").GetString());
        Assert.Equal("unassigned", group.GetProperty("poolState").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_all_pages_return_each_member_once_with_full_set_totals()
    {
        var accountId = await CreateAccount("Pages");
        var ids = new[]
        {
            await Record(accountId, "-1", "2026-07-01", "One"),
            await Record(accountId, "-2", "2026-07-02", "Two"),
            await Record(accountId, "10", "2026-07-03", "Three")
        };
        var pages = await AllPages(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "category_direct" }, 1);

        Assert.Equal(3, pages.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal), pages.SelectMany(Items).Select(item => item.GetProperty("transactionId").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(pages, page =>
        {
            Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
            AssertTotals(page, "7", "3", "3");
            Assert.Equal(pages[0].GetProperty("groups").GetRawText(), page.GetProperty("groups").GetRawText());
        });
        Assert.Null(pages[^1].GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_later_pages_keep_frozen_membership_ancestry_pool_and_totals_after_writes()
    {
        var accountId = await CreateAccount("Frozen pages");
        var oldParent = await CreateCategory("Frozen old");
        var newParent = await CreateCategory("Frozen new");
        var child = await CreateCategory("Frozen child", oldParent);
        var oldPool = await CreatePool("Frozen old pool");
        var newPool = await CreatePool("Frozen new pool");
        var target = await Record(accountId, "-4", "2026-08-01", "Frozen target");
        await Record(accountId, "-6", "2026-08-02", "Frozen other");
        await AssignCategory(target, child);
        await AssignPool(await GetTransaction(target), oldPool);
        var first = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "pool_category" }, 1);

        await Record(accountId, "-100", "2026-08-03", "Later mutation");
        await Success(
            ["ledger", "category", "reparent", "--input", "-"],
            Envelope(new JsonObject { ["categoryId"] = child, ["parentCategoryId"] = newParent, ["reason"] = "Later move" }, Key("later-reparent")),
            "ledger.category.reparent");
        await CorrectPool(await GetTransaction(target), newPool);
        await MatchStatementEvidence(accountId, target, "-4", "2026-08-01", "2026-08-01", "2026-08-31");
        var pages = await RemainingPages(first);
        var frozen = pages.SelectMany(Items).Single(item => item.GetProperty("transactionId").GetString() == target);

        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { oldParent, child }, frozen.GetProperty("frozenAncestryIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(oldPool, frozen.GetProperty("poolId").GetString());
        Assert.Equal("recorded_unreconciled", frozen.GetProperty("reconciliationState").GetString());
        Assert.All(pages, page => AssertTotals(page, "-10", "10", "10"));
        var live = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["categoryIds"] = Array(newParent), ["categoryScope"] = "subtree", ["poolIds"] = Array(newPool), ["groupBy"] = "pool_category" });
        var current = Assert.Single(live.GetProperty("items").EnumerateArray());
        Assert.Equal(target, current.GetProperty("transactionId").GetString());
        Assert.Equal("owner_confirmed_match", current.GetProperty("reconciliationState").GetString());
    }

    [Theory]
    [InlineData("not-a-cursor", "LEDGER-SNAPSHOT-CURSOR-INVALID", 7)]
    [InlineData("", "LEDGER-SNAPSHOT-CURSOR-INVALID", 7)]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_invalid_encoding_fails_without_live_fallback(string cursor, string error, int exit)
    {
        var result = await Run(
            ["ledger", "actuals", "query", "--input", "-"],
            Envelope(new JsonObject { ["filter"] = null, ["pageSize"] = null, ["cursor"] = cursor }));

        AssertError(result, exit, error);
    }

    [Fact]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_cursor_with_altered_filters_is_rejected()
    {
        var accountId = await CreateAccount("Filter cursor");
        await Record(accountId, "-1", "2026-09-01", "First cursor item");
        await Record(accountId, "-2", "2026-09-02", "Second cursor item");
        var first = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "none" }, 1);

        var result = await Run(
            ["ledger", "actuals", "query", "--input", "-"],
            Envelope(new JsonObject
            {
                ["filter"] = new JsonObject { ["accountIds"] = Array(accountId), ["effectiveFrom"] = "2026-09-02", ["groupBy"] = "none" },
                ["pageSize"] = null,
                ["cursor"] = first.GetProperty("cursor").GetString()
            }));

        AssertError(result, 7, "LEDGER-SNAPSHOT-FILTER-MISMATCH");
    }

    [Fact]
    public async Task FR_LEDGER_ACTUALS_QUERY_statement_authority_excludes_the_superseded_fact_and_counts_the_replacement_once()
    {
        var accountId = await CreateAccount("Statement correction");
        var categoryId = await CreateCategory("Statement category");
        var poolId = await CreatePool("Statement pool");
        var prior = await Record(accountId, "-12", "2026-10-12", "Notification amount");
        await AssignCategory(prior, categoryId);
        await AssignPool(await GetTransaction(prior), poolId);
        var evidenceId = await RegisterStatementEvidence(accountId, "-12.34", "2026-10-12");
        var scope = await Success(
            ["ledger", "reconciliation", "scope", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["periodStart"] = "2026-10-01",
                ["periodEnd"] = "2026-10-31",
                ["manifestOpaqueReference"] = "statement:october",
                ["evidenceIds"] = Array(evidenceId)
            }, Key("scope")),
            "ledger.reconciliation.scope.register");
        var projection = await Success(
            ["ledger", "reconciliation", "candidates", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = evidenceId,
                ["scopeId"] = scope.GetProperty("scopeId").GetString(),
                ["policyId"] = "manual_review_projection",
                ["policyVersion"] = "1.0"
            }),
            "ledger.reconciliation.candidates");
        var candidateIds = projection.GetProperty("guardCandidates").EnumerateArray().Select(candidate => candidate.GetProperty("transactionId").GetString()!).ToArray();
        Assert.Contains(prior, candidateIds);
        var corrected = await Success(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = evidenceId,
                ["evidenceFingerprint"] = projection.GetProperty("evidenceFingerprint").GetString(),
                ["scopeId"] = scope.GetProperty("scopeId").GetString(),
                ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
                ["disposition"] = "correct_existing_from_statement",
                ["authorityKind"] = "owner",
                ["reviewedCandidateIds"] = new JsonArray(candidateIds.Select(value => JsonValue.Create(value)).ToArray()),
                ["targetTransactionId"] = prior,
                ["statementFact"] = new JsonObject
                {
                    ["accountId"] = accountId,
                    ["signedAmount"] = "-12.34",
                    ["currencyCode"] = "ZAR",
                    ["transactionDate"] = "2026-10-12",
                    ["postingDate"] = null,
                    ["originalDescription"] = "Authoritative statement row"
                },
                ["exceptionCode"] = null,
                ["reason"] = "Owner approved statement correction"
            }, Key("apply-correction")),
            "ledger.reconciliation.apply");
        var replacement = corrected.GetProperty("activeTransactionId").GetString()!;

        var actuals = await Query(new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "pool_category" });
        var item = Assert.Single(actuals.GetProperty("items").EnumerateArray());
        Assert.Equal(replacement, item.GetProperty("transactionId").GetString());
        Assert.NotEqual(prior, replacement);
        Assert.Equal(categoryId, item.GetProperty("categoryId").GetString());
        Assert.Equal(poolId, item.GetProperty("poolId").GetString());
        Assert.Equal("statement_reconciled", item.GetProperty("reconciliationState").GetString());
        AssertTotals(actuals, "-12.34", "12.34", "12.34");
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

    private async Task<string> CreateAccount(string label) => (await Success(
        ["ledger", "account", "create", "--input", "-"],
        Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = label + " " + Key("account-label"),
            ["accountType"] = "cheque",
            ["maskedIdentifier"] = "****" + sequence.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..],
            ["currencyCode"] = "ZAR"
        }, Key("account")),
        "ledger.account.create")).GetProperty("accountId").GetString()!;

    private async Task<string> CreateCategory(string name, string? parentId = null) => (await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name + " " + Key("category-label"), ["parentCategoryId"] = parentId }, Key("category")),
        "ledger.category.create")).GetProperty("categoryId").GetString()!;

    private async Task<string> CreatePool(string name) => (await Success(
        ["ledger", "pool", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name + " " + Key("pool-label") }, Key("pool")),
        "ledger.pool.create")).GetProperty("poolId").GetString()!;

    private async Task<string> CreateInstrument(string accountId, string label) => (await Success(
        ["ledger", "instrument", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label, ["accountId"] = accountId, ["maskedSuffix"] = "1234" }, Key("instrument")),
        "ledger.instrument.create")).GetProperty("instrumentId").GetString()!;

    private async Task<string> CreateCardholder(string label) => (await Success(
        ["ledger", "cardholder", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = label + " " + Key("cardholder-label") }, Key("cardholder")),
        "ledger.cardholder.create")).GetProperty("cardholderId").GetString()!;

    private async Task<string> Record(
        string accountId,
        string amount,
        string date,
        string description,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var token = Key("record");
        return (await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(new JsonObject
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
            }, token),
            "ledger.transaction.record")).GetProperty("transactionId").GetString()!;
    }

    private async Task AssignCategory(string transactionId, string categoryId) => _ = await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classification" }, Key("category-assign")),
        "ledger.transaction.category.assign");

    private async Task AssignPool(JsonElement transaction, string poolId) => _ = await Success(
        ["ledger", "transaction", "pool", "assign", "--input", "-"],
        PoolEnvelope(transaction, poolId, "Owner pool assignment", Key("pool-assign")),
        "ledger.transaction.pool.assign");

    private async Task CorrectPool(JsonElement transaction, string poolId) => _ = await Success(
        ["ledger", "transaction", "pool", "correct", "--input", "-"],
        PoolEnvelope(transaction, poolId, "Owner pool correction", Key("pool-correct")),
        "ledger.transaction.pool.correct");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<string> RegisterStatementEvidence(string accountId, string amount, string date)
    {
        var token = Key("statement-evidence");
        return (await Success(
            ["ledger", "evidence", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["kind"] = "statement_row",
                ["logicalIdentityDigest"] = Digest(token),
                ["opaqueExternalReference"] = "statement:" + token,
                ["contentFingerprint"] = Digest("content-" + token),
                ["observation"] = new JsonObject
                {
                    ["accountId"] = accountId,
                    ["signedAmountMinor"] = Minor(amount),
                    ["currencyCode"] = "ZAR",
                    ["transactionDate"] = date,
                    ["postingDate"] = null,
                    ["instrumentId"] = null,
                    ["cardholderId"] = null,
                    ["descriptionFingerprint"] = Digest("description-" + token)
                }
            }, token),
            "ledger.evidence.register")).GetProperty("evidenceId").GetString()!;
    }

    private async Task MatchStatementEvidence(
        string accountId,
        string transactionId,
        string amount,
        string date,
        string periodStart,
        string periodEnd)
    {
        var evidenceId = await RegisterStatementEvidence(accountId, amount, date);
        var scope = await Success(
            ["ledger", "reconciliation", "scope", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["periodStart"] = periodStart,
                ["periodEnd"] = periodEnd,
                ["manifestOpaqueReference"] = "statement:" + Key("match-manifest"),
                ["evidenceIds"] = Array(evidenceId)
            }, Key("match-scope")),
            "ledger.reconciliation.scope.register");
        var projection = await Success(
            ["ledger", "reconciliation", "candidates", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = evidenceId,
                ["scopeId"] = scope.GetProperty("scopeId").GetString(),
                ["policyId"] = "manual_review_projection",
                ["policyVersion"] = "1.0"
            }),
            "ledger.reconciliation.candidates");
        var candidateIds = projection.GetProperty("exactCandidates").EnumerateArray()
            .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
            .Select(candidate => candidate.GetProperty("transactionId").GetString()!).ToArray();
        Assert.Contains(transactionId, candidateIds);
        await Success(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = evidenceId,
                ["evidenceFingerprint"] = projection.GetProperty("evidenceFingerprint").GetString(),
                ["scopeId"] = scope.GetProperty("scopeId").GetString(),
                ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
                ["disposition"] = "match_existing",
                ["authorityKind"] = "owner",
                ["reviewedCandidateIds"] = new JsonArray(candidateIds.Select(value => JsonValue.Create(value)).ToArray()),
                ["targetTransactionId"] = transactionId,
                ["statementFact"] = null,
                ["exceptionCode"] = null,
                ["reason"] = "Owner confirmed exact statement match"
            }, Key("match-apply")),
            "ledger.reconciliation.apply");
    }

    private async Task<JsonElement> Query(JsonObject filter, int pageSize = 100) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject { ["filter"] = filter.DeepClone(), ["pageSize"] = pageSize, ["cursor"] = null }),
        "ledger.actuals.query");

    private async Task<JsonElement> Continue(string cursor) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject { ["filter"] = null, ["pageSize"] = null, ["cursor"] = cursor }),
        "ledger.actuals.query");

    private async Task<List<JsonElement>> AllPages(JsonObject filter, int pageSize) => await RemainingPages(await Query(filter, pageSize));

    private async Task<List<JsonElement>> RemainingPages(JsonElement first)
    {
        var pages = new List<JsonElement> { first };
        while (pages[^1].GetProperty("cursor").GetString() is { } cursor) pages.Add(await Continue(cursor));
        return pages;
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc005-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static IEnumerable<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.Clone());

    private static string ItemDigest(JsonElement item) => string.Join('|', new[]
    {
        item.GetProperty("transactionId").GetString(),
        item.GetProperty("categoryState").GetString(),
        item.GetProperty("poolState").GetString(),
        item.GetProperty("instrumentState").GetString(),
        item.GetProperty("cardholderState").GetString(),
        item.GetProperty("reconciliationState").GetString(),
        item.GetProperty("relationshipState").GetString(),
        item.GetProperty("contribution").GetRawText()
    });

    private static long Minor(string amount) => decimal.ToInt64(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture) * 100m);

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PoolEnvelope(JsonElement transaction, string poolId, string reason, string key) => Envelope(
        new JsonObject
        {
            ["transactionId"] = transaction.GetProperty("transactionId").GetString(),
            ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
            ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId },
            ["reason"] = reason
        },
        key);

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc005", ["runId"] = "published-e2e" },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stdout} {result.Stderr}");
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
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertTotals(JsonElement actuals, string net, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(net, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
    }
}
