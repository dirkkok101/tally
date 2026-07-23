using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Relationships;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Relationships;
using Xunit;

namespace Tally.Tests.Features.Ledger.Relationships;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-RELATIONSHIP-CORRECTION-CONTRACT.
public sealed class RelationshipLifecycleTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-relationship-lifecycle-{Guid.NewGuid():N}");
    private int digestSeed;
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private RelationshipStore store = null!;

    [Fact]
    public void DM_LEDGER_RELATIONSHIP_ACTUALS_CONTRACTS_registry_exposes_typed_lifecycle_operations()
    {
        var registry = OperationRegistry.Create();

        AssertDescriptor<RevokeRelationshipInput>(registry, "ledger.transfer.revoke");
        AssertDescriptor<ReplaceTransferInput>(registry, "ledger.transfer.replace");
        AssertDescriptor<RevokeRelationshipInput>(registry, "ledger.refund.revoke");
        AssertDescriptor<ReplaceRefundInput>(registry, "ledger.refund.replace");
        Assert.All(
            new[] { "ledger.transfer.revoke", "ledger.transfer.replace", "ledger.refund.revoke", "ledger.refund.replace" },
            operationId => Assert.Equal(typeof(RelationshipLifecycleResult), registry.Find(operationId)!.ResultTypeInfo.Type));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_transfer_revoke_retires_with_attributable_history()
    {
        var relationship = await CreateTransfer("12.34");

        var result = Lifecycle(await Revoke("ledger.transfer.revoke", relationship.RelationshipId, "owner correction", "revoke"));

        Assert.Equal(relationship.RelationshipId, result.Relationship.RelationshipId);
        Assert.Equal(FinancialRelationshipState.Retired, result.Relationship.State);
        Assert.Null(result.ReplacementRelationship);
        var history = Assert.Single(result.Relationship.History);
        Assert.Equal(result.LifecycleEventId, history.LifecycleEventId);
        Assert.Equal(RelationshipLifecycleAction.Revoked, history.Action);
        Assert.Equal("owner correction", history.Reason);
        Assert.Equal("human:lifecycle-test", history.Actor);
        Assert.Equal(0, await ActiveRelationshipCount());
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_refund_revoke_retires_and_removes_active_refund_effect()
    {
        var relationship = await CreateRefund("12.34");

        var result = Lifecycle(await Revoke("ledger.refund.revoke", relationship.RelationshipId, "not a refund", "revoke"));

        Assert.Equal(FinancialRelationshipState.Retired, result.Relationship.State);
        Assert.Equal(FinancialRelationshipType.Refund, result.Relationship.Type);
        Assert.Equal(0, await ActiveRelationshipCount());
        Assert.Equal(0, await CountWhere("refund_current_dimensions", "1 = 1"));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_revoke_rejects_missing_retired_and_wrong_type_stably()
    {
        var transfer = await CreateTransfer("10");

        AssertError(await Revoke("ledger.refund.revoke", transfer.RelationshipId, "wrong type", "wrong"), 6, RelationshipLifecycleErrors.TypeMismatch);
        AssertError(await Revoke("ledger.transfer.revoke", LedgerId.New().ToString(), "missing", "missing"), 4, RelationshipLifecycleErrors.NotFound);
        await RetireRelationship(transfer.RelationshipId);
        AssertError(await Revoke("ledger.transfer.revoke", transfer.RelationshipId, "second", "second"), 6, RelationshipLifecycleErrors.AlreadyRetired);
        Assert.Equal(1, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_transfer_replace_is_atomic_and_preserves_participants()
    {
        var fixture = await TransferFixture("12.34");
        var replacementInflow = await Record(fixture.InflowAccount.AccountId, "12.34");

        var result = Lifecycle(await ReplaceTransfer(
            fixture.Relationship.RelationshipId,
            fixture.Outflow.TransactionId,
            replacementInflow.TransactionId,
            "correct inflow", "replace"));

        Assert.Equal(FinancialRelationshipState.Retired, result.Relationship.State);
        Assert.Equal(result.ReplacementRelationship!.RelationshipId, Assert.Single(result.Relationship.History).ReplacementRelationshipId);
        Assert.Equal(fixture.Outflow.TransactionId, result.ReplacementRelationship.SourceTransactionId);
        Assert.Equal(replacementInflow.TransactionId, result.ReplacementRelationship.TargetTransactionId);
        Assert.Equal(FinancialRelationshipState.Active, result.ReplacementRelationship.State);
        Assert.Equal(1, await ActiveRelationshipCount());
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_full_refund_replace_accepts_one_exact_credit()
    {
        var fixture = await RefundFixture("12.34");
        var replacementCredit = await Record(fixture.Account.AccountId, "12.34");

        var result = Lifecycle(await ReplaceRefund(
            fixture.Relationship.RelationshipId,
            fixture.Original.TransactionId,
            replacementCredit.TransactionId,
            "correct refund", "replace"));

        Assert.Equal("12.34", result.ReplacementRelationship!.PrincipalAmount);
        Assert.Equal(FinancialRelationshipType.Refund, result.ReplacementRelationship.Type);
        Assert.Equal(1, await ActiveRelationshipCount());
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_partial_refund_replace_is_rejected_before_retirement()
    {
        var fixture = await RefundFixture("12.34");
        var partialCredit = await Record(fixture.Account.AccountId, "6.17");

        AssertError(await ReplaceRefund(
            fixture.Relationship.RelationshipId,
            fixture.Original.TransactionId,
            partialCredit.TransactionId,
            "partial", "replace"), 3, RefundErrors.Amount);

        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_over_refund_replace_is_rejected_before_retirement()
    {
        var fixture = await RefundFixture("12.34");
        var overCredit = await Record(fixture.Account.AccountId, "12.35");

        AssertError(await ReplaceRefund(
            fixture.Relationship.RelationshipId,
            fixture.Original.TransactionId,
            overCredit.TransactionId,
            "over", "replace"), 3, RefundErrors.Amount);

        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_transfer_amount_failure_leaves_old_relationship_active()
    {
        var fixture = await TransferFixture("12.34");
        var wrongInflow = await Record(fixture.InflowAccount.AccountId, "12.33");

        AssertError(await ReplaceTransfer(
            fixture.Relationship.RelationshipId,
            fixture.Outflow.TransactionId,
            wrongInflow.TransactionId,
            "wrong amount", "replace"), 3, TransferErrors.Amount);

        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_inactive_replacement_transaction_leaves_old_relationship_active()
    {
        var fixture = await TransferFixture("12.34");
        var replacementInflow = await Record(fixture.InflowAccount.AccountId, "12.34");
        await Terminate(replacementInflow.TransactionId);

        AssertError(await ReplaceTransfer(
            fixture.Relationship.RelationshipId,
            fixture.Outflow.TransactionId,
            replacementInflow.TransactionId,
            "inactive", "replace"), 6, TransferErrors.TransactionInactive);

        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_archived_replacement_account_leaves_old_relationship_active()
    {
        var fixture = await TransferFixture("12.34");
        var replacementAccount = await CreateAccount("Replacement", AccountType.Savings);
        var replacementInflow = await Record(replacementAccount.AccountId, "12.34");
        await ArchiveAccount(replacementAccount.AccountId);

        AssertError(await ReplaceTransfer(
            fixture.Relationship.RelationshipId,
            fixture.Outflow.TransactionId,
            replacementInflow.TransactionId,
            "archived", "replace"), 6, "LEDGER-ACCOUNT-ARCHIVED");

        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_cross_role_conflict_leaves_old_relationship_active()
    {
        var first = await TransferFixture("12.34");
        var second = await TransferFixture("12.34");

        AssertError(await ReplaceTransfer(
            first.Relationship.RelationshipId,
            first.Outflow.TransactionId,
            second.Inflow.TransactionId,
            "conflicting inflow", "replace"), 5, TransferErrors.ActiveRoleConflict);

        Assert.Equal(2, await ActiveRelationshipCount());
        Assert.Equal(0, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_revoke_replay_is_stable_and_changed_input_conflicts()
    {
        var relationship = await CreateTransfer("12.34");
        var input = new RevokeRelationshipInput(relationship.RelationshipId, "owner correction");
        var first = Lifecycle(await Run("ledger.transfer.revoke", input, LedgerJsonContext.Default.RevokeRelationshipInput, "same"));

        var replay = Lifecycle(await Run("ledger.transfer.revoke", input, LedgerJsonContext.Default.RevokeRelationshipInput, "same"));

        Assert.Equal(first.LifecycleEventId, replay.LifecycleEventId);
        Assert.Equal(first.LifecycleEventId, Lifecycle(await Run("ledger.transfer.revoke", input, LedgerJsonContext.Default.RevokeRelationshipInput, "other")).LifecycleEventId);
        AssertError(await Run("ledger.transfer.revoke", input with { Reason = "changed" }, LedgerJsonContext.Default.RevokeRelationshipInput, "same"), 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_replace_replay_is_stable_and_changed_input_conflicts()
    {
        var fixture = await TransferFixture("12.34");
        var replacementInflow = await Record(fixture.InflowAccount.AccountId, "12.34");
        var input = new ReplaceTransferInput(fixture.Relationship.RelationshipId, fixture.Outflow.TransactionId, replacementInflow.TransactionId, "correct inflow");
        var first = Lifecycle(await Run("ledger.transfer.replace", input, LedgerJsonContext.Default.ReplaceTransferInput, "same"));

        var replay = Lifecycle(await Run("ledger.transfer.replace", input, LedgerJsonContext.Default.ReplaceTransferInput, "same"));

        Assert.Equal(first.ReplacementRelationship!.RelationshipId, replay.ReplacementRelationship!.RelationshipId);
        Assert.Equal(first.ReplacementRelationship.RelationshipId, Lifecycle(await Run("ledger.transfer.replace", input, LedgerJsonContext.Default.ReplaceTransferInput, "other")).ReplacementRelationship!.RelationshipId);
        AssertError(await Run("ledger.transfer.replace", input with { Reason = "changed" }, LedgerJsonContext.Default.ReplaceTransferInput, "same"), 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(2, await CountWhere("financial_relationship", "1 = 1"));
        Assert.Equal(1, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_replace_retains_old_and_new_relationship_details()
    {
        var fixture = await RefundFixture("12.34");
        var replacementCredit = await Record(fixture.Account.AccountId, "12.34");
        var replaced = Lifecycle(await ReplaceRefund(
            fixture.Relationship.RelationshipId,
            fixture.Original.TransactionId,
            replacementCredit.TransactionId,
            "statement corrected credit", "replace"));

        var oldDetail = Relationship(await GetRelationship(fixture.Relationship.RelationshipId, true));
        var newDetail = Relationship(await GetRelationship(replaced.ReplacementRelationship!.RelationshipId, true));

        Assert.Equal(fixture.Original.TransactionId, oldDetail.SourceTransactionId);
        Assert.Equal(fixture.Credit.TransactionId, oldDetail.TargetTransactionId);
        Assert.Equal(RelationshipLifecycleAction.Replaced, Assert.Single(oldDetail.History).Action);
        Assert.Equal(newDetail.RelationshipId, oldDetail.History[0].ReplacementRelationshipId);
        Assert.Empty(newDetail.History);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_statement_correction_substitutes_authorized_transaction()
    {
        var fixture = await RefundFixture("12.34");
        var replacementCredit = await Record(fixture.Account.AccountId, "12.34");
        var decisionId = await AuthorizeStatementCorrection(fixture.Credit.TransactionId, replacementCredit.TransactionId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var outcome = await store.ReplaceForStatementCorrectionAsync(
            connection, transaction, fixture.Relationship.RelationshipId, fixture.Credit.TransactionId,
            replacementCredit.TransactionId, decisionId, "statement correction", "system:reconciliation", At,
            CancellationToken.None);
        await transaction.CommitAsync();

        Assert.False(outcome.ReviewRequired);
        Assert.Null(outcome.ErrorCode);
        Assert.Equal(decisionId, outcome.ReplacementRelationship!.ReconciliationDecisionId);
        Assert.Equal(replacementCredit.TransactionId, outcome.ReplacementRelationship.TargetTransactionId);
        Assert.Equal(decisionId, Assert.Single(outcome.Relationship!.History).ReconciliationDecisionId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_statement_correction_without_authority_requires_review_before_write()
    {
        var fixture = await RefundFixture("12.34");
        var replacementCredit = await Record(fixture.Account.AccountId, "12.34");

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var outcome = await store.ReplaceForStatementCorrectionAsync(
            connection, transaction, fixture.Relationship.RelationshipId, fixture.Credit.TransactionId,
            replacementCredit.TransactionId, LedgerId.New().ToString(), "unauthorized", "system:test", At,
            CancellationToken.None);
        await transaction.CommitAsync();

        Assert.True(outcome.ReviewRequired);
        Assert.Equal(RelationshipLifecycleErrors.ReviewRequired, outcome.ErrorCode);
        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Fact]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_statement_correction_invariant_failure_requires_review_before_write()
    {
        var fixture = await RefundFixture("12.34");
        var replacementCredit = await Record(fixture.Account.AccountId, "6.17");
        var decisionId = await AuthorizeStatementCorrection(fixture.Credit.TransactionId, replacementCredit.TransactionId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var outcome = await store.ReplaceForStatementCorrectionAsync(
            connection, transaction, fixture.Relationship.RelationshipId, fixture.Credit.TransactionId,
            replacementCredit.TransactionId, decisionId, "partial statement credit", "system:reconciliation", At,
            CancellationToken.None);
        await transaction.CommitAsync();

        Assert.True(outcome.ReviewRequired);
        Assert.Equal(RefundErrors.Amount, outcome.ErrorCode);
        await AssertOldRelationshipUnchanged(fixture.Relationship.RelationshipId);
    }

    [Theory]
    [InlineData("relationship")]
    [InlineData("participant")]
    [InlineData("reason")]
    public async Task FR_LEDGER_RELATIONSHIP_CORRECTION_invalid_public_replace_contract_is_atomic(string field)
    {
        var relationshipId = field == "relationship" ? "invalid" : LedgerId.New().ToString();
        var participantId = field == "participant" ? "invalid" : LedgerId.New().ToString();
        var reason = field == "reason" ? "" : "owner correction";

        AssertError(await ReplaceTransfer(relationshipId, participantId, LedgerId.New().ToString(), reason, "invalid-" + field), 3, RelationshipLifecycleErrors.Invalid);
        Assert.Equal(0, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        store = new(database, factory);
        process = new(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<TransferTestFixture> TransferFixture(string amount)
    {
        var outflowAccount = await CreateAccount("Transfer out", AccountType.Cheque);
        var inflowAccount = await CreateAccount("Transfer in", AccountType.Savings);
        var outflow = await Record(outflowAccount.AccountId, "-" + amount);
        var inflow = await Record(inflowAccount.AccountId, amount);
        var confirmationKey = "confirm-transfer-" + ++digestSeed;
        var relationship = Relationship(await Run(
            "ledger.transfer.confirm",
            new ConfirmTransferInput(outflow.TransactionId, inflow.TransactionId, "owner confirmed"),
            LedgerJsonContext.Default.ConfirmTransferInput,
            confirmationKey));
        return new(outflowAccount, inflowAccount, outflow, inflow, relationship);
    }

    private async Task<RefundTestFixture> RefundFixture(string amount)
    {
        var account = await CreateAccount("Refund account", AccountType.Cheque);
        var original = await Record(account.AccountId, "-" + amount);
        var credit = await Record(account.AccountId, amount);
        var confirmationKey = "confirm-refund-" + ++digestSeed;
        var relationship = Relationship(await Run(
            "ledger.refund.confirm",
            new ConfirmRefundInput(original.TransactionId, credit.TransactionId, "owner confirmed"),
            LedgerJsonContext.Default.ConfirmRefundInput,
            confirmationKey));
        return new(account, original, credit, relationship);
    }

    private async Task<FinancialRelationshipDetail> CreateTransfer(string amount) => (await TransferFixture(amount)).Relationship;
    private async Task<FinancialRelationshipDetail> CreateRefund(string amount) => (await RefundFixture(amount)).Relationship;

    private async Task<AccountDetail> CreateAccount(string name, AccountType type)
    {
        var seed = ++digestSeed;
        var input = new CreateAccountInput("Test Bank", name + seed, type, "****" + seed.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), "ZAR");
        return Success(await Run("ledger.account.create", input, LedgerJsonContext.Default.CreateAccountInput, "account-" + seed), LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId) => Run(
        "ledger.account.archive",
        new ArchiveAccountInput(accountId, "archive for test"),
        LedgerJsonContext.Default.ArchiveAccountInput,
        "archive-" + accountId);

    private async Task<TransactionDetail> Record(string accountId, string amount)
    {
        var seed = ++digestSeed;
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", "2026-07-01", null, "Owner-safe banking transaction", null, null,
            new(EvidenceKind.AgentCapture, Digest(seed), "capture:" + seed, null, null));
        return Success(await Run("ledger.transaction.record", input, LedgerJsonContext.Default.RecordTransactionInput, "record-" + seed), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Revoke(string operationId, string relationshipId, string reason, string key) => Run(
        operationId, new RevokeRelationshipInput(relationshipId, reason), LedgerJsonContext.Default.RevokeRelationshipInput, key);

    private Task<ProcessResult> ReplaceTransfer(string relationshipId, string outflowId, string inflowId, string reason, string key) => Run(
        "ledger.transfer.replace", new ReplaceTransferInput(relationshipId, outflowId, inflowId, reason), LedgerJsonContext.Default.ReplaceTransferInput, key);

    private Task<ProcessResult> ReplaceRefund(string relationshipId, string originalId, string refundId, string reason, string key) => Run(
        "ledger.refund.replace", new ReplaceRefundInput(relationshipId, originalId, refundId, reason), LedgerJsonContext.Default.ReplaceRefundInput, key);

    private Task<ProcessResult> GetRelationship(string relationshipId, bool includeHistory) => Run(
        "ledger.relationship.get", new GetRelationshipInput(relationshipId, includeHistory), LedgerJsonContext.Default.GetRelationshipInput, null);

    private async Task AssertOldRelationshipUnchanged(string relationshipId)
    {
        var old = Relationship(await GetRelationship(relationshipId, true));
        Assert.Equal(FinancialRelationshipState.Active, old.State);
        Assert.Empty(old.History);
        Assert.Equal(1, await CountWhere("financial_relationship", $"relationship_id = '{relationshipId}'"));
        Assert.Equal(0, await CountWhere("relationship_lifecycle_event", "1 = 1"));
    }

    private async Task Terminate(string transactionId)
    {
        await using var connection = await Open();
        await Execute(connection, "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, 'void', NULL, NULL, 'test', 'system:test', $at);",
            ("$eventId", LedgerId.New().ToString()), ("$transactionId", transactionId), ("$at", At));
    }

    private async Task RetireRelationship(string relationshipId)
    {
        await using var connection = await Open();
        await Execute(connection, """
            INSERT INTO relationship_lifecycle_event (
                lifecycle_event_id, relationship_id, event_type, replacement_relationship_id,
                reconciliation_decision_id, reason, actor_context, occurred_at)
            VALUES ($eventId, $relationshipId, 'revoked', NULL, NULL, 'test retirement', 'system:test', $at);
            """,
            ("$eventId", LedgerId.New().ToString()), ("$relationshipId", relationshipId), ("$at", At));
    }

    private async Task<string> AuthorizeStatementCorrection(string sourceId, string replacementId)
    {
        var evidenceId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await Execute(connection, """
            INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, NULL, NULL, 'system:test', $at);
            INSERT INTO reconciliation_decision (
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', NULL, NULL,
                    'statement authority', 0, 'corrected from statement', 'system:reconciliation', $at, NULL);
            INSERT INTO reconciliation_decision_authority (
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'corrected_from_statement', $sourceId, $replacementId,
                    'owner', 'statement row authority', 'v2', $at);
            INSERT INTO transaction_lifecycle_event (
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($lifecycleId, $sourceId, 'statement_authoritative_replacement', $replacementId,
                    $decisionId, 'statement correction', 'system:reconciliation', $at);
            """,
            ("$evidenceId", evidenceId), ("$digest", Digest(++digestSeed)), ("$decisionId", decisionId),
            ("$replacementId", replacementId), ("$sourceId", sourceId), ("$lifecycleId", LedgerId.New().ToString()), ("$at", At));
        return decisionId;
    }

    private async Task<ProcessResult> Run<T>(string operationId, T input, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string? key)
    {
        var request = new RequestEnvelope("1.0", new("human", "lifecycle-test"), JsonSerializer.SerializeToElement(input, type), key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> ActiveRelationshipCount() => await CountWhere("financial_relationship_current", "state = 'active'");

    private async Task<long> CountWhere(string table, string condition)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {condition};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string Digest(int seed) => string.Concat(Enumerable.Repeat(seed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
    private static FinancialRelationshipDetail Relationship(ProcessResult result) => Success(result, LedgerJsonContext.Default.FinancialRelationshipDetail);
    private static RelationshipLifecycleResult Lifecycle(ProcessResult result) => Success(result, LedgerJsonContext.Default.RelationshipLifecycleResult);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        var actualCode = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code;
        Assert.Equal(code, actualCode);
        Assert.Equal(exitCode, result.ExitCode);
    }

    private static void AssertDescriptor<T>(OperationRegistry registry, string operationId)
    {
        var descriptor = registry.Find(operationId)!;
        Assert.Equal(typeof(T), descriptor.RequestTypeInfo.Type);
        Assert.True(descriptor.RequiresIdempotencyKey);
    }

    private sealed record TransferTestFixture(
        AccountDetail OutflowAccount,
        AccountDetail InflowAccount,
        TransactionDetail Outflow,
        TransactionDetail Inflow,
        FinancialRelationshipDetail Relationship);

    private sealed record RefundTestFixture(
        AccountDetail Account,
        TransactionDetail Original,
        TransactionDetail Credit,
        FinancialRelationshipDetail Relationship);
}
