using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class AccountStoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-account-store-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;
    private AccountStore store = null!;

    // DM-LEDGER-ACCOUNT
    [Fact]
    public async Task Active_write_guard_distinguishes_missing_active_and_archived_accounts()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();
        Assert.Equal(AccountStore.NotFoundError, await store.ActiveWriteErrorAsync(connection, transaction, LedgerId.New().ToString(), CancellationToken.None));
        var account = await InsertAsync(connection, transaction);
        Assert.Null(await store.ActiveWriteErrorAsync(connection, transaction, account.AccountId, CancellationToken.None));
        await store.AppendLifecycleAsync(connection, transaction, LedgerId.New().ToString(), account, AccountLifecycleAction.Archive, null, "Closed", "owner", At(1), CancellationToken.None);
        Assert.Equal(AccountStore.ArchivedError, await store.ActiveWriteErrorAsync(connection, transaction, account.AccountId, CancellationToken.None));
        await transaction.CommitAsync();
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Account_fact_and_lifecycle_history_reject_update_and_delete()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var account = await InsertAsync(connection, transaction);
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE account SET institution_name = 'Changed' WHERE account_id = $id;", account.AccountId));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM account WHERE account_id = $id;", account.AccountId));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM catalogue_lifecycle_event WHERE entity_id = $id;", account.AccountId));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Current_projection_keeps_one_row_after_multiple_lifecycle_events()
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var account = await InsertAsync(connection, transaction);
        var renameId = LedgerId.New().ToString();
        await store.AppendLifecycleAsync(connection, transaction, renameId, account, AccountLifecycleAction.Rename, "Renamed", "Clearer", "owner", At(1), CancellationToken.None);
        var renamed = (await store.FindCurrentAsync(connection, transaction, account.AccountId, CancellationToken.None))!;
        await store.AppendLifecycleAsync(connection, transaction, LedgerId.New().ToString(), renamed, AccountLifecycleAction.Archive, null, "Closed", "owner", At(2), CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM catalogue_current WHERE catalogue_kind = 'account';"));
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM catalogue_lifecycle_event WHERE catalogue_kind = 'account';"));
        var detail = (await store.GetAsync(account.AccountId, true, CancellationToken.None))!;
        Assert.Equal(AccountStatus.Archived, detail.Status);
        Assert.Equal("Renamed", detail.DisplayName);
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Fact]
    public async Task Account_storage_columns_exclude_credentials_and_full_identifiers()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_xinfo('account') ORDER BY cid;";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        Assert.Equal(["account_id", "institution_name", "account_type", "account_class", "masked_identifier", "currency_code", "created_at"], columns);
        Assert.DoesNotContain(columns, column => column.Contains("credential", StringComparison.OrdinalIgnoreCase) || column.Contains("full", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        store = new(database, new(protection));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<AccountCurrentState> InsertAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        var accountId = LedgerId.New().ToString();
        var eventId = LedgerId.New().ToString();
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Daily", AccountType.Cheque, "****1234", "ZAR"), out var account, out _));
        await store.InsertAsync(connection, transaction, accountId, eventId, account!, "owner", At(0), CancellationToken.None);
        return (await store.FindCurrentAsync(connection, transaction, accountId, CancellationToken.None))!;
    }

    private async Task<SqliteConnection> OpenAsync() =>
        await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, string accountId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", accountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string At(int second) => $"2026-07-21T00:00:{second:D2}Z";
}
