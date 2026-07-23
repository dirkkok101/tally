using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC009TransactionCorrectionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc009-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_009_agent_discovers_closed_void_supersede_and_history_contracts()
    {
        foreach (var (operationId, requestType) in new[]
                 {
                     ("ledger.transaction.void", "VoidTransactionInput"),
                     ("ledger.transaction.supersede", "SupersedeTransactionInput"),
                     ("ledger.transaction.get", "GetTransactionInput")
                 })
        {
            var operation = (await Success(["schema", "show", operationId, "--input", "-"], Envelope(new JsonObject()), "system.schema.show")).GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.Equal(operationId != "ledger.transaction.get", operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_ordinary_supersede_preserves_prior_meaning_without_copying_it()
    {
        var accountId = await CreateAccount("Primary", "cheque");
        var counterpartAccountId = await CreateAccount("Savings", "savings");
        var instrumentId = await CreateInstrument(accountId);
        var cardholderId = await CreateCardholder();
        var categoryId = await CreateCategory();
        var poolId = await CreatePool();
        var original = await Record(accountId, "-12.34", "2026-07-01", "Original transaction", instrumentId, cardholderId);
        var originalId = Id(original);
        await AssignCategory(originalId, categoryId);
        await AssignPool(original, poolId);
        var evidenceId = await RegisterStatementEvidence(accountId, -1234, "2026-07-01");
        await ApplyExactMatch(accountId, evidenceId, originalId);
        var counterpart = await Record(counterpartAccountId, "12.34", "2026-07-01", "Transfer counterpart");
        var relationship = await Success(["ledger", "transfer", "confirm", "--input", "-"], Envelope(new JsonObject
        {
            ["outflowTransactionId"] = originalId,
            ["inflowTransactionId"] = Id(counterpart),
            ["reason"] = "Owner confirmed transfer"
        }, Key("transfer")), "ledger.transfer.confirm");

        var corrected = await Supersede(originalId, Replacement(accountId, "-13.57", "Ordinary replacement"), "Owner corrected notification", Key("supersede"));
        var prior = corrected.GetProperty("original");
        var replacement = corrected.GetProperty("replacement");
        var replacementId = Id(replacement);

        Assert.Equal("superseded", corrected.GetProperty("action").GetString());
        Assert.Equal("superseded", prior.GetProperty("lifecycleStatus").GetString());
        Assert.Equal(replacementId, prior.GetProperty("activeReplacementTransactionId").GetString());
        Assert.Equal("reconciliation_exception", prior.GetProperty("reconciliationState").GetString());
        Assert.Equal(categoryId, prior.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(poolId, prior.GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal(instrumentId, prior.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal(cardholderId, prior.GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        Assert.Contains(prior.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == evidenceId);
        Assert.Equal("active", replacement.GetProperty("lifecycleStatus").GetString());
        Assert.Equal("recorded_unreconciled", replacement.GetProperty("reconciliationState").GetString());
        Assert.Equal("uncategorized", replacement.GetProperty("category").GetProperty("state").GetString());
        Assert.Equal("unassigned", replacement.GetProperty("pool").GetProperty("state").GetString());
        Assert.Equal("unknown", replacement.GetProperty("paymentAttribution").GetProperty("instrumentState").GetString());
        Assert.Equal("unknown", replacement.GetProperty("paymentAttribution").GetProperty("cardholderState").GetString());
        Assert.DoesNotContain(replacement.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == evidenceId);
        Assert.Empty(replacement.GetProperty("history").GetProperty("categoryAssignments").EnumerateArray());
        Assert.Single(replacement.GetProperty("history").GetProperty("poolAssignments").EnumerateArray());
        Assert.Single(replacement.GetProperty("history").GetProperty("paymentAttribution").EnumerateArray());
        Assert.Equal([relationship.GetProperty("relationshipId").GetString()], corrected.GetProperty("retiredRelationshipIds").EnumerateArray().Select(item => item.GetString()));
        var retired = await GetRelationship(relationship.GetProperty("relationshipId").GetString()!);
        Assert.Equal("retired", retired.GetProperty("state").GetString());
        Assert.Equal("revoked", Assert.Single(retired.GetProperty("history").EnumerateArray()).GetProperty("action").GetString());
        var actuals = await Actuals(accountId);
        Assert.Equal(replacementId, Assert.Single(actuals.GetProperty("items").EnumerateArray()).GetProperty("transactionId").GetString());
        AssertTotals(actuals, "-13.57", "13.57", "13.57");
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_void_keeps_facts_and_attributable_history_but_removes_current_actuals()
    {
        var accountId = await CreateAccount("Void account", "cheque");
        var original = await Record(accountId, "-8.25", "2026-07-02", "Duplicate notification");

        var result = await Void(Id(original), "Owner removed duplicate", Key("void"));
        var prior = result.GetProperty("original");

        Assert.Equal("void", result.GetProperty("action").GetString());
        Assert.Equal("voided", prior.GetProperty("lifecycleStatus").GetString());
        Assert.Equal("-8.25", prior.GetProperty("signedAmount").GetString());
        Assert.Equal("Duplicate notification", prior.GetProperty("originalDescription").GetString());
        Assert.Null(prior.GetProperty("activeReplacementTransactionId").GetString());
        var lifecycle = Assert.Single(prior.GetProperty("history").GetProperty("lifecycle").EnumerateArray());
        Assert.Equal("Owner removed duplicate", lifecycle.GetProperty("reason").GetString());
        Assert.Equal("automation:uc009:published-e2e", lifecycle.GetProperty("actor").GetString());
        Assert.Empty((await Actuals(accountId)).GetProperty("items").EnumerateArray());
        var fetched = await GetTransaction(Id(original));
        Assert.Equal(prior.GetRawText(), fetched.GetRawText());
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_exact_and_cross_key_replay_converge_while_changed_reuse_conflicts()
    {
        var accountId = await CreateAccount("Replay account", "cheque");
        var original = await Record(accountId, "-12.34", "2026-07-03", "Replay source");
        var replacement = Replacement(accountId, "-12.35", "Replay replacement");
        var request = SupersedeEnvelope(Id(original), replacement, "Correct amount", "same-key");

        var first = await Run(["ledger", "transaction", "supersede", "--input", "-"], request);
        var replay = await Run(["ledger", "transaction", "supersede", "--input", "-"], request);
        var crossKey = await Run(["ledger", "transaction", "supersede", "--input", "-"], SupersedeEnvelope(Id(original), replacement, "Correct amount", "cross-key"));
        var changed = await Run(["ledger", "transaction", "supersede", "--input", "-"], SupersedeEnvelope(Id(original), replacement, "Changed reason", "same-key"));

        AssertSuccess(first, "ledger.transaction.supersede");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Single((await GetTransaction(Id(original))).GetProperty("history").GetProperty("lifecycle").EnumerateArray());
    }

    [Theory]
    [InlineData("missing", 4, "LEDGER-TRANSACTION-NOT-FOUND")]
    [InlineData("inactive", 6, "LEDGER-TRANSACTION-INACTIVE")]
    [InlineData("zero-amount", 3, "amount.zero")]
    [InlineData("archived-account", 6, "LEDGER-ACCOUNT-ARCHIVED")]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_invalid_or_stale_requests_preserve_the_prior_state(string scenario, int exit, string error)
    {
        var accountId = await CreateAccount("Validation account", "cheque");
        var original = await Record(accountId, "-4.00", "2026-07-04", "Validation source");
        PublishedTallyResult result;
        switch (scenario)
        {
            case "missing":
                result = await Run(["ledger", "transaction", "void", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = "01J00000000000000000000000", ["reason"] = "Missing" }, Key("missing")));
                break;
            case "inactive":
                await CorrectFromStatement(accountId, Id(original), -401, "-4.01", "2026-07-04");
                result = await Run(["ledger", "transaction", "void", "--input", "-"], Envelope(new JsonObject { ["transactionId"] = Id(original), ["reason"] = "Stale correction" }, Key("inactive")));
                break;
            case "zero-amount":
                result = await Run(["ledger", "transaction", "supersede", "--input", "-"], SupersedeEnvelope(Id(original), Replacement(accountId, "0", "Invalid replacement"), "Invalid amount", Key("zero")));
                break;
            case "archived-account":
                var archivedId = await CreateAccount("Archived replacement", "savings");
                await Success(["ledger", "account", "archive", "--input", "-"], Envelope(new JsonObject { ["accountId"] = archivedId, ["reason"] = "Archive test account" }, Key("archive")), "ledger.account.archive");
                result = await Run(["ledger", "transaction", "supersede", "--input", "-"], SupersedeEnvelope(Id(original), Replacement(archivedId, "-4.01", "Archived account replacement"), "Invalid account", Key("archived")));
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        AssertError(result, exit, error);
        var current = await GetTransaction(Id(original));
        Assert.Equal(scenario == "inactive" ? "superseded" : "active", current.GetProperty("lifecycleStatus").GetString());
        Assert.Equal(scenario == "inactive", current.GetProperty("activeReplacementTransactionId").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_CORRECTION_relationship_retirement_failure_rolls_back_and_same_request_retries_cleanly()
    {
        var sourceAccountId = await CreateAccount("Rollback source", "cheque");
        var targetAccountId = await CreateAccount("Rollback target", "savings");
        var source = await Record(sourceAccountId, "-20", "2026-07-05", "Rollback source");
        var target = await Record(targetAccountId, "20", "2026-07-05", "Rollback target");
        var relationship = await Success(["ledger", "transfer", "confirm", "--input", "-"], Envelope(new JsonObject
        {
            ["outflowTransactionId"] = Id(source),
            ["inflowTransactionId"] = Id(target),
            ["reason"] = "Rollback relationship"
        }, Key("rollback-transfer")), "ledger.transfer.confirm");
        const string trigger = "uc009_fail_relationship_retirement";
        await ExecuteCurrent($"CREATE TRIGGER {trigger} BEFORE INSERT ON relationship_lifecycle_event BEGIN SELECT RAISE(ABORT, 'injected retirement failure'); END;");
        var request = SupersedeEnvelope(Id(source), Replacement(sourceAccountId, "-20", "Rollback replacement"), "Retry correction", "rollback-supersede");

        var failed = await Run(["ledger", "transaction", "supersede", "--input", "-"], request);

        AssertError(failed, 10, "host.unexpected");
        Assert.Equal("active", (await GetTransaction(Id(source))).GetProperty("lifecycleStatus").GetString());
        Assert.Equal("active", (await GetRelationship(relationship.GetProperty("relationshipId").GetString()!)).GetProperty("state").GetString());
        await ExecuteCurrent($"DROP TRIGGER {trigger};");
        var recovered = AssertSuccess(await Run(["ledger", "transaction", "supersede", "--input", "-"], request), "ledger.transaction.supersede");
        Assert.Equal("superseded", recovered.GetProperty("original").GetProperty("lifecycleStatus").GetString());
        Assert.Equal("retired", (await GetRelationship(relationship.GetProperty("relationshipId").GetString()!)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_statement_apply_is_the_only_path_that_carries_dimensions_forward()
    {
        var accountId = await CreateAccount("Statement correction", "cheque");
        var instrumentId = await CreateInstrument(accountId);
        var cardholderId = await CreateCardholder();
        var categoryId = await CreateCategory();
        var poolId = await CreatePool();
        var original = await Record(accountId, "-12.00", "2026-07-06", "Notification fact", instrumentId, cardholderId);
        var originalId = Id(original);
        await AssignCategory(originalId, categoryId);
        await AssignPool(original, poolId);
        var (applied, statementEvidenceId) = await CorrectFromStatement(accountId, originalId, -1234, "-12.34", "2026-07-06");
        var decisionId = applied.GetProperty("decisionId").GetString();
        var replacementId = applied.GetProperty("activeTransactionId").GetString()!;
        var prior = await GetTransaction(originalId);
        var replacement = await GetTransaction(replacementId);

        Assert.Equal("statement_authoritative_replacement", Assert.Single(prior.GetProperty("history").GetProperty("lifecycle").EnumerateArray()).GetProperty("action").GetString());
        Assert.Equal(decisionId, prior.GetProperty("history").GetProperty("lifecycle")[0].GetProperty("reconciliationDecisionId").GetString());
        Assert.Equal(categoryId, replacement.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(poolId, replacement.GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal(instrumentId, replacement.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal(cardholderId, replacement.GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        AssertCarryForward(replacement.GetProperty("history").GetProperty("categoryAssignments"), originalId, decisionId);
        AssertCarryForward(replacement.GetProperty("history").GetProperty("poolAssignments"), originalId, decisionId);
        AssertCarryForward(replacement.GetProperty("history").GetProperty("paymentAttribution"), originalId, decisionId);
        Assert.Contains(prior.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("kind").GetString() == "agent_capture");
        var confirming = Assert.Single(replacement.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == statementEvidenceId);
        Assert.Equal("confirming", confirming.GetProperty("role").GetString());
        Assert.Equal("statement_reconciled", replacement.GetProperty("reconciliationState").GetString());
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

    private async Task<string> CreateAccount(string name, string accountType) => (await Success(
        ["ledger", "account", "create", "--input", "-"],
        Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = name + " " + sequence,
            ["accountType"] = accountType,
            ["maskedIdentifier"] = "****" + (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..],
            ["currencyCode"] = "ZAR"
        }, Key("account")),
        "ledger.account.create")).GetProperty("accountId").GetString()!;

    private async Task<string> CreateInstrument(string accountId) => (await Success(
        ["ledger", "instrument", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = "UC009 instrument " + sequence, ["accountId"] = accountId, ["maskedSuffix"] = "9009" }, Key("instrument")),
        "ledger.instrument.create")).GetProperty("instrumentId").GetString()!;

    private async Task<string> CreateCardholder() => (await Success(
        ["ledger", "cardholder", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = "UC009 owner " + sequence }, Key("cardholder")),
        "ledger.cardholder.create")).GetProperty("cardholderId").GetString()!;

    private async Task<string> CreateCategory() => (await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "UC009 category " + sequence, ["parentCategoryId"] = null }, Key("category")),
        "ledger.category.create")).GetProperty("categoryId").GetString()!;

    private async Task<string> CreatePool() => (await Success(
        ["ledger", "pool", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "UC009 pool " + sequence }, Key("pool")),
        "ledger.pool.create")).GetProperty("poolId").GetString()!;

    private async Task<JsonElement> Record(string accountId, string amount, string date, string description, string? instrumentId = null, string? cardholderId = null) =>
        await Success(["ledger", "transaction", "record", "--input", "-"], Envelope(Replacement(accountId, amount, description, date, instrumentId, cardholderId), Key("record")), "ledger.transaction.record");

    private JsonObject Replacement(string accountId, string amount, string description, string date = "2026-07-01", string? instrumentId = null, string? cardholderId = null)
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
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classification" }, Key("category-assign")),
        "ledger.transaction.category.assign");

    private async Task AssignPool(JsonElement transaction, string poolId) => _ = await Success(
        ["ledger", "transaction", "pool", "assign", "--input", "-"],
        Envelope(new JsonObject
        {
            ["transactionId"] = Id(transaction),
            ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
            ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId },
            ["reason"] = "Owner pool assignment"
        }, Key("pool-assign")),
        "ledger.transaction.pool.assign");

    private async Task<string> RegisterStatementEvidence(string accountId, long amountMinor, string date)
    {
        var token = Key("statement");
        return (await Success(["ledger", "evidence", "register", "--input", "-"], Envelope(new JsonObject
        {
            ["kind"] = "statement_row",
            ["logicalIdentityDigest"] = Digest(token),
            ["opaqueExternalReference"] = "statement:" + token,
            ["contentFingerprint"] = Digest("content:" + token),
            ["observation"] = new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmountMinor"] = amountMinor,
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = date,
                ["postingDate"] = null,
                ["instrumentId"] = null,
                ["cardholderId"] = null,
                ["descriptionFingerprint"] = Digest("description:" + token)
            }
        }, token), "ledger.evidence.register")).GetProperty("evidenceId").GetString()!;
    }

    private async Task ApplyExactMatch(string accountId, string evidenceId, string transactionId)
    {
        var scopeId = await RegisterScope(accountId, evidenceId, "statement:uc009-exact-" + sequence);
        var projection = await Project(evidenceId, scopeId);
        var candidates = CandidateIds(projection);
        Assert.Contains(transactionId, candidates);
        await Success(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = evidenceId,
            ["evidenceFingerprint"] = projection.GetProperty("evidenceFingerprint").GetString(),
            ["scopeId"] = scopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "match_existing",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = new JsonArray(candidates.Select(value => JsonValue.Create(value)).ToArray()),
            ["targetTransactionId"] = transactionId,
            ["statementFact"] = null,
            ["exceptionCode"] = null,
            ["reason"] = "Owner confirmed statement match"
        }, Key("exact-match")), "ledger.reconciliation.apply");
    }

    private async Task<(JsonElement Applied, string EvidenceId)> CorrectFromStatement(
        string accountId,
        string transactionId,
        long amountMinor,
        string amount,
        string date)
    {
        var evidenceId = await RegisterStatementEvidence(accountId, amountMinor, date);
        var scopeId = await RegisterScope(accountId, evidenceId, "statement:uc009-correction-" + sequence);
        var projection = await Project(evidenceId, scopeId);
        var candidates = CandidateIds(projection);
        Assert.Contains(transactionId, candidates);
        var applied = await Success(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = evidenceId,
            ["evidenceFingerprint"] = projection.GetProperty("evidenceFingerprint").GetString(),
            ["scopeId"] = scopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "correct_existing_from_statement",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = new JsonArray(candidates.Select(value => JsonValue.Create(value)).ToArray()),
            ["targetTransactionId"] = transactionId,
            ["statementFact"] = new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmount"] = amount,
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = date,
                ["postingDate"] = null,
                ["originalDescription"] = "Authoritative statement fact"
            },
            ["exceptionCode"] = null,
            ["reason"] = "Owner approved statement authority"
        }, Key("statement-correction")), "ledger.reconciliation.apply");
        return (applied, evidenceId);
    }

    private async Task<string> RegisterScope(string accountId, string evidenceId, string reference) => (await Success(
        ["ledger", "reconciliation", "scope", "register", "--input", "-"],
        Envelope(new JsonObject
        {
            ["accountId"] = accountId,
            ["periodStart"] = "2026-07-01",
            ["periodEnd"] = "2026-07-31",
            ["manifestOpaqueReference"] = reference,
            ["evidenceIds"] = Array(evidenceId)
        }, Key("scope")),
        "ledger.reconciliation.scope.register")).GetProperty("scopeId").GetString()!;

    private async Task<JsonElement> Project(string evidenceId, string scopeId) => await Success(
        ["ledger", "reconciliation", "candidates", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = evidenceId,
            ["scopeId"] = scopeId,
            ["policyId"] = "manual_review_projection",
            ["policyVersion"] = "1.0"
        }),
        "ledger.reconciliation.candidates");

    private async Task<JsonElement> Supersede(string transactionId, JsonObject replacement, string reason, string key) =>
        await Success(["ledger", "transaction", "supersede", "--input", "-"], SupersedeEnvelope(transactionId, replacement, reason, key), "ledger.transaction.supersede");

    private async Task<JsonElement> Void(string transactionId, string reason, string key) => await Success(
        ["ledger", "transaction", "void", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["reason"] = reason }, key),
        "ledger.transaction.void");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> GetRelationship(string relationshipId) => await Success(
        ["ledger", "relationship", "get", "--input", "-"],
        Envelope(new JsonObject { ["relationshipId"] = relationshipId, ["includeHistory"] = true }),
        "ledger.relationship.get");

    private async Task<JsonElement> Actuals(string accountId) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "pool_category" },
            ["pageSize"] = 100,
            ["cursor"] = null
        }),
        "ledger.actuals.query");

    private async Task ExecuteCurrent(string sql)
    {
        var generationId = (await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT"))).Trim();
        var path = Path.Combine(dataRoot, "generations", generationId, "ledger.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc009-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string SupersedeEnvelope(string transactionId, JsonObject replacement, string reason, string key) =>
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["replacement"] = replacement.DeepClone(), ["reason"] = reason }, key);

    private static string[] CandidateIds(JsonElement projection) => projection.GetProperty("exactCandidates").EnumerateArray()
        .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
        .Select(candidate => candidate.GetProperty("transactionId").GetString()!)
        .ToArray();

    private static string Id(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertCarryForward(JsonElement history, string sourceTransactionId, string? decisionId)
    {
        var carry = Assert.Single(history.EnumerateArray(), item => item.GetProperty("action").GetString() == "carry_forward");
        Assert.Equal(sourceTransactionId, carry.GetProperty("sourceTransactionId").GetString());
        Assert.Equal(decisionId, carry.GetProperty("reconciliationDecisionId").GetString());
    }

    private static void AssertTotals(JsonElement actuals, string movement, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(movement, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
    }

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc009", ["runId"] = "published-e2e" },
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
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
