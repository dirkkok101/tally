using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Evidence;

namespace Tally.Infrastructure.Storage.Evidence;

public sealed class EvidenceStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<EvidenceRecordDetail> RegisterInitialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceIdentity identity,
        RegisterEvidenceInput input,
        string actor,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        var evidenceId = LedgerId.New().ToString();
        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO evidence_record (
                    evidence_id, kind, logical_identity_digest, opaque_external_reference,
                    content_fingerprint, recorded_by, recorded_at)
                VALUES ($id, $kind, $digest, $reference, $fingerprint, $actor, $recordedAt);
                """;
            record.Parameters.AddWithValue("$id", evidenceId);
            record.Parameters.AddWithValue("$kind", KindValue(input.Kind));
            record.Parameters.AddWithValue("$digest", identity.LogicalIdentityDigest);
            record.Parameters.AddWithValue("$reference", DbValue(input.OpaqueExternalReference));
            record.Parameters.AddWithValue("$fingerprint", DbValue(input.ContentFingerprint));
            record.Parameters.AddWithValue("$actor", actor);
            record.Parameters.AddWithValue("$recordedAt", recordedAt);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        if (input.Observation is not null)
        {
            await using var observation = connection.CreateCommand();
            observation.Transaction = transaction;
            observation.CommandText = """
                INSERT INTO evidence_observation (
                    evidence_id, account_id, signed_amount_minor, currency_code, transaction_date,
                    posting_date, instrument_id, cardholder_id, description_fingerprint)
                VALUES ($id, $account, $amount, $currency, $transactionDate, $postingDate, $instrument, $cardholder, $description);
                """;
            observation.Parameters.AddWithValue("$id", evidenceId);
            observation.Parameters.AddWithValue("$account", DbValue(input.Observation.AccountId));
            observation.Parameters.AddWithValue("$amount", DbValue(input.Observation.SignedAmountMinor));
            observation.Parameters.AddWithValue("$currency", DbValue(input.Observation.CurrencyCode));
            observation.Parameters.AddWithValue("$transactionDate", DbValue(input.Observation.TransactionDate));
            observation.Parameters.AddWithValue("$postingDate", DbValue(input.Observation.PostingDate));
            observation.Parameters.AddWithValue("$instrument", DbValue(input.Observation.InstrumentId));
            observation.Parameters.AddWithValue("$cardholder", DbValue(input.Observation.CardholderId));
            observation.Parameters.AddWithValue("$description", DbValue(input.Observation.DescriptionFingerprint));
            await observation.ExecuteNonQueryAsync(cancellationToken);
        }

        return new(
            evidenceId,
            input.Kind,
            input.LogicalIdentityDigest,
            input.OpaqueExternalReference,
            input.ContentFingerprint,
            input.Observation,
            actor,
            recordedAt,
            []);
    }

    public async Task<EvidenceRecordDetail?> GetAsync(string evidenceId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, evidenceId, true, cancellationToken);
    }

    public async Task<EvidenceRecordDetail?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evidenceId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT record.evidence_id, record.kind, record.logical_identity_digest,
                   record.opaque_external_reference, record.content_fingerprint,
                   record.recorded_by, record.recorded_at,
                   observation.account_id, observation.signed_amount_minor, observation.currency_code,
                   observation.transaction_date, observation.posting_date, observation.instrument_id,
                   observation.cardholder_id, observation.description_fingerprint
            FROM evidence_record AS record
            LEFT JOIN evidence_observation AS observation ON observation.evidence_id = record.evidence_id
            WHERE record.evidence_id = $id;
            """;
        command.Parameters.AddWithValue("$id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new EvidenceRecordDetail(
            reader.GetString(0),
            ParseKind(reader.GetString(1)),
            reader.GetString(2),
            OptionalString(reader, 3),
            OptionalString(reader, 4),
            reader.IsDBNull(7) && reader.IsDBNull(8) && reader.IsDBNull(9) && reader.IsDBNull(10) && reader.IsDBNull(11) && reader.IsDBNull(12) && reader.IsDBNull(13) && reader.IsDBNull(14)
                ? null
                : new(OptionalString(reader, 7), OptionalLong(reader, 8), OptionalString(reader, 9), OptionalString(reader, 10), OptionalString(reader, 11), OptionalString(reader, 12), OptionalString(reader, 13), OptionalString(reader, 14)),
            reader.GetString(5),
            reader.GetString(6),
            []);
        await reader.DisposeAsync();
        return includeHistory
            ? detail with { LinkHistory = await LinkHistoryAsync(connection, transaction, evidenceId, cancellationToken) }
            : detail;
    }

    public async Task<IReadOnlyList<EvidenceLinkHistoryItem>> CurrentLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT link_event_id, transaction_id, role, action, decision_id, reason, recorded_by, recorded_at, previous_link_event_id
            FROM evidence_link_event AS link
            WHERE evidence_id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM evidence_link_event AS successor
                  WHERE successor.previous_link_event_id = link.link_event_id)
            ORDER BY recorded_at, link_event_id;
            """;
        command.Parameters.AddWithValue("$id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var links = new List<EvidenceLinkHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) links.Add(ReadLink(reader));
        return links;
    }

    public async Task AppendSupportingLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string linkEventId,
        string evidenceId,
        string transactionId,
        string reason,
        string actor,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evidence_link_event (
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $transactionId, 'supporting', 'link', NULL,
                    $reason, $actor, $recordedAt, NULL);
            """;
        command.Parameters.AddWithValue("$linkId", linkEventId);
        command.Parameters.AddWithValue("$evidenceId", evidenceId);
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$recordedAt", recordedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ObservationReferencesExistAsync(SqliteConnection connection, SqliteTransaction transaction, EvidenceObservation? observation, CancellationToken cancellationToken)
    {
        if (observation is null) return true;
        return await ExistsAsync(connection, transaction, "account", "account_id", observation.AccountId, cancellationToken)
            && await ExistsAsync(connection, transaction, "payment_instrument", "instrument_id", observation.InstrumentId, cancellationToken)
            && await ExistsAsync(connection, transaction, "cardholder", "cardholder_id", observation.CardholderId, cancellationToken);
    }

    private static async Task<IReadOnlyList<EvidenceLinkHistoryItem>> LinkHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string evidenceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT link_event_id, transaction_id, role, action, decision_id, reason, recorded_by, recorded_at, previous_link_event_id
            FROM evidence_link_event
            WHERE evidence_id = $id
            ORDER BY recorded_at, link_event_id;
            """;
        command.Parameters.AddWithValue("$id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<EvidenceLinkHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) history.Add(ReadLink(reader));

        return history;
    }

    private static EvidenceLinkHistoryItem ReadLink(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), ParseRole(reader.GetString(2)), ParseAction(reader.GetString(3)),
        OptionalString(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7), OptionalString(reader, 8));

    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string? value, CancellationToken cancellationToken)
    {
        if (value is null) return true;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {column} = $id);";
        command.Parameters.AddWithValue("$id", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static string KindValue(EvidenceKind kind) => kind switch
    {
        EvidenceKind.AgentCapture => "agent_capture",
        EvidenceKind.StatementRow => "statement_row",
        EvidenceKind.Receipt => "receipt",
        EvidenceKind.ExternalDocument => "external_document",
        EvidenceKind.OwnerAssertion => "owner_assertion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static EvidenceKind ParseKind(string value) => value switch
    {
        "agent_capture" => EvidenceKind.AgentCapture,
        "statement_row" => EvidenceKind.StatementRow,
        "receipt" => EvidenceKind.Receipt,
        "external_document" => EvidenceKind.ExternalDocument,
        "owner_assertion" => EvidenceKind.OwnerAssertion,
        _ => throw new InvalidOperationException("Stored evidence kind is invalid.")
    };

    private static EvidenceLinkRole ParseRole(string value) => value switch
    {
        "supporting" => EvidenceLinkRole.Supporting,
        "confirming" => EvidenceLinkRole.Confirming,
        _ => throw new InvalidOperationException("Stored evidence role is invalid.")
    };

    private static EvidenceLinkAction ParseAction(string value) => value switch
    {
        "link" => EvidenceLinkAction.Link,
        "revoke" => EvidenceLinkAction.Revoke,
        "replace" => EvidenceLinkAction.Replace,
        _ => throw new InvalidOperationException("Stored evidence action is invalid.")
    };

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? OptionalLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
