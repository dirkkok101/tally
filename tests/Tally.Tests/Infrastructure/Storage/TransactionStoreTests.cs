using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class TransactionStoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-transaction-store-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private TransactionStore store = null!;
    private EvidenceStore evidenceStore = null!;
    private AccountStore accountStore = null!;

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_FACT_insert_and_get_round_trip_exact_minor_units_and_dates()
    {
        var accountId = await SeedAccount();
        var fact = Fact(accountId, amount: long.MinValue, postingDate: "2026-07-03");
        var transactionId = await Insert(fact);

        var detail = (await store.GetAsync(transactionId, false, CancellationToken.None))!;

        Assert.Equal("-92233720368547758.08", detail.SignedAmount);
        Assert.Equal("2026-07-01", detail.TransactionDate);
        Assert.Equal("2026-07-03", detail.PostingDate);
        Assert.Equal("2026-07-01", detail.EffectiveDate);
    }

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_CONTRACTS_defaults_are_one_unknown_attribution_and_one_unassigned_pool()
    {
        var transactionId = await Insert(Fact(await SeedAccount()));

        var detail = (await store.GetAsync(transactionId, true, CancellationToken.None))!;

        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.CardholderState);
        Assert.Equal(TransactionPoolState.Unassigned, detail.Pool.State);
        Assert.Equal(TransactionCategoryState.Uncategorized, detail.Category.State);
        Assert.Single(detail.History!.PaymentAttribution);
        Assert.Single(detail.History.PoolAssignments);
        Assert.Empty(detail.History.CategoryAssignments);
    }

    [Fact]
    public async Task DM_LEDGER_EVIDENCE_RECORD_LINK_initial_evidence_and_supporting_link_round_trip()
    {
        var fact = Fact(await SeedAccount());
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await InsertDefaults(connection, transaction, transactionId, fact);
        var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, fact.EvidenceIdentity, fact.InitialEvidence, "actor", At(0), CancellationToken.None);
        await store.InsertInitialEvidenceLinkAsync(connection, transaction, LedgerId.New().ToString(), evidence.EvidenceId, transactionId, "actor", At(0), CancellationToken.None);
        await transaction.CommitAsync();

        var detail = (await store.GetAsync(transactionId, false, CancellationToken.None))!;

        var linked = Assert.Single(detail.Evidence);
        Assert.Equal(evidence.EvidenceId, linked.EvidenceId);
        Assert.Equal(EvidenceLinkRole.Supporting, linked.Role);
    }

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_FACT_duplicate_transaction_id_is_rejected_without_second_defaults()
    {
        var fact = Fact(await SeedAccount());
        var transactionId = await Insert(fact);
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();

        await Assert.ThrowsAsync<SqliteException>(() => InsertDefaults(connection, transaction, transactionId, fact));
        await transaction.RollbackAsync();

        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(1, await Count("transaction_attribution_event"));
        Assert.Equal(1, await Count("pool_assignment_event"));
    }

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_FACT_missing_account_is_rejected_by_real_sqlite()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();

        await Assert.ThrowsAsync<SqliteException>(() => InsertDefaults(connection, transaction, LedgerId.New().ToString(), Fact(LedgerId.New().ToString())));
        await transaction.RollbackAsync();

        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_FACT_archived_account_is_rejected_by_real_sqlite()
    {
        var accountId = await SeedAccount();
        await ArchiveAccount(accountId);
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();

        await Assert.ThrowsAsync<SqliteException>(() => InsertDefaults(connection, transaction, LedgerId.New().ToString(), Fact(accountId)));
        await transaction.RollbackAsync();

        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_transaction_fact_rejects_update_and_delete()
    {
        var transactionId = await Insert(Fact(await SeedAccount()));
        await using var connection = await Open();

        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE transaction_fact SET original_description = 'Changed' WHERE transaction_id = $id;", transactionId));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM transaction_fact WHERE transaction_id = $id;", transactionId));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_default_dimension_events_reject_update_and_delete()
    {
        var transactionId = await Insert(Fact(await SeedAccount()));
        await using var connection = await Open();

        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE transaction_attribution_event SET reason = 'Changed' WHERE transaction_id = $id;", transactionId));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM pool_assignment_event WHERE transaction_id = $id;", transactionId));
    }

    [Fact]
    public async Task DM_LEDGER_TRANSACTION_CONTRACTS_get_missing_returns_null_without_mutation()
    {
        Assert.Null(await store.GetAsync(LedgerId.New().ToString(), true, CancellationToken.None));
        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task DM_LEDGER_IDEMPOTENCY_RECORD_crash_after_fact_and_defaults_rolls_back_every_effect()
    {
        var fact = Fact(await SeedAccount());
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var request = Request(fact, "crash-fact");

        await Assert.ThrowsAsync<InjectedFailure>(() => executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            await InsertDefaults(connection, transaction, LedgerId.New().ToString(), fact);
            throw new InjectedFailure();
        }, CancellationToken.None));

        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(0, await Count("transaction_attribution_event"));
        Assert.Equal(0, await Count("pool_assignment_event"));
        Assert.Equal(0, await Count("idempotency_record"));
    }

    [Fact]
    public async Task DM_LEDGER_IDEMPOTENCY_RECORD_crash_after_evidence_rolls_back_fact_evidence_and_defaults()
    {
        var fact = Fact(await SeedAccount());
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var request = Request(fact, "crash-evidence");

        await Assert.ThrowsAsync<InjectedFailure>(() => executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var transactionId = LedgerId.New().ToString();
            await InsertDefaults(connection, transaction, transactionId, fact);
            await evidenceStore.RegisterInitialAsync(connection, transaction, fact.EvidenceIdentity, fact.InitialEvidence, "actor", At(0), token);
            throw new InjectedFailure();
        }, CancellationToken.None));

        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(0, await Count("evidence_record"));
        Assert.Equal(0, await Count("idempotency_record"));
    }

    [Fact]
    public async Task DM_LEDGER_IDEMPOTENCY_RECORD_complete_mutation_commits_fact_evidence_link_defaults_and_replay_together()
    {
        var fact = Fact(await SeedAccount());
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var request = Request(fact, "complete");
        var transactionId = LedgerId.New().ToString();

        async Task<CommandResult<JsonElement>> Mutation(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        {
            await InsertDefaults(connection, transaction, transactionId, fact);
            var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, fact.EvidenceIdentity, fact.InitialEvidence, "actor", At(0), token);
            await store.InsertInitialEvidenceLinkAsync(connection, transaction, LedgerId.New().ToString(), evidence.EvidenceId, transactionId, "actor", At(0), token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId), LedgerJsonContext.Default.GetTransactionInput));
        }

        var first = await executor.ExecuteAsync(request, Mutation, CancellationToken.None);
        var replay = await executor.ExecuteAsync(request, Mutation, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value!.GetRawText(), replay.Value!.GetRawText());
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(1, await Count("evidence_record"));
        Assert.Equal(1, await Count("evidence_link_event"));
        Assert.Equal(1, await Count("idempotency_record"));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(protection);
        store = new(database, factory);
        evidenceStore = new(database, factory);
        accountStore = new(database, factory);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<string> SeedAccount()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var accountId = LedgerId.New().ToString();
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Daily", AccountType.Cheque, "****1234", "ZAR"), out var account, out _));
        await accountStore.InsertAsync(connection, transaction, accountId, LedgerId.New().ToString(), account!, "actor", At(0), CancellationToken.None);
        await transaction.CommitAsync();
        return accountId;
    }

    private async Task ArchiveAccount(string accountId)
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var current = (await accountStore.FindCurrentAsync(connection, transaction, accountId, CancellationToken.None))!;
        await accountStore.AppendLifecycleAsync(connection, transaction, LedgerId.New().ToString(), current, AccountLifecycleAction.Archive, null, "closed", "actor", At(1), CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static TransactionFact Fact(string accountId, long amount = -1234, string? postingDate = null)
    {
        var input = new RecordTransactionInput(
            accountId,
            Money.FromMinorUnits(amount).ToString(),
            "ZAR",
            "2026-07-01",
            postingDate,
            "Owner-safe purchase",
            null,
            null,
            new(EvidenceKind.AgentCapture, new string('a', 64), "capture:one", null, null));
        Assert.True(TransactionFact.TryCreate(input, out var fact, out _));
        return fact!;
    }

    private async Task<string> Insert(TransactionFact fact)
    {
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await InsertDefaults(connection, transaction, transactionId, fact);
        await transaction.CommitAsync();
        return transactionId;
    }

    private Task InsertDefaults(SqliteConnection connection, SqliteTransaction transaction, string transactionId, TransactionFact fact) =>
        store.InsertFactAndDefaultsAsync(
            connection, transaction, transactionId, LedgerId.New().ToString(), null, LedgerId.New().ToString(),
            fact, At(0), "ubuntu", "actor", CancellationToken.None);

    private static IdempotencyRequest Request(TransactionFact fact, string key) => new(
        "1.0",
        "ledger.transaction.record",
        key,
        "human:store-test",
        JsonSerializer.SerializeToElement(fact.CanonicalInput(), LedgerJsonContext.Default.RecordTransactionInput),
        new LogicalEffectIdentity("transaction-evidence:" + fact.EvidenceIdentity.LogicalIdentityDigest, "transaction_record"));

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql, string transactionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", transactionId);
        await command.ExecuteNonQueryAsync();
    }

    private static string At(int second) => $"2026-07-21T00:00:{second:D2}Z";
    private sealed class InjectedFailure : Exception;
}
