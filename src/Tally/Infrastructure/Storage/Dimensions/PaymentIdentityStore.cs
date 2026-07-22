using System.Globalization;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Dimensions;

namespace Tally.Infrastructure.Storage.Dimensions;

public enum PaymentIdentityKind
{
    Instrument,
    Cardholder
}

public sealed record PaymentIdentityCurrent(string Id, string LifecycleEventId, string Label, PaymentIdentityStatus Status);
public sealed record PaymentInstrumentIdentity(string? AccountId, string? MaskedSuffix);

public sealed class PaymentIdentityStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<bool> AccountIsActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        CancellationToken cancellationToken) =>
        await ExistsAsync(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM catalogue_current WHERE catalogue_kind = 'account' AND entity_id = $id AND status = 'active');",
            cancellationToken,
            ("$id", accountId));

    public async Task<bool> ActiveLabelExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityKind kind,
        string label,
        string? exceptId,
        CancellationToken cancellationToken) =>
        await ExistsAsync(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM catalogue_current WHERE catalogue_kind = $kind AND status = 'active' AND normalized_label = lower(trim($label)) AND ($except IS NULL OR entity_id <> $except));",
            cancellationToken,
            ("$kind", KindValue(kind)),
            ("$label", label),
            ("$except", exceptId));

    public async Task<bool> ActiveInstrumentIdentityExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? accountId,
        string? suffix,
        string? exceptId,
        CancellationToken cancellationToken) =>
        suffix is not null && await ExistsAsync(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM payment_instrument JOIN catalogue_current ON catalogue_kind = 'payment_instrument' AND entity_id = instrument_id AND status = 'active' WHERE account_id IS $account AND masked_suffix = $suffix AND ($except IS NULL OR instrument_id <> $except));",
            cancellationToken,
            ("$account", accountId),
            ("$suffix", suffix),
            ("$except", exceptId));

    public async Task<PaymentInstrumentIdentity?> GetInstrumentIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrumentId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT account_id, masked_suffix FROM payment_instrument WHERE instrument_id = $id;", ("$id", instrumentId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(Optional(reader, 0), Optional(reader, 1)) : null;
    }

    public async Task<bool> InstrumentAssociationIsActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrumentId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT account_id FROM payment_instrument WHERE instrument_id = $id;", ("$id", instrumentId));
        var accountId = await command.ExecuteScalarAsync(cancellationToken);
        return accountId is null or DBNull
            || await AccountIsActiveAsync(connection, transaction, Convert.ToString(accountId, CultureInfo.InvariantCulture)!, cancellationToken);
    }

    public async Task<string?> ActiveAttributionErrorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityKind kind,
        string id,
        CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(connection, transaction, kind, id, cancellationToken);
        return current switch
        {
            null => kind == PaymentIdentityKind.Instrument ? "LEDGER-PAYMENT-INSTRUMENT-NOT-FOUND" : "LEDGER-CARDHOLDER-NOT-FOUND",
            { Status: PaymentIdentityStatus.Archived } => kind == PaymentIdentityKind.Instrument ? "LEDGER-PAYMENT-INSTRUMENT-ARCHIVED" : "LEDGER-CARDHOLDER-ARCHIVED",
            _ when kind == PaymentIdentityKind.Instrument && !await InstrumentAssociationIsActiveAsync(connection, transaction, id, cancellationToken) => "LEDGER-PAYMENT-INSTRUMENT-ACCOUNT-NOT-ACTIVE",
            _ => null
        };
    }

    public async Task InsertInstrumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string eventId,
        string label,
        string? accountId,
        string? suffix,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO payment_instrument (instrument_id, account_id, masked_suffix, created_at)
            VALUES ($id, $account, $suffix, $occurredAt);
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'payment_instrument', $id, 'create', NULL, $label, lower(trim($label)), NULL, $actor, $occurredAt, NULL);
            """,
            ("$id", id),
            ("$account", accountId),
            ("$suffix", suffix),
            ("$occurredAt", occurredAt),
            ("$eventId", eventId),
            ("$label", label),
            ("$actor", actor));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertCardholderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string eventId,
        string label,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO cardholder (cardholder_id, created_at) VALUES ($id, $occurredAt);
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, 'cardholder', $id, 'create', NULL, $label, lower(trim($label)), NULL, $actor, $occurredAt, NULL);
            """,
            ("$id", id),
            ("$occurredAt", occurredAt),
            ("$eventId", eventId),
            ("$label", label),
            ("$actor", actor));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentIdentityCurrent?> FindCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityKind kind,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            "SELECT entity_id, lifecycle_event_id, label, status FROM catalogue_current WHERE catalogue_kind = $kind AND entity_id = $id;",
            ("$kind", KindValue(kind)),
            ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseStatus(reader.GetString(3)))
            : null;
    }

    public async Task AppendLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityKind kind,
        string eventId,
        PaymentIdentityCurrent current,
        PaymentIdentityLifecycleAction action,
        string? newLabel,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        var resultingLabel = newLabel ?? (action == PaymentIdentityLifecycleAction.Reactivate ? current.Label : null);
        await using var command = Command(connection, transaction, """
            INSERT INTO catalogue_lifecycle_event (
                lifecycle_event_id, catalogue_kind, entity_id, action, previous_label, new_label,
                normalized_label, reason, actor, occurred_at, previous_event_id)
            VALUES ($eventId, $kind, $id, $action, $previousLabel, $newLabel,
                    lower(trim(COALESCE($newLabel, $previousLabel))), $reason, $actor, $occurredAt, $previousEventId);
            """,
            ("$eventId", eventId),
            ("$kind", KindValue(kind)),
            ("$id", current.Id),
            ("$action", ActionValue(action)),
            ("$previousLabel", current.Label),
            ("$newLabel", resultingLabel),
            ("$reason", reason),
            ("$actor", actor),
            ("$occurredAt", occurredAt),
            ("$previousEventId", current.LifecycleEventId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentInstrumentDetail?> GetInstrumentAsync(string id, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetInstrumentAsync(connection, null, id, includeHistory, cancellationToken);
    }

    public async Task<PaymentInstrumentDetail?> GetInstrumentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT instrument.instrument_id, current.label, instrument.account_id, instrument.masked_suffix,
                   current.status, created.actor, instrument.created_at
            FROM payment_instrument AS instrument
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'payment_instrument' AND current.entity_id = instrument.instrument_id
            JOIN catalogue_lifecycle_event AS created
              ON created.catalogue_kind = 'payment_instrument' AND created.entity_id = instrument.instrument_id AND created.action = 'create'
            WHERE instrument.instrument_id = $id;
            """, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new PaymentInstrumentDetail(
            reader.GetString(0), reader.GetString(1), Optional(reader, 2), Optional(reader, 3),
            ParseStatus(reader.GetString(4)), reader.GetString(5), reader.GetString(6), []);
        await reader.DisposeAsync();
        return includeHistory
            ? detail with { LifecycleHistory = await HistoryAsync(connection, transaction, PaymentIdentityKind.Instrument, id, cancellationToken) }
            : detail;
    }

    public async Task<CardholderDetail?> GetCardholderAsync(string id, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetCardholderAsync(connection, null, id, includeHistory, cancellationToken);
    }

    public async Task<CardholderDetail?> GetCardholderAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT cardholder.cardholder_id, current.label, current.status, created.actor, cardholder.created_at
            FROM cardholder
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'cardholder' AND current.entity_id = cardholder.cardholder_id
            JOIN catalogue_lifecycle_event AS created
              ON created.catalogue_kind = 'cardholder' AND created.entity_id = cardholder.cardholder_id AND created.action = 'create'
            WHERE cardholder.cardholder_id = $id;
            """, ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new CardholderDetail(
            reader.GetString(0), reader.GetString(1), ParseStatus(reader.GetString(2)), reader.GetString(3), reader.GetString(4), []);
        await reader.DisposeAsync();
        return includeHistory
            ? detail with { LifecycleHistory = await HistoryAsync(connection, transaction, PaymentIdentityKind.Cardholder, id, cancellationToken) }
            : detail;
    }

    public async Task<IReadOnlyList<PaymentInstrumentDetail>> ListInstrumentsAsync(
        PaymentIdentityStatus? status,
        string? accountId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var command = Command(connection, null, """
            SELECT instrument.instrument_id
            FROM payment_instrument AS instrument
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'payment_instrument' AND current.entity_id = instrument.instrument_id
            WHERE ($status IS NULL OR current.status = $status)
              AND ($account IS NULL OR instrument.account_id = $account)
            ORDER BY lower(current.label), instrument.instrument_id;
            """,
            ("$status", status is null ? null : StatusValue(status.Value)),
            ("$account", accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var items = new List<PaymentInstrumentDetail>();
        foreach (var id in ids) items.Add((await GetInstrumentAsync(connection, null, id, false, cancellationToken))!);
        return items;
    }

    public async Task<IReadOnlyList<CardholderDetail>> ListCardholdersAsync(PaymentIdentityStatus? status, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var command = Command(connection, null, """
            SELECT cardholder.cardholder_id
            FROM cardholder
            JOIN catalogue_current AS current
              ON current.catalogue_kind = 'cardholder' AND current.entity_id = cardholder.cardholder_id
            WHERE ($status IS NULL OR current.status = $status)
            ORDER BY lower(current.label), cardholder.cardholder_id;
            """, ("$status", status is null ? null : StatusValue(status.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var items = new List<CardholderDetail>();
        foreach (var id in ids) items.Add((await GetCardholderAsync(connection, null, id, false, cancellationToken))!);
        return items;
    }

    private static async Task<IReadOnlyList<PaymentIdentityHistoryItem>> HistoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PaymentIdentityKind kind,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT lifecycle_event_id, action, previous_label, new_label, reason, actor, occurred_at, previous_event_id
            FROM catalogue_lifecycle_event
            WHERE catalogue_kind = $kind AND entity_id = $id
            ORDER BY occurred_at, lifecycle_event_id;
            """,
            ("$kind", KindValue(kind)),
            ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PaymentIdentityHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(
                reader.GetString(0), ParseAction(reader.GetString(1)), Optional(reader, 2), Optional(reader, 3),
                Optional(reader, 4), reader.GetString(5), reader.GetString(6), Optional(reader, 7)));
        }

        return items;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string KindValue(PaymentIdentityKind kind) => kind == PaymentIdentityKind.Instrument ? "payment_instrument" : "cardholder";
    private static string StatusValue(PaymentIdentityStatus status) => status == PaymentIdentityStatus.Active ? "active" : "archived";
    private static PaymentIdentityStatus ParseStatus(string status) => status switch { "active" => PaymentIdentityStatus.Active, "archived" => PaymentIdentityStatus.Archived, _ => throw new InvalidOperationException("Unknown payment identity status.") };
    private static string ActionValue(PaymentIdentityLifecycleAction action) => action.ToString().ToLowerInvariant();
    private static PaymentIdentityLifecycleAction ParseAction(string action) => action switch
    {
        "create" => PaymentIdentityLifecycleAction.Create,
        "rename" => PaymentIdentityLifecycleAction.Rename,
        "archive" => PaymentIdentityLifecycleAction.Archive,
        "reactivate" => PaymentIdentityLifecycleAction.Reactivate,
        _ => throw new InvalidOperationException("Unknown payment identity lifecycle action.")
    };
}
