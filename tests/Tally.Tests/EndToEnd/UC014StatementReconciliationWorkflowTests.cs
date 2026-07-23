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
public sealed class UC014StatementReconciliationWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc014-" + Guid.NewGuid().ToString("N"));
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_014_agent_discovers_the_closed_reconciliation_contract()
    {
        foreach (var (operationId, requestType, requiresKey) in new[]
                 {
                     ("ledger.reconciliation.scope.register", "RegisterReconciliationScopeInput", true),
                     ("ledger.reconciliation.candidates", "GetReconciliationCandidatesInput", false),
                     ("ledger.reconciliation.apply", "ReconciliationApplyInput", true),
                     ("ledger.reconciliation.decision.get", "GetReconciliationDecisionInput", false)
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
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_exact_unique_row_confirms_existing_without_creation()
    {
        var accountId = await CreateAccount("Exact");
        var transaction = await Record(accountId, "-12.34", "2026-07-03", "Notification fact");
        var statement = await Statement(accountId, -1234, "2026-07-03");
        var projection = await Project(statement);
        var before = await Actuals(accountId);

        var applied = await Apply(
            statement,
            projection,
            "match_existing",
            "deterministic_policy",
            TransactionId(transaction),
            null,
            null,
            "exact_unique_candidate_v1");

        Assert.False(applied.GetProperty("createdStatementOnly").GetBoolean());
        Assert.Equal(TransactionId(transaction), applied.GetProperty("activeTransactionId").GetString());
        Assert.Equal("deterministic_policy", applied.GetProperty("authorityKind").GetString());
        Assert.Equal("reconciliation-policy-v1", applied.GetProperty("policyId").GetString());
        var current = await GetTransaction(TransactionId(transaction));
        Assert.Equal("statement_reconciled", current.GetProperty("reconciliationState").GetString());
        Assert.Contains(current.GetProperty("evidence").EnumerateArray(), item =>
            item.GetProperty("evidenceId").GetString() == statement.EvidenceId
            && item.GetProperty("role").GetString() == "confirming");
        AssertFinancialActualsEqual(before, await Actuals(accountId));
        var history = await Decision(statement.EvidenceId);
        var decision = Assert.Single(history.GetProperty("history").EnumerateArray());
        Assert.Equal("confirmed_existing", decision.GetProperty("disposition").GetString());
        Assert.Equal("deterministic_policy", decision.GetProperty("authorityKind").GetString());
        Assert.Equal(
            "account=exact;currency=ZAR;signed_amount_minor=exact;effective_date=exact;tolerance_days=0;exact_candidates=1;guard_candidates=0",
            decision.GetProperty("matchBasis").GetString());
        Assert.Single(decision.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_zero_candidates_create_one_statement_only_transaction()
    {
        var accountId = await CreateAccount("Statement only");
        var statement = await Statement(accountId, -4567, "2026-07-04");
        var projection = await Project(statement);
        Assert.Equal("no_candidate", projection.GetProperty("outcome").GetString());

        var applied = await Apply(
            statement,
            projection,
            "create_statement_only",
            "owner",
            null,
            StatementFact(accountId, "-45.67", "2026-07-04"),
            null,
            "Owner approved missing statement transaction");

        Assert.True(applied.GetProperty("createdStatementOnly").GetBoolean());
        var transactionId = applied.GetProperty("activeTransactionId").GetString()!;
        var transaction = await GetTransaction(transactionId);
        Assert.Equal("statement_only", transaction.GetProperty("reconciliationState").GetString());
        Assert.Equal("unknown", transaction.GetProperty("paymentAttribution").GetProperty("instrumentState").GetString());
        Assert.Equal("unknown", transaction.GetProperty("paymentAttribution").GetProperty("cardholderState").GetString());
        Assert.Equal("unassigned", transaction.GetProperty("pool").GetProperty("state").GetString());
        Assert.Equal("-45.67", transaction.GetProperty("signedAmount").GetString());
        Assert.Equal(transactionId, Assert.Single((await Actuals(accountId)).GetProperty("items").EnumerateArray()).GetProperty("transactionId").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_multiple_candidates_remain_ambiguous_without_financial_effect()
    {
        var accountId = await CreateAccount("Ambiguous");
        await Record(accountId, "-12.34", "2026-07-05", "First notification");
        await Record(accountId, "-12.34", "2026-07-05", "Second notification");
        var statement = await Statement(accountId, -1234, "2026-07-05");
        var projection = await Project(statement);
        var before = await Actuals(accountId);
        Assert.Equal("ambiguous", projection.GetProperty("outcome").GetString());

        var applied = await Apply(statement, projection, "record_ambiguous", "owner", null, null, null, "Owner review required");

        Assert.Null(applied.GetProperty("activeTransactionId").GetString());
        Assert.Null(applied.GetProperty("confirmingLinkEventId").GetString());
        Assert.NotNull(applied.GetProperty("exceptionId").GetString());
        Assert.Equal(2, applied.GetProperty("reviewedCandidateIds").GetArrayLength());
        AssertFinancialActualsEqual(before, await Actuals(accountId));
        var decision = Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray());
        Assert.Equal("ambiguous", decision.GetProperty("disposition").GetString());
        Assert.Empty(decision.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_owner_can_confirm_a_guard_candidate_explicitly()
    {
        var accountId = await CreateAccount("Guard review");
        var transaction = await Record(accountId, "-12.30", "2026-07-06", "Notification fact");
        var statement = await Statement(accountId, -1234, "2026-07-06");
        var projection = await Project(statement);
        Assert.Equal("guard_only", projection.GetProperty("outcome").GetString());

        var applied = await Apply(statement, projection, "match_existing", "owner", TransactionId(transaction), null, null, "Owner accepted guard candidate");

        Assert.Equal(TransactionId(transaction), applied.GetProperty("activeTransactionId").GetString());
        Assert.Equal("owner_confirmed_match", (await GetTransaction(TransactionId(transaction))).GetProperty("reconciliationState").GetString());
        Assert.Equal("owner", Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray()).GetProperty("authorityKind").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_authoritative_correction_carries_dimensions_and_keeps_one_active_effect()
    {
        var accountId = await CreateAccount("Correction");
        var instrumentId = await CreateInstrument(accountId);
        var cardholderId = await CreateCardholder();
        var categoryId = await CreateCategory();
        var poolId = await CreatePool();
        var prior = await Record(accountId, "-12.30", "2026-07-07", "Provisional notification", instrumentId, cardholderId);
        await AssignCategory(TransactionId(prior), categoryId);
        await AssignPool(prior, poolId);
        var statement = await Statement(accountId, -1234, "2026-07-07");
        var projection = await Project(statement);

        var applied = await Apply(statement, projection, "correct_existing_from_statement", "owner", TransactionId(prior), StatementFact(accountId, "-12.34", "2026-07-07"), null, "Owner approved statement authority");
        var replacementId = applied.GetProperty("activeTransactionId").GetString()!;
        var correction = applied.GetProperty("correction");
        var historical = await GetTransaction(TransactionId(prior));
        var replacement = await GetTransaction(replacementId);

        Assert.Equal(TransactionId(prior), correction.GetProperty("priorTransactionId").GetString());
        Assert.Equal(replacementId, correction.GetProperty("replacementTransactionId").GetString());
        Assert.Equal("superseded", historical.GetProperty("lifecycleStatus").GetString());
        Assert.Equal(replacementId, historical.GetProperty("activeReplacementTransactionId").GetString());
        Assert.Equal("active", replacement.GetProperty("lifecycleStatus").GetString());
        Assert.Equal("statement_reconciled", replacement.GetProperty("reconciliationState").GetString());
        Assert.Equal(categoryId, replacement.GetProperty("category").GetProperty("categoryId").GetString());
        Assert.Equal(poolId, replacement.GetProperty("pool").GetProperty("poolId").GetString());
        Assert.Equal(instrumentId, replacement.GetProperty("paymentAttribution").GetProperty("instrumentId").GetString());
        Assert.Equal(cardholderId, replacement.GetProperty("paymentAttribution").GetProperty("cardholderId").GetString());
        AssertCarryForward(replacement.GetProperty("history").GetProperty("categoryAssignments"), TransactionId(prior), applied.GetProperty("decisionId").GetString());
        AssertCarryForward(replacement.GetProperty("history").GetProperty("poolAssignments"), TransactionId(prior), applied.GetProperty("decisionId").GetString());
        AssertCarryForward(replacement.GetProperty("history").GetProperty("paymentAttribution"), TransactionId(prior), applied.GetProperty("decisionId").GetString());
        var item = Assert.Single((await Actuals(accountId)).GetProperty("items").EnumerateArray());
        Assert.Equal(replacementId, item.GetProperty("transactionId").GetString());
        Assert.Equal("-12.34", item.GetProperty("contribution").GetProperty("netAccountMovement").GetString());
        var decision = Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray());
        Assert.Equal(TransactionId(prior), decision.GetProperty("priorTransactionId").GetString());
        Assert.Equal(replacementId, decision.GetProperty("activeTransactionId").GetString());
        Assert.Equal($"scope:{statement.ScopeId}|evidence:{statement.Fingerprint}", decision.GetProperty("statementAuthorityBasis").GetString());
        Assert.NotNull(decision.GetProperty("carryForward").GetProperty("correctionId").GetString());
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_compatible_relationship_is_replaced_explicitly(string type)
    {
        var accountId = await CreateAccount("Relationship " + type);
        var counterpartAccountId = type == "transfer" ? await CreateAccount("Relationship counterpart") : accountId;
        var prior = await Record(accountId, "-12.34", "2026-07-08", "Relationship source");
        var counterpart = await Record(counterpartAccountId, "12.34", "2026-07-08", "Relationship target");
        var relationship = await ConfirmRelationship(type, TransactionId(prior), TransactionId(counterpart));
        var statement = await Statement(accountId, -1234, "2026-07-08");

        var applied = await Apply(statement, await Project(statement), "correct_existing_from_statement", "owner", TransactionId(prior), StatementFact(accountId, "-12.34", "2026-07-08"), null, "Owner approved relationship-safe correction");

        Assert.Single(applied.GetProperty("correction").GetProperty("relationshipLifecycleEventIds").EnumerateArray());
        var retired = await GetRelationship(relationship.GetProperty("relationshipId").GetString()!);
        Assert.Equal("retired", retired.GetProperty("state").GetString());
        var lifecycle = Assert.Single(retired.GetProperty("history").EnumerateArray());
        var replacement = await GetRelationship(lifecycle.GetProperty("replacementRelationshipId").GetString()!);
        Assert.Equal("active", replacement.GetProperty("state").GetString());
        Assert.Equal(applied.GetProperty("activeTransactionId").GetString(), replacement.GetProperty("sourceTransactionId").GetString());
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("refund")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_incompatible_relationship_requires_review_without_mutation(string type)
    {
        var accountId = await CreateAccount("Invalid relationship " + type);
        var counterpartAccountId = type == "transfer" ? await CreateAccount("Invalid counterpart") : accountId;
        var prior = await Record(accountId, "-12.34", "2026-07-09", "Relationship source");
        var counterpart = await Record(counterpartAccountId, "12.34", "2026-07-09", "Relationship target");
        var relationship = await ConfirmRelationship(type, TransactionId(prior), TransactionId(counterpart));
        var statement = await Statement(accountId, -1300, "2026-07-09");
        var before = await Actuals();

        var result = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, await Project(statement), "correct_existing_from_statement", "owner", TransactionId(prior), StatementFact(accountId, "-13.00", "2026-07-09"), null, "Owner proposed invalid relationship correction", Key("review")));

        AssertError(result, 8, "operation.review_required");
        Assert.Equal("active", (await GetTransaction(TransactionId(prior))).GetProperty("lifecycleStatus").GetString());
        Assert.Equal("active", (await GetRelationship(relationship.GetProperty("relationshipId").GetString()!)).GetProperty("state").GetString());
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("multiple")]
    [InlineData("correction")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_unsupported_automatic_policy_paths_require_review(string scenario)
    {
        var accountId = await CreateAccount("Automatic " + scenario);
        var first = await Record(accountId, scenario == "multiple" ? "-12.34" : "-12.30", "2026-07-10", "Automatic candidate");
        if (scenario == "multiple") await Record(accountId, "-12.34", "2026-07-10", "Second automatic candidate");
        var statement = await Statement(accountId, -1234, "2026-07-10");
        var projection = await Project(statement);
        var before = await Actuals();
        var disposition = scenario == "correction" ? "correct_existing_from_statement" : "match_existing";
        var fact = scenario == "correction" ? StatementFact(accountId, "-12.34", "2026-07-10") : null;

        var result = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, projection, disposition, "deterministic_policy", TransactionId(first), fact, null, "Unproven automatic request", Key("automatic")));

        AssertError(result, 8, "operation.review_required");
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_unsupported_projection_policy_is_rejected_without_mutation()
    {
        var accountId = await CreateAccount("Unsupported policy");
        var statement = await Statement(accountId, -1234, "2026-07-10");
        var before = await Actuals();

        var result = await Run(
            ["ledger", "reconciliation", "candidates", "--input", "-"],
            Envelope(new JsonObject
            {
                ["evidenceId"] = statement.EvidenceId,
                ["scopeId"] = statement.ScopeId,
                ["policyId"] = "unsupported-policy",
                ["policyVersion"] = "99.0"
            }));

        AssertError(result, 7, "LEDGER-RECONCILIATION-POLICY-UNSUPPORTED");
        AssertFinancialActualsEqual(before, await Actuals());
    }

    [Theory]
    [InlineData("fingerprint", 5, "LEDGER-RECONCILIATION-EVIDENCE-CHANGED")]
    [InlineData("token", 5, "LEDGER-RECONCILIATION-PROJECTION-CHANGED")]
    [InlineData("candidates", 5, "LEDGER-RECONCILIATION-CANDIDATES-CHANGED")]
    [InlineData("fact", 5, "LEDGER-RECONCILIATION-STATEMENT-FACT-MISMATCH")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_stale_review_material_is_rejected_without_mutation(string scenario, int exitCode, string errorCode)
    {
        var accountId = await CreateAccount("Stale " + scenario);
        var prior = await Record(accountId, "-12.30", "2026-07-11", "Stale candidate");
        if (scenario == "candidates") await Record(accountId, "-12.30", "2026-07-11", "Unreviewed candidate");
        var statement = await Statement(accountId, -1234, "2026-07-11");
        var projection = await Project(statement);
        var before = await Actuals();
        var candidates = CandidateIds(projection);
        if (scenario == "candidates") candidates = [TransactionId(prior)];
        var fact = StatementFact(accountId, scenario == "fact" ? "-99.00" : "-12.34", "2026-07-11");
        var input = ApplyInput(statement, projection, "correct_existing_from_statement", "owner", TransactionId(prior), fact, null, "Owner review");
        input["reviewedCandidateIds"] = Array(candidates);
        if (scenario == "fingerprint") input["evidenceFingerprint"] = Digest("changed-fingerprint");
        if (scenario == "token") input["expectedProjectionToken"] = Digest("changed-token");

        var result = await Run(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(input, Key("stale")));

        AssertError(result, exitCode, errorCode);
        AssertFinancialActualsEqual(before, await Actuals());
        Assert.Equal("active", (await GetTransaction(TransactionId(prior))).GetProperty("lifecycleStatus").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_active_confirmation_conflict_records_no_second_effect()
    {
        var accountId = await CreateAccount("Confirmation conflict");
        var transaction = await Record(accountId, "-12.34", "2026-07-12", "Confirmed candidate");
        var statement = await Statement(accountId, -1234, "2026-07-12");
        var originalProjection = await Project(statement);
        await Apply(statement, originalProjection, "match_existing", "owner", TransactionId(transaction), null, null, "Owner confirmed candidate");
        var conflictProjection = await Project(statement);
        Assert.Equal("conflict", conflictProjection.GetProperty("outcome").GetString());
        var before = await Actuals();

        var result = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, conflictProjection, "record_exception", "owner", null, null, "ACTIVE-CONFIRMATION", "Conflicting second outcome", Key("conflict")));

        AssertError(result, 5, "LEDGER-IDEMPOTENCY-001");
        AssertFinancialActualsEqual(before, await Actuals());
        Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_replay_converges_and_changed_request_conflicts()
    {
        var accountId = await CreateAccount("Replay");
        var transaction = await Record(accountId, "-12.34", "2026-07-13", "Replay candidate");
        var statement = await Statement(accountId, -1234, "2026-07-13");
        var projection = await Project(statement);
        var firstRequest = ApplyEnvelope(statement, projection, "match_existing", "owner", TransactionId(transaction), null, null, "Owner confirmed replay", "same-key");

        var first = await Run(["ledger", "reconciliation", "apply", "--input", "-"], firstRequest);
        var replay = await Run(["ledger", "reconciliation", "apply", "--input", "-"], firstRequest);
        var firstResult = AssertSuccess(first, "ledger.reconciliation.apply");
        var crossKey = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, projection, "match_existing", "owner", TransactionId(transaction), null, null, "Owner confirmed replay", "cross-key"));
        var changed = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, projection, "match_existing", "owner", TransactionId(transaction), null, null, "Changed reason", "same-key"));

        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(firstResult.GetProperty("decisionId").GetString(), AssertSuccess(crossKey, "ledger.reconciliation.apply").GetProperty("decisionId").GetString());
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray());
    }

    [Theory]
    [InlineData("rawPayload")]
    [InlineData("mailbox")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_apply_rejects_transport_or_raw_payload_fields(string field)
    {
        var accountId = await CreateAccount("Privacy " + field);
        var statement = await Statement(accountId, -1234, "2026-07-14");
        var input = ApplyInput(statement, await Project(statement), "record_exception", "owner", null, null, "REVIEW", "Privacy review");
        input[field] = "forbidden";

        AssertError(
            await Run(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(input, Key("privacy"))),
            3,
            "validation.invalid_input");
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("basis")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_requires_owner_authority_and_basis(string scenario)
    {
        var accountId = await CreateAccount("Authority " + scenario);
        var statement = await Statement(accountId, -1234, "2026-07-15");
        var input = ApplyInput(statement, await Project(statement), "record_exception", "owner", null, null, "REVIEW", "Owner reviewed exception");
        if (scenario == "authority") input["authorityKind"] = null;
        else input["reason"] = "";

        AssertError(
            await Run(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(input, Key("authority"))),
            3,
            "validation.invalid_input");
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_interrupted_apply_commits_none_or_one_and_retry_converges()
    {
        var accountId = await CreateAccount("Crash");
        var transaction = await Record(accountId, "-12.34", "2026-07-16", "Crash candidate");
        var statement = await Statement(accountId, -1234, "2026-07-16");
        var projection = await Project(statement);
        var request = ApplyEnvelope(statement, projection, "match_existing", "owner", TransactionId(transaction), null, null, "Crash-atomic confirmation", "crash-apply");

        Assert.True(await KillPublishedProcessDuringMutation(["ledger", "reconciliation", "apply", "--input", "-"], request), "The published process completed before interruption.");
        var current = await GetTransaction(TransactionId(transaction));
        Assert.Contains(current.GetProperty("reconciliationState").GetString(), new[] { "recorded_unreconciled", "owner_confirmed_match" });

        var converged = AssertSuccess(await Run(["ledger", "reconciliation", "apply", "--input", "-"], request), "ledger.reconciliation.apply");
        Assert.Equal(TransactionId(transaction), converged.GetProperty("activeTransactionId").GetString());
        Assert.Equal("owner_confirmed_match", (await GetTransaction(TransactionId(transaction))).GetProperty("reconciliationState").GetString());
        Assert.Single((await Decision(statement.EvidenceId)).GetProperty("history").EnumerateArray());
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
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = "UC014 " + label + " " + suffix,
                ["accountType"] = "cheque",
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<string> CreateInstrument(string accountId) => (await Success(
        ["ledger", "instrument", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = "UC014 instrument " + sequence, ["accountId"] = accountId, ["maskedSuffix"] = "1414" }, Key("instrument")),
        "ledger.instrument.create")).GetProperty("instrumentId").GetString()!;

    private async Task<string> CreateCardholder() => (await Success(
        ["ledger", "cardholder", "create", "--input", "-"],
        Envelope(new JsonObject { ["label"] = "UC014 owner " + sequence }, Key("cardholder")),
        "ledger.cardholder.create")).GetProperty("cardholderId").GetString()!;

    private async Task<string> CreateCategory() => (await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "UC014 category " + sequence, ["parentCategoryId"] = null }, Key("category")),
        "ledger.category.create")).GetProperty("categoryId").GetString()!;

    private async Task<string> CreatePool() => (await Success(
        ["ledger", "pool", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = "UC014 pool " + sequence }, Key("pool")),
        "ledger.pool.create")).GetProperty("poolId").GetString()!;

    private async Task<JsonElement> Record(
        string accountId,
        string amount,
        string date,
        string description,
        string? instrumentId = null,
        string? cardholderId = null)
    {
        var evidenceToken = Key("capture");
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
                ["instrumentId"] = instrumentId,
                ["cardholderId"] = cardholderId,
                ["initialEvidence"] = new JsonObject
                {
                    ["kind"] = "agent_capture",
                    ["logicalIdentityDigest"] = Digest(evidenceToken),
                    ["opaqueExternalReference"] = "capture:" + evidenceToken,
                    ["contentFingerprint"] = null,
                    ["observation"] = null
                }
            }, Key("record")),
            "ledger.transaction.record");
    }

    private async Task AssignCategory(string transactionId, string categoryId) => _ = await Success(
        ["ledger", "transaction", "category", "assign", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["categoryId"] = categoryId, ["reason"] = "Owner classification" }, Key("category-assign")),
        "ledger.transaction.category.assign");

    private async Task AssignPool(JsonElement transaction, string poolId) => _ = await Success(
        ["ledger", "transaction", "pool", "assign", "--input", "-"],
        Envelope(new JsonObject
        {
            ["transactionId"] = TransactionId(transaction),
            ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
            ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId },
            ["reason"] = "Owner pool assignment"
        }, Key("pool-assign")),
        "ledger.transaction.pool.assign");

    private async Task<JsonElement> ConfirmRelationship(string type, string sourceId, string targetId) => await Success(
        type == "transfer" ? ["ledger", "transfer", "confirm", "--input", "-"] : ["ledger", "refund", "confirm", "--input", "-"],
        Envelope(type == "transfer"
            ? new JsonObject { ["outflowTransactionId"] = sourceId, ["inflowTransactionId"] = targetId, ["reason"] = "Owner confirmed transfer" }
            : new JsonObject { ["originalTransactionId"] = sourceId, ["refundTransactionId"] = targetId, ["reason"] = "Owner confirmed full refund" }, Key(type)),
        type == "transfer" ? "ledger.transfer.confirm" : "ledger.refund.confirm");

    private async Task<StatementFixture> Statement(string accountId, long amountMinor, string date)
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
        var evidenceId = evidence.GetProperty("evidenceId").GetString()!;
        var scope = await Success(
            ["ledger", "reconciliation", "scope", "register", "--input", "-"],
            Envelope(new JsonObject
            {
                ["accountId"] = accountId,
                ["periodStart"] = "2026-07-01",
                ["periodEnd"] = "2026-07-31",
                ["manifestOpaqueReference"] = "statement:manifest:" + token,
                ["evidenceIds"] = Array(evidenceId)
            }, Key("scope")),
            "ledger.reconciliation.scope.register");
        return new(accountId, evidenceId, evidence.GetProperty("contentFingerprint").GetString()!, scope.GetProperty("scopeId").GetString()!);
    }

    private async Task<JsonElement> Project(StatementFixture statement, string policyId = "manual_review_projection", string policyVersion = "1.0") => await Success(
        ["ledger", "reconciliation", "candidates", "--input", "-"],
        Envelope(new JsonObject
        {
            ["evidenceId"] = statement.EvidenceId,
            ["scopeId"] = statement.ScopeId,
            ["policyId"] = policyId,
            ["policyVersion"] = policyVersion
        }),
        "ledger.reconciliation.candidates");

    private async Task<JsonElement> Apply(
        StatementFixture statement,
        JsonElement projection,
        string disposition,
        string authority,
        string? target,
        JsonObject? statementFact,
        string? exceptionCode,
        string reason) => await Success(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            ApplyEnvelope(statement, projection, disposition, authority, target, statementFact, exceptionCode, reason, Key("apply")),
            "ledger.reconciliation.apply");

    private static string ApplyEnvelope(
        StatementFixture statement,
        JsonElement projection,
        string disposition,
        string authority,
        string? target,
        JsonObject? statementFact,
        string? exceptionCode,
        string reason,
        string key) => Envelope(ApplyInput(statement, projection, disposition, authority, target, statementFact, exceptionCode, reason), key);

    private static JsonObject ApplyInput(
        StatementFixture statement,
        JsonElement projection,
        string disposition,
        string authority,
        string? target,
        JsonObject? statementFact,
        string? exceptionCode,
        string reason) => new()
        {
            ["evidenceId"] = statement.EvidenceId,
            ["evidenceFingerprint"] = statement.Fingerprint,
            ["scopeId"] = statement.ScopeId,
            ["expectedProjectionToken"] = projection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = disposition,
            ["authorityKind"] = authority,
            ["reviewedCandidateIds"] = Array(CandidateIds(projection)),
            ["targetTransactionId"] = target,
            ["statementFact"] = statementFact,
            ["exceptionCode"] = exceptionCode,
            ["reason"] = reason
        };

    private static JsonObject StatementFact(string accountId, string amount, string date) => new()
    {
        ["accountId"] = accountId,
        ["signedAmount"] = amount,
        ["currencyCode"] = "ZAR",
        ["transactionDate"] = date,
        ["postingDate"] = null,
        ["originalDescription"] = "Statement-authoritative banking transaction"
    };

    private async Task<JsonElement> Decision(string evidenceId) => await Success(
        ["ledger", "reconciliation", "decision", "get", "--input", "-"],
        Envelope(new JsonObject { ["evidenceId"] = evidenceId }),
        "ledger.reconciliation.decision.get");

    private async Task<JsonElement> GetTransaction(string transactionId) => await Success(
        ["ledger", "transaction", "get", "--input", "-"],
        Envelope(new JsonObject { ["transactionId"] = transactionId, ["includeHistory"] = true }),
        "ledger.transaction.get");

    private async Task<JsonElement> GetRelationship(string relationshipId) => await Success(
        ["ledger", "relationship", "get", "--input", "-"],
        Envelope(new JsonObject { ["relationshipId"] = relationshipId, ["includeHistory"] = true }),
        "ledger.relationship.get");

    private async Task<JsonElement> Actuals(string? accountId = null) => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject
            {
                ["accountIds"] = accountId is null ? null : Array(accountId),
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

    private string Key(string purpose) => "uc014-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string TransactionId(JsonElement transaction) => transaction.GetProperty("transactionId").GetString()!;

    private static string[] CandidateIds(JsonElement projection) => projection.GetProperty("exactCandidates").EnumerateArray()
        .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
        .Select(candidate => candidate.GetProperty("transactionId").GetString()!)
        .ToArray();

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertCarryForward(JsonElement history, string sourceTransactionId, string? decisionId)
    {
        var carry = Assert.Single(history.EnumerateArray(), item => item.GetProperty("action").GetString() == "carry_forward");
        Assert.Equal(sourceTransactionId, carry.GetProperty("sourceTransactionId").GetString());
        Assert.Equal(decisionId, carry.GetProperty("reconciliationDecisionId").GetString());
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
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc014", ["runId"] = "published-e2e" },
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

    private sealed record StatementFixture(string AccountId, string EvidenceId, string Fingerprint, string ScopeId);
}
