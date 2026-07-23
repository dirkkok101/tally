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
// Covers TC-LEDGER-TRANSFER-CONFIRMATION-CONTRACT.
public sealed class TransferTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-transfer-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;

    [Fact]
    public void DM_LEDGER_RELATIONSHIP_ACTUALS_CONTRACTS_registry_exposes_transfer_confirm_and_relationship_get()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(typeof(ConfirmTransferInput), registry.Find("ledger.transfer.confirm")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(GetRelationshipInput), registry.Find("ledger.relationship.get")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(FinancialRelationshipDetail), registry.Find("ledger.transfer.confirm")!.ResultTypeInfo.Type);
        Assert.True(registry.Find("ledger.transfer.confirm")!.RequiresIdempotencyKey);
        Assert.False(registry.Find("ledger.relationship.get")!.RequiresIdempotencyKey);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_equal_opposite_owned_account_legs_create_active_transfer()
    {
        var outflowAccount = await CreateAccount("Cheque", "1111", AccountType.Cheque, "outflow-account");
        var inflowAccount = await CreateAccount("Credit card", "2222", AccountType.CreditCard, "inflow-account");
        var outflow = await Record(outflowAccount.AccountId, "-12.34", "2026-07-01", 1);
        var inflow = await Record(inflowAccount.AccountId, "12.34", "2026-07-01", 2);

        var relationship = Relationship(await Confirm(outflow.TransactionId, inflow.TransactionId, "owner confirmed transfer", "confirm"));

        Assert.True(LedgerId.TryParse(relationship.RelationshipId, out _, out _));
        Assert.Equal(FinancialRelationshipType.Transfer, relationship.Type);
        Assert.Equal(outflow.TransactionId, relationship.SourceTransactionId);
        Assert.Equal(FinancialRelationshipRole.TransferOutflow, relationship.SourceRole);
        Assert.Equal(inflow.TransactionId, relationship.TargetTransactionId);
        Assert.Equal(FinancialRelationshipRole.TransferInflow, relationship.TargetRole);
        Assert.Equal("12.34", relationship.PrincipalAmount);
        Assert.Equal("ZAR", relationship.CurrencyCode);
        Assert.Equal(FinancialRelationshipState.Active, relationship.State);
        Assert.Equal("human:transfer-test", relationship.Actor);
        Assert.Empty(relationship.History);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_different_dates_do_not_block_explicit_confirmation()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-100", "2026-01-01", 3);
        var inflow = await Record(second.AccountId, "100", "2026-02-15", 4);

        var relationship = Relationship(await Confirm(outflow.TransactionId, inflow.TransactionId, "dates independently supplied", "confirm"));

        Assert.Equal("100", relationship.PrincipalAmount);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_separately_recorded_fee_remains_unlinked()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-50", "2026-07-01", 5);
        var inflow = await Record(second.AccountId, "50", "2026-07-01", 6);
        var fee = await Record(first.AccountId, "-0.50", "2026-07-01", 7);

        await Confirm(outflow.TransactionId, inflow.TransactionId, "principal only", "confirm");

        Assert.Equal(0, await ActiveRelationshipsFor(fee.TransactionId));
        Assert.Equal("-0.50", (await GetTransaction(fee.TransactionId)).SignedAmount);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, (await GetTransaction(fee.TransactionId)).ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_same_account_legs_are_rejected_atomically()
    {
        var account = await CreateAccount("Primary", "1111", AccountType.Cheque, "account");
        var outflow = await Record(account.AccountId, "-12.34", "2026-07-01", 8);
        var inflow = await Record(account.AccountId, "12.34", "2026-07-01", 9);

        AssertError(await Confirm(outflow.TransactionId, inflow.TransactionId, "same account", "confirm"), 3, TransferErrors.SameAccount);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("12.34", "-12.34")]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_explicit_outflow_and_inflow_signs_are_required(string outflowAmount, string inflowAmount)
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, outflowAmount, "2026-07-01", 10);
        var inflow = await Record(second.AccountId, inflowAmount, "2026-07-01", 11);

        AssertError(await Confirm(outflow.TransactionId, inflow.TransactionId, "wrong roles", "confirm"), 3, TransferErrors.Sign);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_unequal_principal_is_rejected_atomically()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 12);
        var inflow = await Record(second.AccountId, "12.33", "2026-07-01", 13);

        AssertError(await Confirm(outflow.TransactionId, inflow.TransactionId, "unequal", "confirm"), 3, TransferErrors.Amount);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("outflow")]
    [InlineData("inflow")]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_inactive_leg_is_rejected_atomically(string inactiveLeg)
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 14);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 15);
        await Terminate(inactiveLeg == "outflow" ? outflow.TransactionId : inflow.TransactionId);

        AssertError(await Confirm(outflow.TransactionId, inflow.TransactionId, "inactive", "confirm"), 6, TransferErrors.TransactionInactive);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_archived_owned_account_is_rejected_atomically()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 16);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 17);
        await ArchiveAccount(second.AccountId);

        AssertError(await Confirm(outflow.TransactionId, inflow.TransactionId, "archived account", "confirm"), 6, "LEDGER-ACCOUNT-ARCHIVED");
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_active_roles_are_exclusive_across_relationships()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var third = await CreateAccount("Third", "3333", AccountType.Savings, "third-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 18);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 19);
        var competingInflow = await Record(third.AccountId, "12.34", "2026-07-01", 20);
        await Confirm(outflow.TransactionId, inflow.TransactionId, "first", "first");

        AssertError(await Confirm(outflow.TransactionId, competingInflow.TransactionId, "competing", "second"), 5, TransferErrors.ActiveRoleConflict);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_replays_are_stable_and_changed_replay_conflicts()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 21);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 22);
        var input = new ConfirmTransferInput(outflow.TransactionId, inflow.TransactionId, "owner confirmed");
        var original = Relationship(await Confirm(input, "same"));

        Assert.Equal(original.RelationshipId, Relationship(await Confirm(input, "same")).RelationshipId);
        Assert.Equal(original.RelationshipId, Relationship(await Confirm(input, "other")).RelationshipId);
        AssertError(await Confirm(input with { Reason = "changed" }, "same"), 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_missing_transaction_is_rejected()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 23);

        AssertError(await Confirm(outflow.TransactionId, LedgerId.New().ToString(), "missing", "confirm"), 4, TransactionErrors.NotFound);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Theory]
    [InlineData("outflow")]
    [InlineData("inflow")]
    [InlineData("same")]
    [InlineData("reason")]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_invalid_contract_is_atomic(string field)
    {
        var outflowId = LedgerId.New().ToString();
        var inflowId = field == "same" ? outflowId : LedgerId.New().ToString();
        var input = new ConfirmTransferInput(
            field == "outflow" ? "invalid" : outflowId,
            field == "inflow" ? "invalid" : inflowId,
            field == "reason" ? "" : "owner confirmed");

        AssertError(await Confirm(input, "confirm"), 3, TransferErrors.Invalid);
        Assert.Equal(0, await Count("financial_relationship"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_different_currency_policy_is_fail_closed()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 24);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 25);

        Assert.False(TransferPolicy.TryPrincipal(outflow with { CurrencyCode = "USD" }, inflow, out _, out var error));
        Assert.Equal(TransferErrors.Currency, error);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSFER_CONFIRMATION_relationship_get_returns_exact_detail_and_stable_errors()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 26);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 27);
        var created = Relationship(await Confirm(outflow.TransactionId, inflow.TransactionId, "owner confirmed", "confirm"));

        var fetched = Relationship(await GetRelationship(created.RelationshipId, true));

        Assert.Equal(
            JsonSerializer.Serialize(created, LedgerJsonContext.Default.FinancialRelationshipDetail),
            JsonSerializer.Serialize(fetched, LedgerJsonContext.Default.FinancialRelationshipDetail));
        AssertError(await GetRelationship("invalid", false), 3, TransferErrors.Invalid);
        AssertError(await GetRelationship(LedgerId.New().ToString(), false), 4, TransferErrors.RelationshipNotFound);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_confirmation_does_not_mutate_source_transactions()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 28);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 29);

        await Confirm(outflow.TransactionId, inflow.TransactionId, "owner confirmed", "confirm");

        var currentOutflow = await GetTransaction(outflow.TransactionId);
        var currentInflow = await GetTransaction(inflow.TransactionId);
        Assert.Equal(outflow.SignedAmount, currentOutflow.SignedAmount);
        Assert.Equal(inflow.SignedAmount, currentInflow.SignedAmount);
        Assert.Equal(outflow.Evidence.Single().EvidenceId, currentOutflow.Evidence.Single().EvidenceId);
        Assert.Equal(TransactionLifecycleStatus.Active, currentOutflow.LifecycleStatus);
        Assert.Empty(currentOutflow.History!.Lifecycle);
        Assert.Empty(currentInflow.History!.Lifecycle);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_relationship_rows_reject_update_and_delete()
    {
        var first = await CreateAccount("First", "1111", AccountType.Cheque, "first-account");
        var second = await CreateAccount("Second", "2222", AccountType.Savings, "second-account");
        var outflow = await Record(first.AccountId, "-12.34", "2026-07-01", 30);
        var inflow = await Record(second.AccountId, "12.34", "2026-07-01", 31);
        await Confirm(outflow.TransactionId, inflow.TransactionId, "owner confirmed", "confirm");

        await using var connection = await Open();
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE financial_relationship SET amount_minor = 1;"))).SqliteErrorCode > 0);
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM financial_relationship;"))).SqliteErrorCode > 0);
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

    private async Task<AccountDetail> CreateAccount(string name, string suffix, AccountType type, string key)
    {
        var input = new CreateAccountInput("Test Bank", name, type, "****" + suffix, "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), key), LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId) => Run(
        "ledger.account.archive",
        JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, "archive for test"), LedgerJsonContext.Default.ArchiveAccountInput),
        "archive-account");

    private async Task<TransactionDetail> Record(string accountId, string amount, string date, int digestSeed)
    {
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", date, null, "Owner-safe banking transaction", null, null,
            new(EvidenceKind.AgentCapture, Digest(digestSeed), "capture:" + digestSeed, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + digestSeed), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Confirm(string outflowId, string inflowId, string reason, string key) => Confirm(new(outflowId, inflowId, reason), key);

    private Task<ProcessResult> Confirm(ConfirmTransferInput input, string key) => Run(
        "ledger.transfer.confirm", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.ConfirmTransferInput), key);

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
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, 'void', NULL, NULL, 'test', 'system:test', $at);";
        command.Parameters.AddWithValue("$eventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ActiveRelationshipsFor(string transactionId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM financial_relationship_current WHERE state = 'active' AND (source_transaction_id = $id OR target_transaction_id = $id);";
        command.Parameters.AddWithValue("$id", transactionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", new("human", "transfer-test"), input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
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
