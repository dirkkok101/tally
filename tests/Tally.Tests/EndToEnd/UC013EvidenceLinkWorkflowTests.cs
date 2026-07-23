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
public sealed class UC013EvidenceLinkWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc013-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_013_agent_discovers_generic_evidence_registration_link_and_read_contracts()
    {
        foreach (var (operationId, requestType, resultType, mutation) in new[]
                 {
                     ("ledger.evidence.register", "RegisterEvidenceInput", "EvidenceRecordDetail", true),
                     ("ledger.evidence.link-supporting", "LinkSupportingEvidenceInput", "EvidenceLinkResult", true),
                     ("ledger.evidence.get", "GetEvidenceInput", "EvidenceRecordDetail", false)
                 })
        {
            var operation = (await Success(
                ["schema", "show", operationId, "--input", "-"],
                Envelope(new JsonObject()),
                "system.schema.show")).GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(resultType, operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.Equal(mutation, operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_multiple_generic_records_link_safely_without_financial_or_reconciliation_change()
    {
        var accountId = await CreateAccount();
        var transaction = await Record(accountId);
        var beforeActuals = await Actuals();
        var before = await GetTransaction(TransactionId(transaction));
        var linkedIds = new List<string>();

        foreach (var kind in new[] { "receipt", "external_document", "owner_assertion" })
        {
            var evidence = await Register(kind);
            var linked = await Link(TransactionId(transaction), EvidenceId(evidence), "Additional owner-safe support", Key("link"));
            linkedIds.Add(EvidenceId(evidence));
            Assert.Equal(EvidenceId(evidence), linked.GetProperty("evidence").GetProperty("evidenceId").GetString());
            Assert.Equal(linked.GetProperty("linkEventId").GetString(), Assert.Single(linked.GetProperty("evidence").GetProperty("linkHistory").EnumerateArray()).GetProperty("linkEventId").GetString());
        }

        var after = await GetTransaction(TransactionId(transaction));
        Assert.Equal("recorded_unreconciled", before.GetProperty("reconciliationState").GetString());
        Assert.Equal("recorded_unreconciled", after.GetProperty("reconciliationState").GetString());
        Assert.Equal(4, after.GetProperty("evidence").GetArrayLength());
        Assert.Equal(0, await DurableTypeCount("reconciliation_decision"));
        Assert.Equal(0, await DurableTypeCount("reconciliation_decision_authority"));
        Assert.All(linkedIds, evidenceId => Assert.Contains(after.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == evidenceId));
        foreach (var evidenceId in linkedIds)
        {
            var detail = await GetEvidence(evidenceId);
            var history = Assert.Single(detail.GetProperty("linkHistory").EnumerateArray());
            Assert.Equal("supporting", history.GetProperty("role").GetString());
            Assert.Equal("link", history.GetProperty("action").GetString());
            Assert.Equal("automation:uc013:published-e2e", history.GetProperty("recordedBy").GetString());
            Assert.Equal(JsonValueKind.Null, history.GetProperty("decisionId").ValueKind);
        }
        AssertFinancialActualsEqual(beforeActuals, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_exact_allowlisted_observation_links_without_inference()
    {
        var accountId = await CreateAccount();
        var transaction = await Record(accountId, "2026-07-02");
        var evidence = await Register("external_document", new JsonObject
        {
            ["accountId"] = accountId,
            ["signedAmountMinor"] = -1234,
            ["currencyCode"] = "ZAR",
            ["transactionDate"] = "2026-07-01",
            ["postingDate"] = "2026-07-02",
            ["instrumentId"] = null,
            ["cardholderId"] = null,
            ["descriptionFingerprint"] = Digest("owner-safe-description")
        });

        var linked = await Link(TransactionId(transaction), EvidenceId(evidence), "Observed facts agree", Key("compatible"));

        Assert.Equal("recorded_unreconciled", linked.GetProperty("transaction").GetProperty("reconciliationState").GetString());
        Assert.Equal(evidence.GetProperty("observation").GetRawText(), linked.GetProperty("evidence").GetProperty("observation").GetRawText());
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_registration_exact_and_cross_key_replay_converge_while_changed_metadata_conflicts()
    {
        var token = Key("registration");
        var input = EvidenceInput("receipt", token, "receipt:" + token, Digest("content:" + token));
        var request = Envelope(input, "same-key");

        var first = await Run(["ledger", "evidence", "register", "--input", "-"], request);
        var replay = await Run(["ledger", "evidence", "register", "--input", "-"], request);
        var crossKey = await Run(["ledger", "evidence", "register", "--input", "-"], Envelope(input.DeepClone(), "cross-key"));
        var changed = input.DeepClone().AsObject();
        changed["contentFingerprint"] = Digest("changed:" + token);

        AssertSuccess(first, "ledger.evidence.register");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(await Run(["ledger", "evidence", "register", "--input", "-"], Envelope(changed, "same-key")), 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_link_exact_and_cross_key_replay_converge_while_changed_reason_conflicts()
    {
        var transaction = await Record(await CreateAccount());
        var evidence = await Register("receipt");
        var input = LinkInput(TransactionId(transaction), EvidenceId(evidence), "Receipt supplied");
        var request = Envelope(input, "same-key");

        var first = await Run(["ledger", "evidence", "link-supporting", "--input", "-"], request);
        var replay = await Run(["ledger", "evidence", "link-supporting", "--input", "-"], request);
        var crossKey = await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(input.DeepClone(), "cross-key"));

        AssertSuccess(first, "ledger.evidence.link-supporting");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(LinkInput(TransactionId(transaction), EvidenceId(evidence), "Changed reason"), "same-key")),
            5,
            "LEDGER-IDEMPOTENCY-001");
        Assert.Single((await GetEvidence(EvidenceId(evidence))).GetProperty("linkHistory").EnumerateArray());
    }

    [Theory]
    [InlineData("short-identity")]
    [InlineData("unsafe-reference")]
    [InlineData("raw-payload")]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_invalid_identity_or_payload_is_rejected_without_retention(string scenario)
    {
        var token = Key("privacy");
        JsonObject input;
        if (scenario == "raw-payload")
        {
            input = EvidenceInput("receipt", token, "receipt:" + token, null);
            input["rawPayload"] = "PRIVATE-CONTENT";
        }
        else
        {
            input = EvidenceInput(
                "receipt",
                token,
                scenario == "unsafe-reference" ? "bank:123456789" : "receipt:" + token,
                null);
            if (scenario == "short-identity") input["logicalIdentityDigest"] = "short";
        }

        var result = await Run(["ledger", "evidence", "register", "--input", "-"], Envelope(input, Key("invalid")));

        AssertError(result, 3, "validation.invalid_input");
        Assert.DoesNotContain("PRIVATE-CONTENT", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("transaction", "LEDGER-TRANSACTION-NOT-FOUND")]
    [InlineData("evidence", "LEDGER-EVIDENCE-LINK-EVIDENCE-NOT-FOUND")]
    public async Task UC_LEDGER_013_missing_transaction_or_evidence_returns_stable_not_found_without_link(string missing, string errorCode)
    {
        var transaction = await Record(await CreateAccount());
        var evidence = await Register("receipt");
        var transactionId = missing == "transaction" ? "01J00000000000000000000000" : TransactionId(transaction);
        var evidenceId = missing == "evidence" ? "01J00000000000000000000000" : EvidenceId(evidence);

        AssertError(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(LinkInput(transactionId, evidenceId, "Missing identity"), Key("missing"))),
            4,
            errorCode);
        Assert.Empty((await GetEvidence(EvidenceId(evidence))).GetProperty("linkHistory").EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_013_inactive_transaction_rejects_link_without_partial_effect()
    {
        var transaction = await Record(await CreateAccount());
        var evidence = await Register("receipt");
        await Success(
            ["ledger", "transaction", "void", "--input", "-"],
            Envelope(new JsonObject { ["transactionId"] = TransactionId(transaction), ["reason"] = "Owner voided transaction" }, Key("void")),
            "ledger.transaction.void");

        AssertError(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(LinkInput(TransactionId(transaction), EvidenceId(evidence), "Late receipt"), Key("inactive"))),
            6,
            "LEDGER-EVIDENCE-LINK-TRANSACTION-INACTIVE");
        Assert.Empty((await GetEvidence(EvidenceId(evidence))).GetProperty("linkHistory").EnumerateArray());
    }

    [Fact]
    public async Task UC_LEDGER_013_evidence_linked_to_first_transaction_cannot_move_to_second()
    {
        var accountId = await CreateAccount();
        var first = await Record(accountId);
        var second = await Record(accountId);
        var evidenceId = Assert.Single(first.GetProperty("evidence").EnumerateArray()).GetProperty("evidenceId").GetString()!;

        AssertError(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(LinkInput(TransactionId(second), evidenceId, "Move evidence"), Key("move"))),
            5,
            "LEDGER-EVIDENCE-LINK-CONFLICT");
        var history = Assert.Single((await GetEvidence(evidenceId)).GetProperty("linkHistory").EnumerateArray());
        Assert.Equal(TransactionId(first), history.GetProperty("transactionId").GetString());
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_statement_evidence_supporting_link_never_confirms_transaction()
    {
        var transaction = await Record(await CreateAccount());
        var evidence = await Register("statement_row");
        var linked = await Link(TransactionId(transaction), EvidenceId(evidence), "Statement candidate only", Key("statement-link"));
        var forbidden = LinkInput(TransactionId(transaction), EvidenceId(evidence), "Direct confirmation");
        forbidden["role"] = "confirming";

        Assert.Equal("supporting", Assert.Single(linked.GetProperty("evidence").GetProperty("linkHistory").EnumerateArray()).GetProperty("role").GetString());
        Assert.Equal("recorded_unreconciled", linked.GetProperty("transaction").GetProperty("reconciliationState").GetString());
        AssertError(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(forbidden, Key("forbidden-confirm"))),
            3,
            "validation.invalid_input");
    }

    [Fact]
    public async Task UC_LEDGER_013_interrupted_link_commits_none_or_one_and_same_request_converges()
    {
        var transaction = await Record(await CreateAccount());
        var evidence = await Register("receipt");
        var request = Envelope(LinkInput(TransactionId(transaction), EvidenceId(evidence), "Crash-atomic link"), "crash-link");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "evidence", "link-supporting", "--input", "-"], request),
            "The published process completed before the interruption could be injected.");
        Assert.InRange((await GetEvidence(EvidenceId(evidence))).GetProperty("linkHistory").GetArrayLength(), 0, 1);

        var converged = AssertSuccess(
            await Run(["ledger", "evidence", "link-supporting", "--input", "-"], request),
            "ledger.evidence.link-supporting");
        Assert.Equal(EvidenceId(evidence), converged.GetProperty("evidence").GetProperty("evidenceId").GetString());
        Assert.Single((await GetEvidence(EvidenceId(evidence))).GetProperty("linkHistory").EnumerateArray());
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

    private async Task<string> CreateAccount()
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC013 account " + suffix,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> Record(string accountId, string? postingDate = null)
    {
        var token = Key("record");
        return await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmount"] = "-12.34",
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = "2026-07-01",
                ["postingDate"] = postingDate,
                ["originalDescription"] = "Owner-safe purchase",
                ["instrumentId"] = null,
                ["cardholderId"] = null,
                ["initialEvidence"] = EvidenceInput("agent_capture", token, "capture:" + token, null)
            }, token),
            "ledger.transaction.record");
    }

    private async Task<JsonElement> Register(string kind, JsonObject? observation = null)
    {
        var token = Key("evidence");
        return await Success(
            ["ledger", "evidence", "register", "--input", "-"],
            Envelope(EvidenceInput(kind, token, "evidence:" + token, Digest("content:" + token), observation), token),
            "ledger.evidence.register");
    }

    private async Task<JsonElement> Link(string transactionId, string evidenceId, string reason, string key) => AssertSuccess(
        await Run(["ledger", "evidence", "link-supporting", "--input", "-"], Envelope(LinkInput(transactionId, evidenceId, reason), key)),
        "ledger.evidence.link-supporting");

    private async Task<JsonElement> GetEvidence(string evidenceId) => await Success(
        ["ledger", "evidence", "get", "--input", "-"],
        Envelope(new JsonObject { ["evidenceId"] = evidenceId }),
        "ledger.evidence.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> Actuals() => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject { ["groupBy"] = "pool_category" },
            ["pageSize"] = 100,
            ["cursor"] = null
        }),
        "ledger.actuals.query");

    private async Task<long> DurableTypeCount(string name)
    {
        var artifactRoot = Path.Combine(dataRoot, "verification-artifacts");
        Directory.CreateDirectory(artifactRoot);
        File.SetUnixFileMode(artifactRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(artifactRoot, Key("state") + ".tally-backup");
        var receipt = await Success(
            ["ledger", "backup", "create", "--input", "-"],
            Envelope(new JsonObject { ["targetPath"] = target }, Key("backup")),
            "ledger.backup.create");
        return receipt.GetProperty("manifest").GetProperty("types").EnumerateArray()
            .Single(type => type.GetProperty("name").GetString() == name)
            .GetProperty("rowCount").GetInt64();
    }

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

    private string Key(string purpose) => "uc013-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static JsonObject EvidenceInput(string kind, string identity, string? reference, string? contentFingerprint, JsonObject? observation = null) => new()
    {
        ["kind"] = kind,
        ["logicalIdentityDigest"] = identity.Length == 64 ? identity : Digest(identity),
        ["opaqueExternalReference"] = reference,
        ["contentFingerprint"] = contentFingerprint,
        ["observation"] = observation
    };

    private static JsonObject LinkInput(string transactionId, string evidenceId, string reason) => new()
    {
        ["transactionId"] = transactionId,
        ["evidenceId"] = evidenceId,
        ["reason"] = reason
    };

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc013", ["runId"] = "published-e2e" },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static void AssertFinancialActualsEqual(JsonElement before, JsonElement after)
    {
        Assert.Equal(before.GetProperty("totals").GetRawText(), after.GetProperty("totals").GetRawText());
        Assert.Equal(before.GetProperty("groups").GetRawText(), after.GetProperty("groups").GetRawText());
        var beforeContributions = before.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText());
        var afterContributions = after.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText());
        Assert.Equal(beforeContributions, afterContributions);
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

    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;
    private static string EvidenceId(JsonElement evidence) => evidence.GetProperty("evidenceId").GetString()!;
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
