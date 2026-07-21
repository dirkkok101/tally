using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ledger.Accounts;

namespace Tally.Infrastructure.Storage.Accounts;

public sealed record AccountCurrentState(string AccountId, string LifecycleEventId, string DisplayName, AccountStatus Status);

public sealed class AccountStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public const string NotFoundError = "LEDGER-ACCOUNT-NOT-FOUND";
    public const string ArchivedError = "LEDGER-ACCOUNT-ARCHIVED";

    public async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string lifecycleEventId,
        AccountDefinition account,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO account (account_id, institution_name, account_type, account_class, masked_identifier, currency_code, created_at)
            VALUES ($id, $institution, $type, $class, $masked, $currency, $occurredAt);
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'account', $id, 'create', NULL, $displayName, lower(trim($displayName)), NULL, $actor, $occurredAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$institution", account.InstitutionName);
        command.Parameters.AddWithValue("$type", TypeValue(account.AccountType));
        command.Parameters.AddWithValue("$class", ClassValue(account.AccountClass));
        command.Parameters.AddWithValue("$masked", account.MaskedIdentifier);
        command.Parameters.AddWithValue("$currency", account.CurrencyCode);
        command.Parameters.AddWithValue("$occurredAt", occurredAt);
        command.Parameters.AddWithValue("$eventId", lifecycleEventId);
        command.Parameters.AddWithValue("$displayName", account.DisplayName);
        command.Parameters.AddWithValue("$actor", actor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        AccountCurrentState current,
        AccountLifecycleAction action,
        string? newDisplayName,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'account', $accountId, $action, $previousName, $newName,
                    lower(trim(COALESCE($newName, $previousName))), $reason, $actor, $occurredAt, $previousEventId);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$accountId", current.AccountId);
        command.Parameters.AddWithValue("$action", ActionValue(action));
        command.Parameters.AddWithValue("$previousName", current.DisplayName);
        command.Parameters.AddWithValue("$newName", newDisplayName is null ? DBNull.Value : newDisplayName);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$occurredAt", occurredAt);
        command.Parameters.AddWithValue("$previousEventId", current.LifecycleEventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountCurrentState?> FindCurrentAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entity_id, lifecycle_event_id, label, status
            FROM catalogue_current
            WHERE catalogue_kind = 'account' AND entity_id = $id;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseStatus(reader.GetString(3)))
            : null;
    }

    public async Task<bool> ActiveIdentityExistsAsync(SqliteConnection connection, SqliteTransaction transaction, AccountDefinition account, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM account
                JOIN catalogue_current ON catalogue_kind = 'account' AND entity_id = account_id AND status = 'active'
                WHERE lower(trim(institution_name)) = lower(trim($institution))
                  AND lower(trim(masked_identifier)) = lower(trim($masked)));
            """;
        command.Parameters.AddWithValue("$institution", account.InstitutionName);
        command.Parameters.AddWithValue("$masked", account.MaskedIdentifier);
        return await ExistsAsync(command, cancellationToken);
    }

    public async Task<bool> ActiveNameExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string displayName, string? exceptAccountId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM catalogue_current
                WHERE catalogue_kind = 'account' AND status = 'active'
                  AND normalized_label = lower(trim($name))
                  AND ($exceptId IS NULL OR entity_id <> $exceptId));
            """;
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$exceptId", exceptAccountId is null ? DBNull.Value : exceptAccountId);
        return await ExistsAsync(command, cancellationToken);
    }

    public async Task<string?> ActiveWriteErrorAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(connection, transaction, accountId, cancellationToken);
        return current switch
        {
            null => NotFoundError,
            { Status: AccountStatus.Archived } => ArchivedError,
            _ => null
        };
    }

    public async Task<AccountDetail?> GetAsync(string accountId, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, accountId, includeHistory, cancellationToken);
    }

    public async Task<AccountDetail?> GetAsync(SqliteConnection connection, SqliteTransaction? transaction, string accountId, bool includeHistory, CancellationToken cancellationToken)
    {
        var detail = await ReadDetailAsync(connection, transaction, accountId, cancellationToken);
        if (detail is null || !includeHistory) return detail;
        return detail with { LifecycleHistory = await HistoryAsync(connection, transaction, accountId, cancellationToken) };
    }

    public async Task<IReadOnlyList<AccountSummary>> ListAsync(AccountStatus? status, string? institutionName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account.account_id, account.institution_name, current.label, account.account_type,
                   account.account_class, account.masked_identifier, account.currency_code, current.status
            FROM account
            JOIN catalogue_current AS current ON current.catalogue_kind = 'account' AND current.entity_id = account.account_id
            WHERE ($status IS NULL OR current.status = $status)
              AND ($institution IS NULL OR lower(trim(account.institution_name)) = lower(trim($institution)))
            ORDER BY lower(account.institution_name), lower(current.label), account.account_id;
            """;
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : StatusValue(status.Value));
        command.Parameters.AddWithValue("$institution", institutionName is null ? DBNull.Value : institutionName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accounts = new List<AccountSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseType(reader.GetString(3)),
                ParseClass(reader.GetString(4)), reader.GetString(5), reader.GetString(6), ParseStatus(reader.GetString(7))));
        }

        return accounts;
    }

    private static async Task<AccountDetail?> ReadDetailAsync(SqliteConnection connection, SqliteTransaction? transaction, string accountId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account.account_id, account.institution_name, current.label, account.account_type,
                   account.account_class, account.masked_identifier, account.currency_code, current.status,
                   created.actor, account.created_at,
                   CASE WHEN current.status = 'archived' THEN current.changed_at ELSE NULL END
            FROM account
            JOIN catalogue_current AS current ON current.catalogue_kind = 'account' AND current.entity_id = account.account_id
            JOIN catalogue_lifecycle_event AS created
              ON created.catalogue_kind = 'account' AND created.entity_id = account.account_id AND created.action = 'create'
            WHERE account.account_id = $id;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseType(reader.GetString(3)),
                ParseClass(reader.GetString(4)), reader.GetString(5), reader.GetString(6), ParseStatus(reader.GetString(7)),
                reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), [])
            : null;
    }

    private static async Task<IReadOnlyList<AccountLifecycleHistoryItem>> HistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string accountId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lifecycle_event_id, action, previous_label, new_label, reason, actor, occurred_at, previous_event_id
            FROM catalogue_lifecycle_event
            WHERE catalogue_kind = 'account' AND entity_id = $id
            ORDER BY occurred_at, lifecycle_event_id;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<AccountLifecycleHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new(
                reader.GetString(0), ParseAction(reader.GetString(1)), OptionalString(reader, 2), OptionalString(reader, 3),
                OptionalString(reader, 4), reader.GetString(5), reader.GetString(6), OptionalString(reader, 7)));
        }

        return history;
    }

    private static async Task<bool> ExistsAsync(SqliteCommand command, CancellationToken cancellationToken) =>
        Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;

    private static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string TypeValue(AccountType value) => value switch { AccountType.Cheque => "cheque", AccountType.Savings => "savings", AccountType.CreditCard => "credit_card", AccountType.OtherAsset => "other_asset", AccountType.OtherLiability => "other_liability", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static AccountType ParseType(string value) => value switch { "cheque" => AccountType.Cheque, "savings" => AccountType.Savings, "credit_card" => AccountType.CreditCard, "other_asset" => AccountType.OtherAsset, "other_liability" => AccountType.OtherLiability, _ => throw new InvalidOperationException("Stored account type is invalid.") };
    private static string ClassValue(AccountClass value) => value == AccountClass.Asset ? "asset" : "liability";
    private static AccountClass ParseClass(string value) => value == "asset" ? AccountClass.Asset : value == "liability" ? AccountClass.Liability : throw new InvalidOperationException("Stored account class is invalid.");
    private static string StatusValue(AccountStatus value) => value == AccountStatus.Active ? "active" : "archived";
    private static AccountStatus ParseStatus(string value) => value == "active" ? AccountStatus.Active : value == "archived" ? AccountStatus.Archived : throw new InvalidOperationException("Stored account status is invalid.");
    private static string ActionValue(AccountLifecycleAction value) => value == AccountLifecycleAction.Rename ? "rename" : value == AccountLifecycleAction.Archive ? "archive" : "create";
    private static AccountLifecycleAction ParseAction(string value) => value == "create" ? AccountLifecycleAction.Create : value == "rename" ? AccountLifecycleAction.Rename : value == "archive" ? AccountLifecycleAction.Archive : throw new InvalidOperationException("Stored account lifecycle action is invalid.");
}
