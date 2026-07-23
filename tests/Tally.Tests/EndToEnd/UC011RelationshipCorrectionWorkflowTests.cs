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
public sealed class UC011RelationshipCorrectionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc011-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_011_agent_discovers_typed_relationship_lifecycle_contracts()
    {
        foreach (var (operationId, requestType, requiresKey) in new[]
                 {
                     ("ledger.transfer.revoke", "RevokeRelationshipInput", true),
                     ("ledger.transfer.replace", "ReplaceTransferInput", true),
                     ("ledger.refund.revoke", "RevokeRelationshipInput", true),
                     ("ledger.refund.replace", "ReplaceRefundInput", true),
                     ("ledger.relationship.get", "GetRelationshipInput", false)
                 })
        {
            var operation = (await Success(
                ["schema", "show", operationId, "--input", "-"],
                Envelope(new JsonObject()),
                "system.schema.show")).GetProperty("operation");

            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.Equal(requiresKey, operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_transfer_revoke_is_attributable_and_restores_ordinary_spend()
    {
        var transfer = await CreateTransfer("12.34");
        AssertTotals(await Actuals(), "0", "0", "0");

        var revoked = await Success(
            ["ledger", "transfer", "revoke", "--input", "-"],
            RevokeEnvelope(Id(transfer.Relationship), "Owner corrected transfer", "revoke-transfer"),
            "ledger.transfer.revoke");

        var retired = revoked.GetProperty("relationship");
        Assert.Equal("retired", retired.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, revoked.GetProperty("replacementRelationship").ValueKind);
        var history = Assert.Single(retired.GetProperty("history").EnumerateArray());
        Assert.Equal(revoked.GetProperty("lifecycleEventId").GetString(), history.GetProperty("lifecycleEventId").GetString());
        Assert.Equal("revoked", history.GetProperty("action").GetString());
        Assert.Equal("Owner corrected transfer", history.GetProperty("reason").GetString());
        Assert.Equal("automation:uc011:published-e2e", history.GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("replacementRelationshipId").ValueKind);
        Assert.Equal(retired.GetRawText(), (await GetRelationship(Id(transfer.Relationship))).GetRawText());

        var actuals = await Actuals();
        AssertTotals(actuals, "0", "12.34", "12.34");
        Assert.All(actuals.GetProperty("items").EnumerateArray(), item => Assert.Equal("none", item.GetProperty("relationshipState").GetString()));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_refund_revoke_removes_offset_and_preserves_history()
    {
        var refund = await CreateRefund("12.34");
        AssertTotals(await Actuals(), "0", "0", "0");

        var revoked = await Success(
            ["ledger", "refund", "revoke", "--input", "-"],
            RevokeEnvelope(Id(refund.Relationship), "Credit was not a refund", "revoke-refund"),
            "ledger.refund.revoke");

        var retired = revoked.GetProperty("relationship");
        Assert.Equal("refund", retired.GetProperty("type").GetString());
        Assert.Equal("retired", retired.GetProperty("state").GetString());
        Assert.Equal("revoked", Assert.Single(retired.GetProperty("history").EnumerateArray()).GetProperty("action").GetString());
        AssertTotals(await Actuals(), "0", "12.34", "12.34");
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_transfer_replace_retires_old_and_activates_one_distinct_link()
    {
        var transfer = await CreateTransfer("12.34");
        var replacementInflow = await Record(transfer.TargetAccountId, "12.34");

        var replaced = await Success(
            ["ledger", "transfer", "replace", "--input", "-"],
            ReplaceTransferEnvelope(
                Id(transfer.Relationship),
                Id(transfer.Source),
                Id(replacementInflow),
                "Correct transfer inflow",
                "replace-transfer"),
            "ledger.transfer.replace");

        AssertReplacementChain(replaced, "transfer", Id(transfer.Source), Id(replacementInflow), "Correct transfer inflow");
        var oldInflow = Assert.Single((await Actuals()).GetProperty("items").EnumerateArray(), item => item.GetProperty("transactionId").GetString() == Id(transfer.Target));
        Assert.Equal("none", oldInflow.GetProperty("relationshipState").GetString());
        AssertTotals(await Actuals(), "12.34", "0", "0");
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_full_refund_replace_keeps_exact_amount_and_one_active_offset()
    {
        var refund = await CreateRefund("12.34");
        var replacementCredit = await Record(refund.SourceAccountId, "12.34");

        var replaced = await Success(
            ["ledger", "refund", "replace", "--input", "-"],
            ReplaceRefundEnvelope(
                Id(refund.Relationship),
                Id(refund.Source),
                Id(replacementCredit),
                "Correct full refund credit",
                "replace-refund"),
            "ledger.refund.replace");

        AssertReplacementChain(replaced, "refund", Id(refund.Source), Id(replacementCredit), "Correct full refund credit");
        Assert.Equal("12.34", replaced.GetProperty("replacementRelationship").GetProperty("principalAmount").GetString());
        AssertTotals(await Actuals(), "12.34", "0", "0");
        Assert.Single((await Actuals()).GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() == "refund_original");
        Assert.Single((await Actuals()).GetProperty("items").EnumerateArray(), item => item.GetProperty("relationshipState").GetString() == "refund_credit");
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("key")]
    public async Task UC_LEDGER_011_absent_reason_or_idempotency_key_is_rejected_before_mutation(string missing)
    {
        var transfer = await CreateTransfer("12.34");
        var before = await GetRelationship(Id(transfer.Relationship));
        var request = RevokeEnvelope(
            Id(transfer.Relationship),
            missing == "reason" ? "" : "Owner correction",
            missing == "key" ? null : "invalid-revoke");

        AssertError(
            await Run(["ledger", "transfer", "revoke", "--input", "-"], request),
            3,
            missing == "key" ? "validation.invalid_input" : "LEDGER-RELATIONSHIP-LIFECYCLE-INVALID");
        Assert.Equal(before.GetRawText(), (await GetRelationship(Id(transfer.Relationship))).GetRawText());
        AssertTotals(await Actuals(), "0", "0", "0");
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_retired_or_wrong_type_requests_fail_without_another_event()
    {
        var transfer = await CreateTransfer("12.34");
        var relationshipId = Id(transfer.Relationship);
        AssertError(
            await Run(["ledger", "refund", "revoke", "--input", "-"], RevokeEnvelope(relationshipId, "Wrong type", "wrong-type")),
            6,
            "LEDGER-RELATIONSHIP-TYPE-MISMATCH");
        var replacementInflow = await Record(transfer.TargetAccountId, "12.34");
        await Success(
            ["ledger", "transfer", "replace", "--input", "-"],
            ReplaceTransferEnvelope(relationshipId, Id(transfer.Source), Id(replacementInflow), "Retire through replacement", "replace-before-revoke"),
            "ledger.transfer.replace");
        AssertError(
            await Run(["ledger", "transfer", "revoke", "--input", "-"], RevokeEnvelope(relationshipId, "Revoke retired link", "revoke-retired")),
            6,
            "LEDGER-RELATIONSHIP-ALREADY-RETIRED");
        Assert.Single((await GetRelationship(relationshipId)).GetProperty("history").EnumerateArray());
    }

    [Theory]
    [InlineData("transfer-amount", 3, "LEDGER-TRANSFER-AMOUNT")]
    [InlineData("refund-partial", 3, "LEDGER-REFUND-AMOUNT")]
    [InlineData("active-role", 5, "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT")]
    [InlineData("inactive", 6, "LEDGER-TRANSFER-TRANSACTION-INACTIVE")]
    public async Task UC_LEDGER_011_invalid_replacement_fails_closed_with_prior_relationship_and_actuals(string scenario, int exitCode, string errorCode)
    {
        var relationship = scenario == "refund-partial"
            ? await CreateRefund("12.34")
            : await CreateTransfer("12.34");
        var before = await GetRelationship(Id(relationship.Relationship));
        PublishedTallyResult result;

        if (scenario == "refund-partial")
        {
            var partial = await Record(relationship.SourceAccountId, "6.17");
            result = await Run(
                ["ledger", "refund", "replace", "--input", "-"],
                ReplaceRefundEnvelope(Id(relationship.Relationship), Id(relationship.Source), Id(partial), "Partial refund", "invalid-replacement"));
        }
        else
        {
            JsonElement candidate;
            if (scenario == "active-role")
            {
                candidate = (await CreateTransfer("12.34")).Target;
            }
            else
            {
                candidate = await Record(relationship.TargetAccountId, scenario == "transfer-amount" ? "12.33" : "12.34");
                if (scenario == "inactive")
                {
                    await Success(
                        ["ledger", "transaction", "void", "--input", "-"],
                        Envelope(new JsonObject { ["transactionId"] = Id(candidate), ["reason"] = "Inactive replacement" }, "void-replacement"),
                        "ledger.transaction.void");
                }
            }

            result = await Run(
                ["ledger", "transfer", "replace", "--input", "-"],
                ReplaceTransferEnvelope(Id(relationship.Relationship), Id(relationship.Source), Id(candidate), "Invalid replacement", "invalid-replacement"));
        }

        AssertError(result, exitCode, errorCode);
        Assert.Equal(before.GetRawText(), (await GetRelationship(Id(relationship.Relationship))).GetRawText());
        Assert.Equal("active", (await GetRelationship(Id(relationship.Relationship))).GetProperty("state").GetString());
        Assert.Empty((await GetRelationship(Id(relationship.Relationship))).GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_replacement_replay_converges_and_changed_retry_conflicts()
    {
        var transfer = await CreateTransfer("12.34");
        var replacementInflow = await Record(transfer.TargetAccountId, "12.34");
        var request = ReplaceTransferEnvelope(Id(transfer.Relationship), Id(transfer.Source), Id(replacementInflow), "Correct inflow", "same-key");

        var first = await Run(["ledger", "transfer", "replace", "--input", "-"], request);
        var replay = await Run(["ledger", "transfer", "replace", "--input", "-"], request);
        var crossKey = await Run(
            ["ledger", "transfer", "replace", "--input", "-"],
            ReplaceTransferEnvelope(Id(transfer.Relationship), Id(transfer.Source), Id(replacementInflow), "Correct inflow", "cross-key"));
        var changed = await Run(
            ["ledger", "transfer", "replace", "--input", "-"],
            ReplaceTransferEnvelope(Id(transfer.Relationship), Id(transfer.Source), Id(replacementInflow), "Changed reason", "same-key"));

        AssertSuccess(first, "ledger.transfer.replace");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Single((await GetRelationship(Id(transfer.Relationship))).GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_011_interrupted_replace_commits_old_or_complete_new_and_replay_converges()
    {
        var transfer = await CreateTransfer("20");
        var replacementInflow = await Record(transfer.TargetAccountId, "20");
        var request = ReplaceTransferEnvelope(
            Id(transfer.Relationship),
            Id(transfer.Source),
            Id(replacementInflow),
            "Crash-atomic replacement",
            "crash-replacement");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "transfer", "replace", "--input", "-"], request),
            "The published process completed before the interruption could be injected.");
        var interrupted = await GetRelationship(Id(transfer.Relationship));
        if (interrupted.GetProperty("state").GetString() == "active")
        {
            Assert.Empty(interrupted.GetProperty("history").EnumerateArray());
        }
        else
        {
            var history = Assert.Single(interrupted.GetProperty("history").EnumerateArray());
            Assert.Equal("replaced", history.GetProperty("action").GetString());
            Assert.Equal("active", (await GetRelationship(history.GetProperty("replacementRelationshipId").GetString()!)).GetProperty("state").GetString());
        }

        var converged = AssertSuccess(
            await Run(["ledger", "transfer", "replace", "--input", "-"], request),
            "ledger.transfer.replace");
        AssertReplacementChain(converged, "transfer", Id(transfer.Source), Id(replacementInflow), "Crash-atomic replacement");
        AssertTotals(await Actuals(), "20", "0", "0");
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

    private async Task<RelationshipFixture> CreateTransfer(string amount)
    {
        var sourceAccountId = await CreateAccount("Transfer source", "cheque");
        var targetAccountId = await CreateAccount("Transfer target", "savings");
        var source = await Record(sourceAccountId, "-" + amount);
        var target = await Record(targetAccountId, amount);
        var relationship = await Success(
            ["ledger", "transfer", "confirm", "--input", "-"],
            Envelope(new JsonObject
            {
                ["outflowTransactionId"] = Id(source),
                ["inflowTransactionId"] = Id(target),
                ["reason"] = "Owner confirmed transfer"
            }, Key("confirm-transfer")),
            "ledger.transfer.confirm");
        return new(sourceAccountId, targetAccountId, source, target, relationship);
    }

    private async Task<RelationshipFixture> CreateRefund(string amount)
    {
        var accountId = await CreateAccount("Refund account", "cheque");
        var source = await Record(accountId, "-" + amount);
        var target = await Record(accountId, amount);
        var relationship = await Success(
            ["ledger", "refund", "confirm", "--input", "-"],
            Envelope(new JsonObject
            {
                ["originalTransactionId"] = Id(source),
                ["refundTransactionId"] = Id(target),
                ["reason"] = "Owner confirmed full refund"
            }, Key("confirm-refund")),
            "ledger.refund.confirm");
        return new(accountId, accountId, source, target, relationship);
    }

    private async Task<string> CreateAccount(string name, string type)
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = name + " " + suffix,
                ["accountType"] = type,
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> Record(string accountId, string amount)
    {
        var token = Key("record");
        return await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmount"] = amount,
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = "2026-07-01",
                ["postingDate"] = null,
                ["originalDescription"] = "UC011 banking transaction",
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
            }, token),
            "ledger.transaction.record");
    }

    private async Task<JsonElement> GetRelationship(string relationshipId) => await Success(
        ["ledger", "relationship", "get", "--input", "-"],
        Envelope(new JsonObject { ["relationshipId"] = relationshipId, ["includeHistory"] = true }),
        "ledger.relationship.get");

    private async Task<JsonElement> Actuals() => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject { ["groupBy"] = "pool_category" },
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

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) =>
        AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc011-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string RevokeEnvelope(string relationshipId, string reason, string? key) => Envelope(
        new JsonObject { ["relationshipId"] = relationshipId, ["reason"] = reason },
        key);

    private static string ReplaceTransferEnvelope(string relationshipId, string outflowId, string inflowId, string reason, string key) => Envelope(
        new JsonObject
        {
            ["relationshipId"] = relationshipId,
            ["outflowTransactionId"] = outflowId,
            ["inflowTransactionId"] = inflowId,
            ["reason"] = reason
        },
        key);

    private static string ReplaceRefundEnvelope(string relationshipId, string originalId, string refundId, string reason, string key) => Envelope(
        new JsonObject
        {
            ["relationshipId"] = relationshipId,
            ["originalTransactionId"] = originalId,
            ["refundTransactionId"] = refundId,
            ["reason"] = reason
        },
        key);

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc011", ["runId"] = "published-e2e" },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static void AssertReplacementChain(JsonElement result, string type, string sourceId, string targetId, string reason)
    {
        var old = result.GetProperty("relationship");
        var replacement = result.GetProperty("replacementRelationship");
        Assert.Equal("retired", old.GetProperty("state").GetString());
        Assert.Equal("active", replacement.GetProperty("state").GetString());
        Assert.Equal(type, replacement.GetProperty("type").GetString());
        Assert.NotEqual(old.GetProperty("relationshipId").GetString(), replacement.GetProperty("relationshipId").GetString());
        Assert.Equal(sourceId, replacement.GetProperty("sourceTransactionId").GetString());
        Assert.Equal(targetId, replacement.GetProperty("targetTransactionId").GetString());
        var history = Assert.Single(old.GetProperty("history").EnumerateArray());
        Assert.Equal("replaced", history.GetProperty("action").GetString());
        Assert.Equal(reason, history.GetProperty("reason").GetString());
        Assert.Equal(replacement.GetProperty("relationshipId").GetString(), history.GetProperty("replacementRelationshipId").GetString());
        Assert.Empty(replacement.GetProperty("history").EnumerateArray());
    }

    private static void AssertTotals(JsonElement actuals, string movement, string spend, string budget)
    {
        var totals = actuals.GetProperty("totals");
        Assert.Equal(movement, totals.GetProperty("netAccountMovement").GetString());
        Assert.Equal(spend, totals.GetProperty("externalSpend").GetString());
        Assert.Equal(budget, totals.GetProperty("budgetActual").GetString());
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

    private static string Id(JsonElement value) => value.TryGetProperty("relationshipId", out var relationshipId)
        ? relationshipId.GetString()!
        : value.GetProperty("transactionId").GetString()!;

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record RelationshipFixture(
        string SourceAccountId,
        string TargetAccountId,
        JsonElement Source,
        JsonElement Target,
        JsonElement Relationship);
}
