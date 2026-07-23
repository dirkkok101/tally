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
public sealed class UC015ReconciliationDecisionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc015-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_015_agent_discovers_the_closed_decision_contract()
    {
        foreach (var (operationId, requestType, requiresKey) in new[]
                 {
                     ("ledger.reconciliation.decision.get", "GetReconciliationDecisionInput", false),
                     ("ledger.reconciliation.decision.confirm", "ConfirmReconciliationDecisionInput", true),
                     ("ledger.reconciliation.decision.reject", "RejectReconciliationDecisionInput", true),
                     ("ledger.reconciliation.decision.revoke", "RevokeReconciliationDecisionInput", true),
                     ("ledger.reconciliation.decision.replace", "ReplaceReconciliationDecisionInput", true)
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
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_owner_confirms_one_ambiguous_candidate()
    {
        var seeded = await Ambiguous();
        var selected = seeded.CandidateIds[0];
        var other = seeded.CandidateIds[1];
        var before = await Actuals();

        var result = await Confirm(seeded, selected, "Owner selected candidate", Key("confirm"));
        var detail = await Decision(seeded.Statement.EvidenceId);

        Assert.Equal("owner_confirmed_match", result.GetProperty("currentState").GetString());
        Assert.Equal(selected, result.GetProperty("activeTransactionId").GetString());
        Assert.NotNull(result.GetProperty("linkEventId").GetString());
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());
        Assert.Equal(selected, detail.GetProperty("activeTransactionId").GetString());
        Assert.False(detail.GetProperty("requiresOwnerReview").GetBoolean());
        Assert.Equal("owner_confirmed_match", (await GetTransaction(selected)).GetProperty("reconciliationState").GetString());
        Assert.Equal("recorded_unreconciled", (await GetTransaction(other)).GetProperty("reconciliationState").GetString());
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_owner_rejects_all_without_link_or_financial_effect()
    {
        var seeded = await Ambiguous();
        var before = await Actuals();

        var result = await Reject(seeded, "Owner rejected every candidate", Key("reject"));
        var detail = await Decision(seeded.Statement.EvidenceId);

        Assert.Equal("rejected", result.GetProperty("currentState").GetString());
        Assert.Null(result.GetProperty("activeTransactionId").GetString());
        Assert.Null(result.GetProperty("linkEventId").GetString());
        Assert.Null(detail.GetProperty("activeConfirmingLinkEventId").GetString());
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());
        foreach (var candidate in seeded.CandidateIds)
        {
            Assert.Equal("recorded_unreconciled", (await GetTransaction(candidate)).GetProperty("reconciliationState").GetString());
        }
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_revoke_retires_link_and_preserves_history()
    {
        var confirmed = await Confirmed();
        var before = await Actuals();

        var result = await Revoke(confirmed, confirmed.Confirmation.GetProperty("decisionId").GetString()!, "Owner revoked confirmation", Key("revoke"));
        var detail = await Decision(confirmed.Seeded.Statement.EvidenceId);

        Assert.Equal("revoked", result.GetProperty("currentState").GetString());
        Assert.Equal(confirmed.TargetId, result.GetProperty("priorTransactionId").GetString());
        Assert.Null(result.GetProperty("activeTransactionId").GetString());
        Assert.Null(detail.GetProperty("activeConfirmingLinkEventId").GetString());
        Assert.Equal(3, detail.GetProperty("history").GetArrayLength());
        Assert.Contains(detail.GetProperty("history").EnumerateArray().SelectMany(item => item.GetProperty("links").EnumerateArray()), link => link.GetProperty("action").GetString() == "revoke");
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_replace_moves_only_the_active_link()
    {
        var confirmed = await Confirmed();
        var replacementId = confirmed.Seeded.CandidateIds.Single(id => id != confirmed.TargetId);
        var before = await Actuals();

        var result = await Replace(confirmed, confirmed.Confirmation.GetProperty("decisionId").GetString()!, replacementId, "Owner selected replacement", Key("replace"));
        var detail = await Decision(confirmed.Seeded.Statement.EvidenceId);

        Assert.Equal("replaced", result.GetProperty("currentState").GetString());
        Assert.Equal(confirmed.TargetId, result.GetProperty("priorTransactionId").GetString());
        Assert.Equal(replacementId, result.GetProperty("activeTransactionId").GetString());
        Assert.Equal(result.GetProperty("linkEventId").GetString(), detail.GetProperty("activeConfirmingLinkEventId").GetString());
        Assert.Equal(3, detail.GetProperty("history").GetArrayLength());
        var activeLinks = detail.GetProperty("history").EnumerateArray()
            .SelectMany(item => item.GetProperty("links").EnumerateArray())
            .Count(link => link.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, activeLinks);
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_owner_can_resolve_a_recorded_exception()
    {
        var accountId = await CreateAccount("Exception");
        var statement = await Statement(accountId, -1234, "2026-07-06");
        var projection = await Project(statement);
        var exception = await Apply(statement, projection, "record_exception", null, "OWNER-REVIEW", "Owner recorded unmatched exception");
        var candidate = await Record(accountId, "-12.34", "2026-07-06", "Later candidate");

        var result = await Confirm(
            new(statement, [TransactionId(candidate)], exception),
            TransactionId(candidate),
            "Owner resolved unmatched evidence",
            Key("resolve"));

        Assert.Equal(TransactionId(candidate), result.GetProperty("activeTransactionId").GetString());
        Assert.Equal("owner_confirmed_match", result.GetProperty("currentState").GetString());
        Assert.Equal(2, (await Decision(statement.EvidenceId)).GetProperty("history").GetArrayLength());
    }

    [Theory]
    [InlineData("missing-evidence", 4, "LEDGER-RECONCILIATION-DECISION-NOT-FOUND")]
    [InlineData("missing-candidate", 4, "LEDGER-RECONCILIATION-CANDIDATE-NOT-FOUND")]
    [InlineData("incompatible-account", 8, "LEDGER-RECONCILIATION-CANDIDATE-INCOMPATIBLE")]
    [InlineData("inactive", 6, "LEDGER-RECONCILIATION-CANDIDATE-INACTIVE")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_invalid_targets_do_not_change_history(string scenario, int exitCode, string errorCode)
    {
        if (scenario == "missing-evidence")
        {
            AssertError(
                await Run(["ledger", "reconciliation", "decision", "get", "--input", "-"], Envelope(new JsonObject { ["evidenceId"] = Ulid("missing-evidence") })),
                exitCode,
                errorCode);
            return;
        }

        SeededAmbiguous seeded;
        if (scenario == "missing-candidate")
        {
            var accountId = await CreateAccount("Missing candidate");
            var statement = await Statement(accountId, -1234, "2026-07-03");
            var exception = await Apply(statement, await Project(statement), "record_exception", null, "OWNER-REVIEW", "Owner recorded unmatched exception");
            seeded = new(statement, [], exception);
        }
        else
        {
            seeded = await Ambiguous();
        }
        string target;
        if (scenario == "missing-candidate") target = Ulid("missing-candidate");
        else if (scenario == "incompatible-account") target = TransactionId(await Record(await CreateAccount("Other"), "-12.34", "2026-07-03", "Other account"));
        else
        {
            target = seeded.CandidateIds[0];
            await Void(target, "Owner voided candidate", Key("void"));
        }
        var before = await Decision(seeded.Statement.EvidenceId);

        var result = await Run(
            ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
            Envelope(ConfirmInput(seeded, target, "Invalid candidate"), Key("invalid")));

        AssertError(result, exitCode, errorCode);
        Assert.Equal(before.GetRawText(), (await Decision(seeded.Statement.EvidenceId)).GetRawText());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_other_confirmation_conflict_preserves_first_link()
    {
        var accountId = await CreateAccount("Other confirmation");
        var first = await Record(accountId, "-12.34", "2026-07-03", "First candidate");
        var second = await Record(accountId, "-12.34", "2026-07-03", "Second candidate");
        var firstEvidence = await StatementEvidence(accountId, -1234, "2026-07-03");
        var otherEvidence = await StatementEvidence(accountId, -1234, "2026-07-03");
        var scopeId = await Scope(accountId, firstEvidence.EvidenceId, otherEvidence.EvidenceId);
        var firstStatement = new StatementFixture(accountId, firstEvidence.EvidenceId, firstEvidence.Fingerprint, scopeId, "2026-07-03");
        var otherStatement = new StatementFixture(accountId, otherEvidence.EvidenceId, otherEvidence.Fingerprint, scopeId, "2026-07-03");
        var firstProjection = await Project(firstStatement);
        var ambiguous = await Apply(firstStatement, firstProjection, "record_ambiguous", null, null, "Owner reviewed ambiguous candidates");
        var seeded = new SeededAmbiguous(firstStatement, [TransactionId(first), TransactionId(second)], ambiguous);
        var target = seeded.CandidateIds[0];
        var otherProjection = await Project(otherStatement);
        await Apply(otherStatement, otherProjection, "match_existing", target, null, "Owner confirmed other evidence");
        var before = await Decision(seeded.Statement.EvidenceId);

        var result = await Run(
            ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
            Envelope(ConfirmInput(seeded, target, "Conflicting confirmation"), Key("other-link")));

        AssertError(result, 5, "LEDGER-RECONCILIATION-CANDIDATE-ALREADY-RECONCILED");
        Assert.Equal(before.GetRawText(), (await Decision(seeded.Statement.EvidenceId)).GetRawText());
        Assert.Equal(target, (await Decision(otherStatement.EvidenceId)).GetProperty("activeTransactionId").GetString());
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("replace")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_stale_predecessor_is_rejected_atomically(string action)
    {
        var confirmed = await Confirmed();
        var stale = Ulid("stale-predecessor");
        var before = await Decision(confirmed.Seeded.Statement.EvidenceId);
        PublishedTallyResult result;
        if (action == "revoke")
        {
            result = await Run(
                ["ledger", "reconciliation", "decision", "revoke", "--input", "-"],
                Envelope(RevokeInput(confirmed.Seeded.Statement.EvidenceId, stale, "Stale revoke"), Key("stale-revoke")));
        }
        else
        {
            var replacement = confirmed.Seeded.CandidateIds.Single(id => id != confirmed.TargetId);
            result = await Run(
                ["ledger", "reconciliation", "decision", "replace", "--input", "-"],
                Envelope(ReplaceInput(confirmed, stale, replacement, "Stale replace"), Key("stale-replace")));
        }

        AssertError(result, 5, "LEDGER-RECONCILIATION-DECISION-STALE");
        Assert.Equal(before.GetRawText(), (await Decision(confirmed.Seeded.Statement.EvidenceId)).GetRawText());
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("authority")]
    [InlineData("key")]
    [InlineData("rawPayload")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_requires_explicit_private_owner_intent(string scenario)
    {
        var seeded = await Ambiguous();
        var input = ConfirmInput(seeded, seeded.CandidateIds[0], scenario == "reason" ? "" : "Explicit owner intent");
        if (scenario == "authority") input["authorityKind"] = "deterministic_policy";
        if (scenario == "rawPayload") input["rawPayload"] = "forbidden";

        var result = await Run(
            ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
            Envelope(input, scenario == "key" ? null : Key("intent")));

        AssertError(result, 3, "validation.invalid_input");
        Assert.Single((await Decision(seeded.Statement.EvidenceId)).GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_exact_replay_converges_and_changed_request_conflicts()
    {
        var seeded = await Ambiguous();
        var input = ConfirmInput(seeded, seeded.CandidateIds[0], "Owner replay confirmation");
        var request = Envelope(input, "same-key");

        var first = await Run(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], request);
        var replay = await Run(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], request);
        var crossKey = await Run(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], Envelope(input.DeepClone(), "cross-key"));
        var changedInput = input.DeepClone().AsObject();
        changedInput["reason"] = "Changed reason";
        var changed = await Run(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], Envelope(changedInput, "same-key"));

        var firstResult = AssertSuccess(first, "ledger.reconciliation.decision.confirm");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(firstResult.GetProperty("decisionId").GetString(), AssertSuccess(crossKey, "ledger.reconciliation.decision.confirm").GetProperty("decisionId").GetString());
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(2, (await Decision(seeded.Statement.EvidenceId)).GetProperty("history").GetArrayLength());
    }

    [Theory]
    [InlineData("void")]
    [InlineData("supersede")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_inactive_target_derives_exception_without_moving_evidence(string action)
    {
        var confirmed = await Confirmed();
        var activeLink = confirmed.Confirmation.GetProperty("linkEventId").GetString();
        if (action == "void") await Void(confirmed.TargetId, "Owner voided confirmed target", Key("terminate"));
        else await Supersede(confirmed.TargetId, confirmed.Seeded.Statement.AccountId, "Owner superseded confirmed target", Key("terminate"));

        var detail = await Decision(confirmed.Seeded.Statement.EvidenceId);

        Assert.Equal("reconciliation_exception", detail.GetProperty("currentState").GetString());
        Assert.True(detail.GetProperty("requiresOwnerReview").GetBoolean());
        Assert.Equal(activeLink, detail.GetProperty("activeConfirmingLinkEventId").GetString());
        Assert.Equal(confirmed.TargetId, detail.GetProperty("activeTransactionId").GetString());
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_interrupted_confirmation_commits_none_or_one_and_retry_converges()
    {
        var seeded = await Ambiguous();
        var target = seeded.CandidateIds[0];
        var request = Envelope(ConfirmInput(seeded, target, "Crash-atomic owner confirmation"), "crash-confirm");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], request),
            "The published process completed before interruption.");
        Assert.InRange((await Decision(seeded.Statement.EvidenceId)).GetProperty("history").GetArrayLength(), 1, 2);

        var converged = AssertSuccess(
            await Run(["ledger", "reconciliation", "decision", "confirm", "--input", "-"], request),
            "ledger.reconciliation.decision.confirm");
        Assert.Equal(target, converged.GetProperty("activeTransactionId").GetString());
        var detail = await Decision(seeded.Statement.EvidenceId);
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());
        Assert.Equal(target, detail.GetProperty("activeTransactionId").GetString());
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

    private async Task<SeededAmbiguous> Ambiguous()
    {
        var accountId = await CreateAccount("Ambiguous");
        var first = await Record(accountId, "-12.34", "2026-07-03", "First candidate");
        var second = await Record(accountId, "-12.34", "2026-07-03", "Second candidate");
        var statement = await Statement(accountId, -1234, "2026-07-03");
        var projection = await Project(statement);
        var ambiguous = await Apply(statement, projection, "record_ambiguous", null, null, "Owner reviewed ambiguous candidates");
        return new(statement, [TransactionId(first), TransactionId(second)], ambiguous);
    }

    private async Task<SeededConfirmed> Confirmed()
    {
        var seeded = await Ambiguous();
        var target = seeded.CandidateIds[0];
        var confirmation = await Confirm(seeded, target, "Owner confirmed candidate", Key("seed-confirm"));
        return new(seeded, target, confirmation);
    }

    private async Task<string> CreateAccount(string label)
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC015 " + label + " " + suffix,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> Record(string accountId, string amount, string date, string description)
    {
        var token = Key("capture");
        return await Success(
            ["ledger", "transaction", "record", "--input", "-"],
            Envelope(TransactionFact(accountId, amount, date, description, token), Key("record")),
            "ledger.transaction.record");
    }

    private static JsonObject TransactionFact(string accountId, string amount, string date, string description, string token) => new()
    {
        ["accountId"] = accountId,
        ["signedAmount"] = amount,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = date,
        ["postingDate"] = null,
        ["originalDescription"] = description,
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
    };

    private async Task<StatementFixture> Statement(string accountId, long amountMinor, string date)
    {
        var evidence = await StatementEvidence(accountId, amountMinor, date);
        var scopeId = await Scope(accountId, evidence.EvidenceId);
        return new(accountId, evidence.EvidenceId, evidence.Fingerprint, scopeId, date);
    }

    private async Task<EvidenceFixture> StatementEvidence(string accountId, long amountMinor, string date)
    {
        var token = Key("statement");
        var evidence = await Success(
            ["ledger", "evidence", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["kind"] = "statement_row",
                ["logicalIdentityDigest"] = Digest("identity:" + token),
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
            }, Key("evidence")),
            "ledger.evidence.register");
        return new(evidence.GetProperty("evidenceId").GetString()!, evidence.GetProperty("contentFingerprint").GetString()!);
    }

    private async Task<string> Scope(string accountId, params string[] evidenceIds) => (await Success(
            ["ledger", "reconciliation", "scope", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["periodStart"] = "2026-07-01",
                ["periodEnd"] = "2026-07-31",
                ["manifestOpaqueReference"] = "statement:manifest:" + Key("manifest"),
                ["evidenceIds"] = Array(evidenceIds)
            }, Key("scope")),
            "ledger.reconciliation.scope.register")).GetProperty("scopeId").GetString()!;

    private async Task<JsonElement> Project(StatementFixture statement) => await Success(
        ["ledger", "reconciliation", "candidates", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["scopeId"] = statement.ScopeId,
            ["policyId"] = "manual_review_projection",
            ["policyVersion"] = "1.0"
        }),
        "ledger.reconciliation.candidates");

    private async Task<JsonElement> Apply(
        StatementFixture statement,
        JsonElement projection,
        string disposition,
        string? target,
        string? exceptionCode,
        string reason) => await Success(
        ["ledger", "reconciliation", "apply", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["evidenceFingerprint"] = statement.Fingerprint,
            ["scopeId"] = statement.ScopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = disposition,
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = Array(CandidateIds(projection)),
            ["targetTransactionId"] = target,
            ["statementFact"] = null,
            ["exceptionCode"] = exceptionCode,
            ["reason"] = reason
        }, Key("apply")),
        "ledger.reconciliation.apply");

    private async Task<JsonElement> Confirm(SeededAmbiguous seeded, string target, string reason, string key) => await Success(
        ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
        Envelope(ConfirmInput(seeded, target, reason), key),
        "ledger.reconciliation.decision.confirm");

    private static JsonObject ConfirmInput(SeededAmbiguous seeded, string target, string reason) => new()
    {
        ["evidenceId"] = seeded.Statement.EvidenceId,
        ["scopeId"] = seeded.Statement.ScopeId,
        ["expectedDecisionId"] = seeded.InitialDecision.GetProperty("decisionId").GetString(),
        ["targetTransactionId"] = target,
        ["authorityKind"] = "owner",
        ["reason"] = reason
    };

    private async Task<JsonElement> Reject(SeededAmbiguous seeded, string reason, string key) => await Success(
        ["ledger", "reconciliation", "decision", "reject", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = seeded.Statement.EvidenceId,
            ["scopeId"] = seeded.Statement.ScopeId,
            ["expectedDecisionId"] = seeded.InitialDecision.GetProperty("decisionId").GetString(),
            ["authorityKind"] = "owner",
            ["reason"] = reason
        }, key),
        "ledger.reconciliation.decision.reject");

    private async Task<JsonElement> Revoke(SeededConfirmed confirmed, string predecessor, string reason, string key) => await Success(
        ["ledger", "reconciliation", "decision", "revoke", "--input", "-"],
        Envelope(RevokeInput(confirmed.Seeded.Statement.EvidenceId, predecessor, reason), key),
        "ledger.reconciliation.decision.revoke");

    private static JsonObject RevokeInput(string evidenceId, string predecessor, string reason) => new()
    {
        ["evidenceId"] = evidenceId,
        ["expectedDecisionId"] = predecessor,
        ["authorityKind"] = "owner",
        ["reason"] = reason
    };

    private async Task<JsonElement> Replace(SeededConfirmed confirmed, string predecessor, string target, string reason, string key) => await Success(
        ["ledger", "reconciliation", "decision", "replace", "--input", "-"],
        Envelope(ReplaceInput(confirmed, predecessor, target, reason), key),
        "ledger.reconciliation.decision.replace");

    private static JsonObject ReplaceInput(SeededConfirmed confirmed, string predecessor, string target, string reason) => new()
    {
        ["evidenceId"] = confirmed.Seeded.Statement.EvidenceId,
        ["scopeId"] = confirmed.Seeded.Statement.ScopeId,
        ["expectedDecisionId"] = predecessor,
        ["targetTransactionId"] = target,
        ["authorityKind"] = "owner",
        ["reason"] = reason
    };

    private async Task<JsonElement> Decision(string evidenceId) => await Success(
        ["ledger", "reconciliation", "decision", "get", "--input", "-"],
        Envelope(new JsonObject { ["evidenceId"] = evidenceId }),
        "ledger.reconciliation.decision.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task Void(string transactionId, string reason, string key) => _ = await Success(
        ["ledger", "transaction", "void", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["reason"] = reason }, key),
        "ledger.transaction.void");

    private async Task Supersede(string transactionId, string accountId, string reason, string key)
    {
        var token = Key("replacement");
        _ = await Success(
            ["ledger", "transaction", "supersede", "--input", "-"],
            Envelope(new JsonObject
            {
                ["transactionId"] = transactionId,
                ["replacement"] = TransactionFact(accountId, "-12.35", "2026-07-03", "Replacement transaction", token),
                ["reason"] = reason
            }, key),
            "ledger.transaction.supersede");
    }

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

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc015-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;

    private static string[] CandidateIds(JsonElement projection) => projection.GetProperty("exactCandidates").EnumerateArray()
        .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
        .Select(candidate => candidate.GetProperty("transactionId").GetString()!)
        .ToArray();

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Ulid(string seed)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "01J" + new string(bytes.Take(23).Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static void AssertFinancialActualsEqual(JsonElement before, JsonElement after)
    {
        Assert.Equal(before.GetProperty("totals").GetRawText(), after.GetProperty("totals").GetRawText());
        Assert.Equal(before.GetProperty("groups").GetRawText(), after.GetProperty("groups").GetRawText());
        var beforeContributions = before.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText());
        var afterContributions = after.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("contribution").GetRawText());
        Assert.Equal(beforeContributions, afterContributions);
    }

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc015", ["runId"] = "published-e2e" },
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

    private sealed record StatementFixture(string AccountId, string EvidenceId, string Fingerprint, string ScopeId, string Date);
    private sealed record EvidenceFixture(string EvidenceId, string Fingerprint);
    private sealed record SeededAmbiguous(StatementFixture Statement, IReadOnlyList<string> CandidateIds, JsonElement InitialDecision);
    private sealed record SeededConfirmed(SeededAmbiguous Seeded, string TargetId, JsonElement Confirmation);
}
