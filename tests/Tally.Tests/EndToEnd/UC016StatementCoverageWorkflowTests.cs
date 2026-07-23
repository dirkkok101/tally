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
public sealed class UC016StatementCoverageWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc016-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_016_agent_discovers_the_closed_coverage_contract()
    {
        foreach (var (operationId, requestType, requiresKey) in new[]
                 {
                     ("ledger.reconciliation.coverage.complete", "CompleteStatementCoverageInput", true),
                     ("ledger.reconciliation.coverage.get", "GetStatementCoverageInput", false)
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
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_complete_scope_has_exact_one_class_membership()
    {
        var batch = await ComprehensiveBatch();

        var summary = await Complete(batch.Scope, Key("complete"));

        Assert.Equal(4, summary.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(6, summary.GetProperty("eligibleTransactionCount").GetInt32());
        Assert.Equal(10, summary.GetProperty("currentMembers").GetArrayLength());
        Assert.Equal(9, summary.GetProperty("history").GetArrayLength());
        var members = summary.GetProperty("currentMembers").EnumerateArray().ToArray();
        Assert.Equal(10, members.Select(MemberKey).Distinct(StringComparer.Ordinal).Count());

        AssertMember(members, "statement_row", batch.AmbiguousEvidenceId, "ambiguous");
        AssertMember(members, "statement_row", batch.ExceptionEvidenceId, "exception");
        AssertMember(members, "statement_row", batch.OwnerEvidenceId, "owner_confirmed_match");
        AssertMember(members, "statement_row", batch.CorrectionEvidenceId, "corrected_from_statement");

        AssertMember(members, "eligible_transaction", batch.OwnerSelectedTransactionId, "statement_reconciled");
        foreach (var absent in batch.AbsentTransactionIds)
        {
            var member = AssertMember(members, "eligible_transaction", absent, "recorded_absent_from_statement");
            Assert.Equal("not_found_in_completed_statement_scope", member.GetProperty("reason").GetString());
        }

        var queried = await GetCoverage(batch.Scope.ScopeId);
        Assert.Equal(summary.GetRawText(), queried.GetRawText());
        AssertCountsMatchMembers(summary);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_deterministic_match_scope_has_confirmed_membership()
    {
        var accountId = await CreateAccount("Confirmed exact");
        var transaction = await Record(accountId, "-10.00", "2026-07-01", "Confirmed candidate");
        var evidence = await StatementEvidence(accountId, -1000, "2026-07-01");
        var scope = await Scope(accountId, evidence);
        var statement = Statement(scope, evidence);
        await Apply(statement, await Project(statement), "match_existing", TransactionId(transaction), null, "Deterministic exact row", "deterministic_policy");

        var summary = await Complete(scope, Key("confirmed-coverage"));
        var members = summary.GetProperty("currentMembers").EnumerateArray().ToArray();
        Assert.Equal(1, summary.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("eligibleTransactionCount").GetInt32());
        AssertMember(members, "statement_row", evidence.EvidenceId, "confirmed_existing");
        AssertMember(members, "eligible_transaction", TransactionId(transaction), "statement_reconciled");
        AssertCountsMatchMembers(summary);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_statement_only_scope_has_exact_row_membership()
    {
        var accountId = await CreateAccount("Statement only");
        var evidence = await StatementEvidence(accountId, -2000, "2026-07-02");
        var scope = await Scope(accountId, evidence);
        var statement = Statement(scope, evidence);
        var projection = await Project(statement);
        Assert.Equal("no_candidate", projection.GetProperty("outcome").GetString());
        await ApplyStatementOnly(statement, projection, "-20.00");

        var summary = await Complete(scope, Key("statement-only-coverage"));

        Assert.Equal(1, summary.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("eligibleTransactionCount").GetInt32());
        var member = Assert.Single(summary.GetProperty("currentMembers").EnumerateArray());
        Assert.Equal("statement_row", member.GetProperty("kind").GetString());
        Assert.Equal(evidence.EvidenceId, member.GetProperty("stableId").GetString());
        Assert.Equal("statement_only", member.GetProperty("outcome").GetString());
        AssertCountsMatchMembers(summary);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_missing_row_outcome_cannot_create_false_absence()
    {
        var accountId = await CreateAccount("Missing outcome");
        var prior = await Record(accountId, "-12.34", "2026-07-10", "Potentially absent");
        var evidence = await StatementEvidence(accountId, -1234, "2026-07-10");
        var scope = await Scope(accountId, evidence);

        var result = await Run(
            ["ledger", "reconciliation", "coverage", "complete", "--input", "-"],
            Envelope(CoverageInput(scope), Key("missing-outcome")));

        AssertError(result, 8, "LEDGER-RECONCILIATION-COVERAGE-OUTCOME-MISSING");
        Assert.Equal("recorded_unreconciled", (await GetTransaction(TransactionId(prior))).GetProperty("reconciliationState").GetString());
        AssertError(
            await Run(["ledger", "reconciliation", "coverage", "get", "--input", "-"], Envelope(new JsonObject { ["scopeId"] = scope.ScopeId })),
            4,
            "LEDGER-RECONCILIATION-COVERAGE-NOT-FOUND");
    }

    [Theory]
    [InlineData("account", 5, "LEDGER-RECONCILIATION-COVERAGE-SCOPE-CONFLICT")]
    [InlineData("period", 5, "LEDGER-RECONCILIATION-COVERAGE-SCOPE-CONFLICT")]
    [InlineData("manifest", 5, "LEDGER-RECONCILIATION-COVERAGE-SCOPE-CONFLICT")]
    [InlineData("evidence", 5, "LEDGER-RECONCILIATION-COVERAGE-EVIDENCE-CHANGED")]
    [InlineData("policy", 7, "LEDGER-RECONCILIATION-COVERAGE-POLICY-UNSUPPORTED")]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_scope_contract_drift_is_rejected(string scenario, int exitCode, string errorCode)
    {
        var batch = await AmbiguousBatch();
        var input = CoverageInput(batch.Scope);
        if (scenario == "account") input["accountId"] = await CreateAccount("Changed account");
        if (scenario == "period") input["periodEnd"] = "2026-07-30";
        if (scenario == "manifest") input["manifestOpaqueReference"] = "statement:different";
        if (scenario == "evidence") input["expectedEvidenceIds"] = Array(Ulid("changed-evidence"));
        if (scenario == "policy") input["policyId"] = "unsupported-policy";

        var result = await Run(
            ["ledger", "reconciliation", "coverage", "complete", "--input", "-"],
            Envelope(input, Key("drift")));

        AssertError(result, exitCode, errorCode);
        AssertError(
            await Run(["ledger", "reconciliation", "coverage", "get", "--input", "-"], Envelope(new JsonObject { ["scopeId"] = batch.Scope.ScopeId })),
            4,
            "LEDGER-RECONCILIATION-COVERAGE-NOT-FOUND");
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_unknown_scope_is_rejected_without_summary()
    {
        var input = new JsonObject
        {
            ["scopeId"] = Ulid("unknown-scope"),
            ["accountId"] = Ulid("unknown-account"),
            ["periodStart"] = "2026-07-01",
            ["periodEnd"] = "2026-07-31",
            ["manifestOpaqueReference"] = "statement:unknown",
            ["expectedEvidenceIds"] = Array(Ulid("unknown-evidence")),
            ["policyId"] = "statement-coverage-v1",
            ["policyVersion"] = "1.0"
        };

        AssertError(
            await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], Envelope(input, Key("unknown"))),
            4,
            "LEDGER-RECONCILIATION-COVERAGE-SCOPE-NOT-FOUND");
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_replay_converges_and_changed_completion_conflicts()
    {
        var batch = await AmbiguousBatch();
        var input = CoverageInput(batch.Scope);
        var request = Envelope(input, "same-key");

        var first = await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], request);
        var replay = await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], request);
        var firstResult = AssertSuccess(first, "ledger.reconciliation.coverage.complete");
        var crossKey = await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], Envelope(input.DeepClone(), "cross-key"));
        var changed = input.DeepClone().AsObject();
        changed["manifestOpaqueReference"] = "statement:changed";
        var conflict = await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], Envelope(changed, "changed-key"));

        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(firstResult.GetProperty("completedAt").GetString(), AssertSuccess(crossKey, "ledger.reconciliation.coverage.complete").GetProperty("completedAt").GetString());
        AssertError(conflict, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(firstResult.GetRawText(), (await GetCoverage(batch.Scope.ScopeId)).GetRawText());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("payload")]
    [InlineData("duplicates")]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_invalid_completion_contract_is_atomic(string scenario)
    {
        var batch = await AmbiguousBatch();
        var input = CoverageInput(batch.Scope);
        if (scenario == "payload") input["rawStatement"] = "forbidden";
        if (scenario == "duplicates") input["expectedEvidenceIds"] = Array(batch.Scope.EvidenceIds[0], batch.Scope.EvidenceIds[0]);

        var result = await Run(
            ["ledger", "reconciliation", "coverage", "complete", "--input", "-"],
            Envelope(input, scenario == "key" ? null : Key("invalid")));

        AssertError(result, 3, "validation.invalid_input");
        AssertError(
            await Run(["ledger", "reconciliation", "coverage", "get", "--input", "-"], Envelope(new JsonObject { ["scopeId"] = batch.Scope.ScopeId })),
            4,
            "LEDGER-RECONCILIATION-COVERAGE-NOT-FOUND");
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_duplicate_transaction_outcome_is_prevented_before_completion()
    {
        var accountId = await CreateAccount("Duplicate prevention");
        var target = await Record(accountId, "-12.34", "2026-07-11", "Shared candidate");
        var firstEvidence = await StatementEvidence(accountId, -1234, "2026-07-11");
        var secondEvidence = await StatementEvidence(accountId, -1234, "2026-07-11");
        var scope = await Scope(accountId, firstEvidence, secondEvidence);
        var firstStatement = Statement(scope, firstEvidence);
        var secondStatement = Statement(scope, secondEvidence);
        await Apply(firstStatement, await Project(firstStatement), "match_existing", TransactionId(target), null, "Owner confirmed first row");
        var secondProjection = await Project(secondStatement);

        var second = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(secondStatement, secondProjection, "match_existing", TransactionId(target), null, "Owner attempted duplicate confirmation", Key("duplicate")));

        AssertError(second, 8, "LEDGER-RECONCILIATION-TARGET-NOT-CANDIDATE");
        AssertError(
            await Run(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], Envelope(CoverageInput(scope), Key("duplicate-coverage"))),
            8,
            "LEDGER-RECONCILIATION-COVERAGE-OUTCOME-MISSING");
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_later_owner_confirmation_changes_current_not_history()
    {
        var batch = await AmbiguousBatch();
        var initial = await Complete(batch.Scope, Key("complete"));
        var initialHistory = initial.GetProperty("history").GetRawText();
        var selected = batch.CandidateIds[0];

        await Success(
            ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = batch.EvidenceId,
                ["scopeId"] = batch.Scope.ScopeId,
                ["expectedDecisionId"] = batch.DecisionId,
                ["targetTransactionId"] = selected,
                ["authorityKind"] = "owner",
                ["reason"] = "Owner resolved covered ambiguity"
            }, Key("confirm")),
            "ledger.reconciliation.decision.confirm");

        var current = await GetCoverage(batch.Scope.ScopeId);
        Assert.Equal(initialHistory, current.GetProperty("history").GetRawText());
        AssertMember(current.GetProperty("currentMembers").EnumerateArray().ToArray(), "statement_row", batch.EvidenceId, "owner_confirmed_match");
        AssertMember(current.GetProperty("currentMembers").EnumerateArray().ToArray(), "eligible_transaction", selected, "statement_reconciled");
        AssertMember(current.GetProperty("currentMembers").EnumerateArray().ToArray(), "eligible_transaction", batch.CandidateIds[1], "recorded_absent_from_statement");
        AssertCountsMatchMembers(current);
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

    private async Task<ComprehensiveFixture> ComprehensiveBatch()
    {
        var accountId = await CreateAccount("Comprehensive");
        var ambiguousFirst = await Record(accountId, "-30.00", "2026-07-03", "Ambiguous first");
        var ambiguousSecond = await Record(accountId, "-30.00", "2026-07-03", "Ambiguous second");
        var ownerFirst = await Record(accountId, "-50.00", "2026-07-05", "Owner first");
        var ownerSecond = await Record(accountId, "-50.00", "2026-07-05", "Owner second");
        var correctionPrior = await Record(accountId, "-59.99", "2026-07-06", "Correction prior");
        var absentFirst = await Record(accountId, "-70.00", "2026-07-07", "Absent transaction");
        var absentSecond = await Record(accountId, "-80.00", "2026-07-08", "Second absent transaction");

        var ambiguousEvidence = await StatementEvidence(accountId, -3000, "2026-07-03");
        var exceptionEvidence = await StatementEvidence(accountId, -4000, "2026-07-04");
        var ownerEvidence = await StatementEvidence(accountId, -5000, "2026-07-05");
        var correctionEvidence = await StatementEvidence(accountId, -6000, "2026-07-06");
        var scope = await Scope(accountId, ambiguousEvidence, exceptionEvidence, ownerEvidence, correctionEvidence);

        var ambiguousStatement = Statement(scope, ambiguousEvidence);
        await Apply(ambiguousStatement, await Project(ambiguousStatement), "record_ambiguous", null, null, "Owner retained ambiguity");

        var exceptionStatement = Statement(scope, exceptionEvidence);
        await Apply(exceptionStatement, await Project(exceptionStatement), "record_exception", null, "OWNER-REVIEW", "Owner recorded exception");

        var ownerStatement = Statement(scope, ownerEvidence);
        var ownerAmbiguous = await Apply(ownerStatement, await Project(ownerStatement), "record_ambiguous", null, null, "Owner reviewed candidates");
        await Success(
            ["ledger", "reconciliation", "decision", "confirm", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = ownerEvidence.EvidenceId,
                ["scopeId"] = scope.ScopeId,
                ["expectedDecisionId"] = ownerAmbiguous.GetProperty("decisionId").GetString(),
                ["targetTransactionId"] = TransactionId(ownerFirst),
                ["authorityKind"] = "owner",
                ["reason"] = "Owner selected candidate"
            }, Key("owner-confirm")),
            "ledger.reconciliation.decision.confirm");

        var correctionStatement = Statement(scope, correctionEvidence);
        await ApplyCorrection(correctionStatement, await Project(correctionStatement), TransactionId(correctionPrior), "-60.00");

        return new(
            scope,
            ambiguousEvidence.EvidenceId,
            exceptionEvidence.EvidenceId,
            ownerEvidence.EvidenceId,
            correctionEvidence.EvidenceId,
            TransactionId(ownerFirst),
            [TransactionId(ambiguousFirst), TransactionId(ambiguousSecond), TransactionId(ownerSecond), TransactionId(absentFirst), TransactionId(absentSecond)]);
    }

    private async Task<AmbiguousFixture> AmbiguousBatch()
    {
        var accountId = await CreateAccount("Ambiguous");
        var first = await Record(accountId, "-12.34", "2026-07-10", "First candidate");
        var second = await Record(accountId, "-12.34", "2026-07-10", "Second candidate");
        var evidence = await StatementEvidence(accountId, -1234, "2026-07-10");
        var scope = await Scope(accountId, evidence);
        var statement = Statement(scope, evidence);
        var decision = await Apply(statement, await Project(statement), "record_ambiguous", null, null, "Owner retained ambiguity");
        return new(scope, evidence.EvidenceId, decision.GetProperty("decisionId").GetString()!, [TransactionId(first), TransactionId(second)]);
    }

    private async Task<string> CreateAccount(string label)
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC016 " + label + " " + suffix,
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
            Envelope(new JsonObject
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
            }, Key("record")),
            "ledger.transaction.record");
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
        return new(evidence.GetProperty("evidenceId").GetString()!, evidence.GetProperty("contentFingerprint").GetString()!, date);
    }

    private async Task<ScopeFixture> Scope(string accountId, params EvidenceFixture[] evidence)
    {
        var manifest = "statement:manifest:" + Key("manifest");
        var result = await Success(
            ["ledger", "reconciliation", "scope", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["periodStart"] = "2026-07-01",
                ["periodEnd"] = "2026-07-31",
                ["manifestOpaqueReference"] = manifest,
                ["evidenceIds"] = Array(evidence.Select(item => item.EvidenceId).ToArray())
            }, Key("scope")),
            "ledger.reconciliation.scope.register");
        return new(result.GetProperty("scopeId").GetString()!, accountId, manifest, evidence.Select(item => item.EvidenceId).Order(StringComparer.Ordinal).ToArray());
    }

    private static StatementFixture Statement(ScopeFixture scope, EvidenceFixture evidence) =>
        new(scope, evidence.EvidenceId, evidence.Fingerprint, evidence.Date);

    private async Task<JsonElement> Project(StatementFixture statement) => await Success(
        ["ledger", "reconciliation", "candidates", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["scopeId"] = statement.Scope.ScopeId,
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
        string reason,
        string authority = "owner") => await Success(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, projection, disposition, target, exceptionCode, reason, Key("apply"), authority),
            "ledger.reconciliation.apply");

    private static string ApplyEnvelope(
        StatementFixture statement,
        JsonElement projection,
        string disposition,
        string? target,
        string? exceptionCode,
        string reason,
        string key,
        string authority = "owner") => Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["evidenceFingerprint"] = statement.Fingerprint,
            ["scopeId"] = statement.Scope.ScopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = disposition,
            ["authorityKind"] = authority,
            ["reviewedCandidateIds"] = Array(CandidateIds(projection)),
            ["targetTransactionId"] = target,
            ["statementFact"] = null,
            ["exceptionCode"] = exceptionCode,
            ["reason"] = reason
        }, key);

    private async Task ApplyStatementOnly(StatementFixture statement, JsonElement projection, string amount) => _ = await Success(
        ["ledger", "reconciliation", "apply", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["evidenceFingerprint"] = statement.Fingerprint,
            ["scopeId"] = statement.Scope.ScopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "create_statement_only",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = Array(CandidateIds(projection)),
            ["targetTransactionId"] = null,
            ["statementFact"] = StatementFact(statement, amount),
            ["exceptionCode"] = null,
            ["reason"] = "Owner approved statement-only transaction"
        }, Key("statement-only")),
        "ledger.reconciliation.apply");

    private async Task ApplyCorrection(StatementFixture statement, JsonElement projection, string target, string amount) => _ = await Success(
        ["ledger", "reconciliation", "apply", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["evidenceFingerprint"] = statement.Fingerprint,
            ["scopeId"] = statement.Scope.ScopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "correct_existing_from_statement",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = Array(CandidateIds(projection)),
            ["targetTransactionId"] = target,
            ["statementFact"] = StatementFact(statement, amount),
            ["exceptionCode"] = null,
            ["reason"] = "Owner approved statement correction"
        }, Key("correction")),
        "ledger.reconciliation.apply");

    private static JsonObject StatementFact(StatementFixture statement, string amount) => new()
    {
        ["accountId"] = statement.Scope.AccountId,
        ["signedAmount"] = amount,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = statement.Date,
        ["postingDate"] = null,
        ["originalDescription"] = "Statement-authoritative banking transaction"
    };

    private async Task<JsonElement> Complete(ScopeFixture scope, string key) => await Success(
        ["ledger", "reconciliation", "coverage", "complete", "--input", "-"],
        Envelope(CoverageInput(scope), key),
        "ledger.reconciliation.coverage.complete");

    private static JsonObject CoverageInput(ScopeFixture scope) => new()
    {
        ["scopeId"] = scope.ScopeId,
        ["accountId"] = scope.AccountId,
        ["periodStart"] = "2026-07-01",
        ["periodEnd"] = "2026-07-31",
        ["manifestOpaqueReference"] = scope.Manifest,
        ["expectedEvidenceIds"] = Array(scope.EvidenceIds.ToArray()),
        ["policyId"] = "statement-coverage-v1",
        ["policyVersion"] = "1.0"
    };

    private async Task<JsonElement> GetCoverage(string scopeId) => await Success(
        ["ledger", "reconciliation", "coverage", "get", "--input", "-"],
        Envelope(new JsonObject { ["scopeId"] = scopeId }),
        "ledger.reconciliation.coverage.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc016-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;

    private static string[] CandidateIds(JsonElement projection) => projection.GetProperty("exactCandidates").EnumerateArray()
        .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
        .Select(candidate => candidate.GetProperty("transactionId").GetString()!)
        .ToArray();

    private static string MemberKey(JsonElement member) => member.GetProperty("kind").GetString() + ":" + member.GetProperty("stableId").GetString();

    private static JsonElement AssertMember(JsonElement[] members, string kind, string stableId, string outcome)
    {
        var member = Assert.Single(members, item => item.GetProperty("kind").GetString() == kind && item.GetProperty("stableId").GetString() == stableId);
        Assert.Equal(outcome, member.GetProperty("outcome").GetString());
        return member;
    }

    private static void AssertCountsMatchMembers(JsonElement summary)
    {
        var expected = summary.GetProperty("currentMembers").EnumerateArray()
            .GroupBy(MemberKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.All(expected.Values, count => Assert.Equal(1, count));
        var counts = summary.GetProperty("counts").EnumerateArray().Sum(item => item.GetProperty("count").GetInt32());
        Assert.Equal(summary.GetProperty("currentMembers").GetArrayLength(), counts);
    }

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Ulid(string seed)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "01J" + new string(bytes.Take(23).Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc016", ["runId"] = "published-e2e" },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string errorCode)
    {
        Assert.True(result.ExitCode == exitCode, $"Expected exit {exitCode} and {errorCode}; actual {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal("tally: " + errorCode, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record EvidenceFixture(string EvidenceId, string Fingerprint, string Date);
    private sealed record ScopeFixture(string ScopeId, string AccountId, string Manifest, IReadOnlyList<string> EvidenceIds);
    private sealed record StatementFixture(ScopeFixture Scope, string EvidenceId, string Fingerprint, string Date);
    private sealed record AmbiguousFixture(ScopeFixture Scope, string EvidenceId, string DecisionId, IReadOnlyList<string> CandidateIds);
    private sealed record ComprehensiveFixture(
        ScopeFixture Scope,
        string AmbiguousEvidenceId,
        string ExceptionEvidenceId,
        string OwnerEvidenceId,
        string CorrectionEvidenceId,
        string OwnerSelectedTransactionId,
        IReadOnlyList<string> AbsentTransactionIds);
}
