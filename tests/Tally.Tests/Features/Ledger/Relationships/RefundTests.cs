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
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Features.Ledger.Relationships;

[SupportedOSPlatform("linux")]
public sealed class RefundTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-refund-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private int digestSeed;

    [Fact]
    public void DM_LEDGER_RELATIONSHIP_ACTUALS_CONTRACTS_registry_exposes_refund_confirm()
    {
        var descriptor = OperationRegistry.Create().Find("ledger.refund.confirm")!;

        Assert.Equal(typeof(ConfirmRefundInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(FinancialRelationshipDetail), descriptor.ResultTypeInfo.Type);
        Assert.Equal("RefundOperationModule.Confirm", descriptor.HandlerTarget);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Contains(descriptor.DomainErrors!, error => error.Code == RefundErrors.Amount && error.Category == "validation");
        Assert.Contains(descriptor.DomainErrors!, error => error.Code == RefundErrors.ActiveRoleConflict && error.Category == "conflict");
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_exact_full_amount_creates_active_refund()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");

        var relationship = Relationship(await Confirm(original.TransactionId, credit.TransactionId, "owner confirmed full refund", "confirm"));

        Assert.True(LedgerId.TryParse(relationship.RelationshipId, out _, out _));
        Assert.Equal(FinancialRelationshipType.Refund, relationship.Type);
        Assert.Equal(original.TransactionId, relationship.SourceTransactionId);
        Assert.Equal(FinancialRelationshipRole.RefundOriginal, relationship.SourceRole);
        Assert.Equal(credit.TransactionId, relationship.TargetTransactionId);
        Assert.Equal(FinancialRelationshipRole.RefundCredit, relationship.TargetRole);
        Assert.Equal("12.34", relationship.PrincipalAmount);
        Assert.Equal("ZAR", relationship.CurrencyCode);
        Assert.Equal(FinancialRelationshipState.Active, relationship.State);
        Assert.Equal("human:refund-test", relationship.Actor);
        Assert.Empty(relationship.History);
    }

    [Fact]
    public async Task DD_LEDGER_FULL_AMOUNT_REFUND_RELATIONSHIP_boundary_amount_and_later_date_are_exact()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-92233720368547758.07", "2026-01-01");
        var credit = await Record(account.AccountId, "92233720368547758.07", "2026-12-31");

        var relationship = Relationship(await Confirm(original.TransactionId, credit.TransactionId, "full boundary refund", "confirm"));

        Assert.Equal("92233720368547758.07", relationship.PrincipalAmount);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("12.33")]
    [InlineData("12.35")]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_partial_and_over_refunds_are_rejected_atomically(string refundAmount)
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, refundAmount, "2026-07-20");
        var idempotencyCount = await Count("idempotency_record");

        AssertError(await Confirm(original.TransactionId, credit.TransactionId, "amount mismatch", "confirm"), 3, RefundErrors.Amount);
        Assert.Equal(0, await Count("financial_relationship"));
        Assert.Equal(idempotencyCount, await Count("idempotency_record"));
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_different_accounts_are_rejected_atomically()
    {
        var originalAccount = await CreateAccount("Original", "1111");
        var refundAccount = await CreateAccount("Refund", "2222");
        var original = await Record(originalAccount.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(refundAccount.AccountId, "12.34", "2026-07-20");

        AssertError(await Confirm(original.TransactionId, credit.TransactionId, "wrong account", "confirm"), 3, RefundErrors.Account);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("12.34", "-12.34")]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_explicit_original_and_credit_signs_are_required(string originalAmount, string refundAmount)
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, originalAmount, "2026-07-01");
        var credit = await Record(account.AccountId, refundAmount, "2026-07-20");

        AssertError(await Confirm(original.TransactionId, credit.TransactionId, "wrong roles", "confirm"), 3, RefundErrors.Sign);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("refund")]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_inactive_participant_is_rejected_atomically(string participant)
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        await Terminate(participant == "original" ? original.TransactionId : credit.TransactionId);

        AssertError(await Confirm(original.TransactionId, credit.TransactionId, "inactive", "confirm"), 6, RefundErrors.TransactionInactive);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_archived_account_is_rejected_atomically()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        await ArchiveAccount(account.AccountId);

        AssertError(await Confirm(original.TransactionId, credit.TransactionId, "archived account", "confirm"), 6, "LEDGER-ACCOUNT-ARCHIVED");
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task DD_LEDGER_FULL_AMOUNT_REFUND_RELATIONSHIP_second_active_refund_is_rejected_as_role_duplicate()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var firstCredit = await Record(account.AccountId, "12.34", "2026-07-20");
        var secondCredit = await Record(account.AccountId, "12.34", "2026-07-21");
        var first = Relationship(await Confirm(original.TransactionId, firstCredit.TransactionId, "first full refund", "first"));

        AssertError(await Confirm(original.TransactionId, secondCredit.TransactionId, "duplicate full refund", "second"), 5, RefundErrors.ActiveRoleConflict);
        Assert.Equal(1, await Count("financial_relationship"));
        Assert.Equal(first.RelationshipId, await ActiveRelationshipFor(original.TransactionId));
    }

    [Fact]
    public async Task DD_LEDGER_FULL_AMOUNT_REFUND_RELATIONSHIP_refund_credit_cannot_be_reused()
    {
        var account = await CreateAccount("Primary", "1111");
        var firstOriginal = await Record(account.AccountId, "-12.34", "2026-07-01");
        var secondOriginal = await Record(account.AccountId, "-12.34", "2026-07-02");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        await Confirm(firstOriginal.TransactionId, credit.TransactionId, "first full refund", "first");

        AssertError(await Confirm(secondOriginal.TransactionId, credit.TransactionId, "credit reuse", "second"), 5, RefundErrors.ActiveRoleConflict);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task DD_LEDGER_FULL_AMOUNT_REFUND_RELATIONSHIP_transfer_role_cannot_overlap_refund()
    {
        var originalAccount = await CreateAccount("Primary", "1111");
        var transferAccount = await CreateAccount("Savings", "2222");
        var original = await Record(originalAccount.AccountId, "-12.34", "2026-07-01");
        var transferInflow = await Record(transferAccount.AccountId, "12.34", "2026-07-02");
        var refundCredit = await Record(originalAccount.AccountId, "12.34", "2026-07-20");
        await ConfirmTransfer(original.TransactionId, transferInflow.TransactionId, "owner transfer", "transfer");

        AssertError(await Confirm(original.TransactionId, refundCredit.TransactionId, "role overlap", "refund"), 5, RefundErrors.ActiveRoleConflict);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_replays_are_stable_and_changed_replay_conflicts()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        var input = new ConfirmRefundInput(original.TransactionId, credit.TransactionId, "owner confirmed");
        var first = Relationship(await Confirm(input, "same"));

        Assert.Equal(first.RelationshipId, Relationship(await Confirm(input, "same")).RelationshipId);
        Assert.Equal(first.RelationshipId, Relationship(await Confirm(input, "other")).RelationshipId);
        AssertError(await Confirm(input with { Reason = "changed" }, "same"), 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1, await Count("financial_relationship"));
        Assert.Equal(1, await CountWhere("idempotency_record", "operation_id LIKE '%ledger.refund.confirm'"));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("refund")]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_missing_participant_is_rejected(string participant)
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");

        var result = participant == "original"
            ? await Confirm(LedgerId.New().ToString(), credit.TransactionId, "missing", "confirm")
            : await Confirm(original.TransactionId, LedgerId.New().ToString(), "missing", "confirm");

        AssertError(result, 4, TransactionErrors.NotFound);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("refund")]
    [InlineData("same")]
    [InlineData("reason")]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_invalid_contract_is_atomic(string field)
    {
        var originalId = LedgerId.New().ToString();
        var refundId = field == "same" ? originalId : LedgerId.New().ToString();
        var input = new ConfirmRefundInput(
            field == "original" ? "invalid" : originalId,
            field == "refund" ? "invalid" : refundId,
            field == "reason" ? "" : "owner confirmed");

        AssertError(await Confirm(input, "confirm"), 3, RefundErrors.Invalid);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_non_zar_currency_policy_is_fail_closed()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");

        Assert.False(RefundPolicy.TryFullAmount(original with { CurrencyCode = "USD" }, credit, out _, out var error));
        Assert.Equal(RefundErrors.Currency, error);
        Assert.False(RefundPolicy.TryFullAmount(original with { CurrencyCode = "USD" }, credit with { CurrencyCode = "USD" }, out _, out error));
        Assert.Equal(RefundErrors.Currency, error);
    }

    [Fact]
    public async Task FR_LEDGER_REFUND_CONFIRMATION_relationship_get_returns_exact_detail_and_history()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        var created = Relationship(await Confirm(original.TransactionId, credit.TransactionId, "owner confirmed", "confirm"));

        var fetched = Relationship(await GetRelationship(created.RelationshipId, true));

        Assert.Equal(
            JsonSerializer.Serialize(created, LedgerJsonContext.Default.FinancialRelationshipDetail),
            JsonSerializer.Serialize(fetched, LedgerJsonContext.Default.FinancialRelationshipDetail));
        Assert.Empty(fetched.History);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_confirmation_does_not_mutate_source_transactions()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        var originalBefore = JsonSerializer.Serialize(await GetTransaction(original.TransactionId), LedgerJsonContext.Default.TransactionDetail);
        var creditBefore = JsonSerializer.Serialize(await GetTransaction(credit.TransactionId), LedgerJsonContext.Default.TransactionDetail);

        await Confirm(original.TransactionId, credit.TransactionId, "owner confirmed", "confirm");

        Assert.Equal(originalBefore, JsonSerializer.Serialize(await GetTransaction(original.TransactionId), LedgerJsonContext.Default.TransactionDetail));
        Assert.Equal(creditBefore, JsonSerializer.Serialize(await GetTransaction(credit.TransactionId), LedgerJsonContext.Default.TransactionDetail));
    }

    [Fact]
    public async Task DD_LEDGER_FULL_AMOUNT_REFUND_RELATIONSHIP_existing_exclusivity_trigger_rejects_direct_duplicate()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var firstCredit = await Record(account.AccountId, "12.34", "2026-07-20");
        var secondCredit = await Record(account.AccountId, "12.34", "2026-07-21");
        await Confirm(original.TransactionId, firstCredit.TransactionId, "first", "first");
        await using var connection = await Open();

        var error = await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, """
            INSERT INTO financial_relationship (
                relationship_id, relationship_type, source_transaction_id, source_role,
                target_transaction_id, target_role, amount_minor, state, created_at, actor_context)
            VALUES ($id, 'refund', $original, 'refund_original', $refund, 'refund_credit', 1234, 'active', $at, 'system:test');
            """, ("$id", LedgerId.New().ToString()), ("$original", original.TransactionId), ("$refund", secondCredit.TransactionId), ("$at", At)));

        Assert.Contains("active relationship role already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_refund_relationship_rows_reject_update_and_delete()
    {
        var account = await CreateAccount("Primary", "1111");
        var original = await Record(account.AccountId, "-12.34", "2026-07-01");
        var credit = await Record(account.AccountId, "12.34", "2026-07-20");
        await Confirm(original.TransactionId, credit.TransactionId, "owner confirmed", "confirm");
        await using var connection = await Open();

        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE financial_relationship SET amount_minor = 1;"));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM financial_relationship;"));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<AccountDetail> CreateAccount(string name, string suffix)
    {
        var input = new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + suffix, "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), "account-" + suffix), LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId) => Run(
        "ledger.account.archive",
        JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, "archive for test"), LedgerJsonContext.Default.ArchiveAccountInput),
        "archive-account");

    private async Task<TransactionDetail> Record(string accountId, string amount, string date)
    {
        var seed = ++digestSeed;
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", date, null, "Owner-safe banking transaction", null, null,
            new(EvidenceKind.AgentCapture, Digest(seed), "capture:" + seed, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + seed), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Confirm(string originalId, string refundId, string reason, string key) => Confirm(new(originalId, refundId, reason), key);

    private Task<ProcessResult> Confirm(ConfirmRefundInput input, string key) => Run(
        "ledger.refund.confirm", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.ConfirmRefundInput), key);

    private Task<ProcessResult> ConfirmTransfer(string outflowId, string inflowId, string reason, string key) => Run(
        "ledger.transfer.confirm",
        JsonSerializer.SerializeToElement(new ConfirmTransferInput(outflowId, inflowId, reason), LedgerJsonContext.Default.ConfirmTransferInput),
        key);

    private Task<ProcessResult> GetRelationship(string relationshipId, bool includeHistory) => Run(
        "ledger.relationship.get",
        JsonSerializer.SerializeToElement(new GetRelationshipInput(relationshipId, includeHistory), LedgerJsonContext.Default.GetRelationshipInput),
        null);

    private async Task<TransactionDetail> GetTransaction(string transactionId) => Success(
        await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, true), LedgerJsonContext.Default.GetTransactionInput), null),
        LedgerJsonContext.Default.TransactionDetail);

    private async Task Terminate(string transactionId)
    {
        await using var connection = await Open();
        await Execute(connection, "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, 'void', NULL, NULL, 'test', 'system:test', $at);",
            ("$eventId", LedgerId.New().ToString()), ("$transactionId", transactionId), ("$at", At));
    }

    private async Task<string?> ActiveRelationshipFor(string transactionId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relationship_id FROM financial_relationship_current WHERE state = 'active' AND (source_transaction_id = $id OR target_transaction_id = $id);";
        command.Parameters.AddWithValue("$id", transactionId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", new("human", "refund-test"), input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table) => await CountWhere(table, "1 = 1");

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

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code);
    }
}
