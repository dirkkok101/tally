using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;

namespace Tally.Infrastructure.Storage.Transactions;

public sealed class TransactionStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<bool> EvidenceIdentityExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string logicalIdentityDigest,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM evidence_record WHERE logical_identity_digest = $digest);", ("$digest", logicalIdentityDigest));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task InsertFactAndDefaultsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        string initialAttributionEventId,
        string? assignedAttributionEventId,
        string poolAssignmentEventId,
        TransactionFact fact,
        string recordedAt,
        string osIdentity,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transactionId, $accountId, $amount, $currency, $transactionDate,
                    $postingDate, $description, $recordedAt, $osIdentity);
            INSERT INTO transaction_attribution_event (
                attribution_event_id, transaction_id, instrument_state, instrument_id, cardholder_state, cardholder_id,
                action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($initialAttributionEventId, $transactionId, 'unknown', NULL, 'unknown', NULL,
                    'initialize', NULL, NULL, NULL, 'initialize transaction attribution', $actor, $recordedAt);
            INSERT INTO pool_assignment_event (
                pool_assignment_event_id, transaction_id, assignment_state, pool_id, action,
                previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($poolAssignmentEventId, $transactionId, 'unassigned', NULL, 'initialize',
                    NULL, NULL, NULL, 'initialize transaction pool', $actor, $recordedAt);
            """,
            ("$transactionId", transactionId),
            ("$accountId", fact.AccountId),
            ("$amount", fact.SignedAmount.MinorUnits),
            ("$currency", fact.Currency.Code),
            ("$transactionDate", fact.TransactionDate.ToString()),
            ("$postingDate", fact.PostingDate?.ToString()),
            ("$description", fact.OriginalDescription),
            ("$recordedAt", recordedAt),
            ("$osIdentity", osIdentity),
            ("$initialAttributionEventId", initialAttributionEventId),
            ("$poolAssignmentEventId", poolAssignmentEventId),
            ("$actor", actor));
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (assignedAttributionEventId is null) return;
        await using var assignment = Command(connection, transaction, """
            INSERT INTO transaction_attribution_event (
                attribution_event_id, transaction_id, instrument_state, instrument_id, cardholder_state, cardholder_id,
                action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $instrumentState, $instrumentId, $cardholderState, $cardholderId,
                    'assign', $previousEventId, NULL, NULL, 'assign declared payment identity', $actor, $recordedAt);
            """,
            ("$eventId", assignedAttributionEventId),
            ("$transactionId", transactionId),
            ("$instrumentState", fact.InstrumentId is null ? "unknown" : "known"),
            ("$instrumentId", fact.InstrumentId),
            ("$cardholderState", fact.CardholderId is null ? "unknown" : "known"),
            ("$cardholderId", fact.CardholderId),
            ("$previousEventId", initialAttributionEventId),
            ("$actor", actor),
            ("$recordedAt", recordedAt));
        await assignment.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertInitialEvidenceLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string linkEventId,
        string evidenceId,
        string transactionId,
        string actor,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO evidence_link_event (
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkEventId, $evidenceId, $transactionId, 'supporting', 'link', NULL,
                    'initial transaction evidence', $actor, $recordedAt, NULL);
            """,
            ("$linkEventId", linkEventId),
            ("$evidenceId", evidenceId),
            ("$transactionId", transactionId),
            ("$actor", actor),
            ("$recordedAt", recordedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TransactionDetail?> GetAsync(string transactionId, bool includeHistory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await GetAsync(connection, null, transactionId, includeHistory, cancellationToken);
    }

    public async Task<TransactionDetail?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transactionId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT fact.transaction_id, fact.account_id, fact.signed_amount_minor, fact.currency_code,
                   fact.transaction_date, fact.posting_date, fact.effective_date, fact.original_description,
                   lifecycle.action, lifecycle.replacement_transaction_id,
                   COALESCE(
                       (SELECT entry.outcome FROM coverage_entry AS entry WHERE entry.transaction_id = fact.transaction_id ORDER BY entry.recorded_at DESC, entry.coverage_entry_id DESC LIMIT 1),
                       (SELECT CASE decision.disposition
                           WHEN 'deterministic_match' THEN 'statement_reconciled'
                           WHEN 'statement_only' THEN 'statement_only'
                           WHEN 'ambiguous' THEN 'ambiguous_match'
                           WHEN 'owner_confirmed' THEN 'owner_confirmed_match'
                           WHEN 'exception' THEN 'reconciliation_exception'
                           ELSE 'recorded_unreconciled' END
                        FROM reconciliation_current AS decision
                        WHERE decision.transaction_id = fact.transaction_id
                        ORDER BY decision.decided_at DESC, decision.decision_id DESC LIMIT 1),
                       'recorded_unreconciled'),
                   allocation.allocation_event_id, category.category_id, category.ancestry_ids,
                   pool.pool_assignment_event_id, pool.assignment_state, pool.pool_id,
                   attribution.attribution_event_id, attribution.instrument_state, attribution.instrument_id,
                   attribution.cardholder_state, attribution.cardholder_id,
                   fact.recorded_by_os_identity, fact.recorded_at
            FROM transaction_fact AS fact
            LEFT JOIN transaction_lifecycle_event AS lifecycle ON lifecycle.transaction_id = fact.transaction_id
            LEFT JOIN current_category_allocation AS allocation ON allocation.transaction_id = fact.transaction_id
            LEFT JOIN current_category_projection AS category ON category.category_id = allocation.category_id
            JOIN current_pool_assignment AS pool ON pool.transaction_id = fact.transaction_id
            JOIN current_transaction_attribution AS attribution ON attribution.transaction_id = fact.transaction_id
            WHERE fact.transaction_id = $transactionId;
            """, ("$transactionId", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var detail = new TransactionDetail(
            reader.GetString(0),
            reader.GetString(1),
            Money.FromMinorUnits(reader.GetInt64(2)).ToString(),
            reader.GetString(3),
            reader.GetString(4),
            Optional(reader, 5),
            reader.GetString(6),
            reader.GetString(7),
            LifecycleStatus(Optional(reader, 8)),
            Optional(reader, 9),
            ParseReconciliation(reader.GetString(10)),
            reader.IsDBNull(11)
                ? new(TransactionCategoryState.Uncategorized, null, null, [])
                : new(TransactionCategoryState.Categorized, reader.GetString(11), reader.GetString(12), ParseAncestry(reader.GetString(13))),
            new(reader.GetString(14), ParsePoolState(reader.GetString(15)), Optional(reader, 16)),
            new(reader.GetString(17), ParseKnowledge(reader.GetString(18)), Optional(reader, 19), ParseKnowledge(reader.GetString(20)), Optional(reader, 21)),
            [],
            reader.GetString(22),
            reader.GetString(23),
            null);
        await reader.DisposeAsync();

        detail = detail with { Evidence = await EvidenceAsync(connection, transaction, transactionId, cancellationToken) };
        return includeHistory ? detail with { History = await HistoryAsync(connection, transaction, transactionId, cancellationToken) } : detail;
    }

    private static async Task<IReadOnlyList<TransactionEvidenceDetail>> EvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT record.evidence_id, record.kind, record.logical_identity_digest,
                   record.opaque_external_reference, record.content_fingerprint,
                   observation.account_id, observation.signed_amount_minor, observation.currency_code,
                   observation.transaction_date, observation.posting_date, observation.instrument_id,
                   observation.cardholder_id, observation.description_fingerprint,
                   link.role, link.link_event_id, record.recorded_by, record.recorded_at
            FROM evidence_link_event AS link
            JOIN evidence_record AS record ON record.evidence_id = link.evidence_id
            LEFT JOIN evidence_observation AS observation ON observation.evidence_id = record.evidence_id
            WHERE link.transaction_id = $transactionId
              AND link.action IN ('link', 'replace')
              AND NOT EXISTS (SELECT 1 FROM evidence_link_event AS successor WHERE successor.previous_link_event_id = link.link_event_id)
            ORDER BY record.recorded_at, record.evidence_id;
            """, ("$transactionId", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var evidence = new List<TransactionEvidenceDetail>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var observation = Enumerable.Range(5, 8).All(reader.IsDBNull)
                ? null
                : new EvidenceObservation(Optional(reader, 5), OptionalLong(reader, 6), Optional(reader, 7), Optional(reader, 8), Optional(reader, 9), Optional(reader, 10), Optional(reader, 11), Optional(reader, 12));
            evidence.Add(new(
                reader.GetString(0), ParseEvidenceKind(reader.GetString(1)), reader.GetString(2), Optional(reader, 3), Optional(reader, 4),
                observation, ParseEvidenceRole(reader.GetString(13)), reader.GetString(14), reader.GetString(15), reader.GetString(16)));
        }

        return evidence;
    }

    private static async Task<TransactionHistory> HistoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transactionId,
        CancellationToken cancellationToken) => new(
            await LifecycleHistoryAsync(connection, transaction, transactionId, cancellationToken),
            await AttributionHistoryAsync(connection, transaction, transactionId, cancellationToken),
            await PoolHistoryAsync(connection, transaction, transactionId, cancellationToken),
            await CategoryHistoryAsync(connection, transaction, transactionId, cancellationToken));

    private static async Task<IReadOnlyList<TransactionLifecycleHistoryItem>> LifecycleHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT lifecycle_event_id, action, replacement_transaction_id, reconciliation_decision_id, reason, actor, occurred_at FROM transaction_lifecycle_event WHERE transaction_id = $id ORDER BY occurred_at, lifecycle_event_id;", ("$id", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TransactionLifecycleHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParseLifecycleAction(reader.GetString(1)), Optional(reader, 2), Optional(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return items;
    }

    private static async Task<IReadOnlyList<TransactionAttributionHistoryItem>> AttributionHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT attribution_event_id, instrument_state, instrument_id, cardholder_state, cardholder_id, action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at FROM transaction_attribution_event WHERE transaction_id = $id ORDER BY occurred_at, attribution_event_id;", ("$id", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TransactionAttributionHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParseKnowledge(reader.GetString(1)), Optional(reader, 2), ParseKnowledge(reader.GetString(3)), Optional(reader, 4), ParseAssignmentAction(reader.GetString(5)), Optional(reader, 6), Optional(reader, 7), Optional(reader, 8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
        return items;
    }

    private static async Task<IReadOnlyList<TransactionPoolHistoryItem>> PoolHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT pool_assignment_event_id, assignment_state, pool_id, action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at FROM pool_assignment_event WHERE transaction_id = $id ORDER BY occurred_at, pool_assignment_event_id;", ("$id", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TransactionPoolHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), ParsePoolState(reader.GetString(1)), Optional(reader, 2), ParseAssignmentAction(reader.GetString(3)), Optional(reader, 4), Optional(reader, 5), Optional(reader, 6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        return items;
    }

    private static async Task<IReadOnlyList<TransactionCategoryHistoryItem>> CategoryHistoryAsync(SqliteConnection connection, SqliteTransaction? transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT allocation_event_id, category_id, action, previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at FROM category_allocation_event WHERE transaction_id = $id ORDER BY occurred_at, allocation_event_id;", ("$id", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TransactionCategoryHistoryItem>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(reader.GetString(0), reader.GetString(1), ParseCategoryAction(reader.GetString(2)), Optional(reader, 3), Optional(reader, 4), Optional(reader, 5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        return items;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? OptionalLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static IReadOnlyList<string> ParseAncestry(string value) => value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static TransactionLifecycleStatus LifecycleStatus(string? action) => action switch { null => TransactionLifecycleStatus.Active, "void" => TransactionLifecycleStatus.Voided, "superseded" or "statement_authoritative_replacement" => TransactionLifecycleStatus.Superseded, _ => throw new InvalidOperationException("Unknown transaction lifecycle action.") };
    private static TransactionKnowledgeState ParseKnowledge(string value) => value switch
    {
        "known" => TransactionKnowledgeState.Known,
        "unknown" => TransactionKnowledgeState.Unknown,
        _ => throw new InvalidOperationException("Unknown transaction knowledge state.")
    };
    private static TransactionPoolState ParsePoolState(string value) => value switch
    {
        "assigned" => TransactionPoolState.Assigned,
        "unassigned" => TransactionPoolState.Unassigned,
        _ => throw new InvalidOperationException("Unknown transaction pool state.")
    };
    private static TransactionReconciliationState ParseReconciliation(string value) => value switch
    {
        "recorded_unreconciled" => TransactionReconciliationState.RecordedUnreconciled,
        "statement_reconciled" => TransactionReconciliationState.StatementReconciled,
        "statement_only" => TransactionReconciliationState.StatementOnly,
        "recorded_absent_from_statement" => TransactionReconciliationState.RecordedAbsentFromStatement,
        "ambiguous_match" => TransactionReconciliationState.AmbiguousMatch,
        "owner_confirmed_match" => TransactionReconciliationState.OwnerConfirmedMatch,
        "reconciliation_exception" => TransactionReconciliationState.ReconciliationException,
        _ => throw new InvalidOperationException("Unknown transaction reconciliation state.")
    };
    private static EvidenceKind ParseEvidenceKind(string value) => value switch { "agent_capture" => EvidenceKind.AgentCapture, "statement_row" => EvidenceKind.StatementRow, "receipt" => EvidenceKind.Receipt, "external_document" => EvidenceKind.ExternalDocument, "owner_assertion" => EvidenceKind.OwnerAssertion, _ => throw new InvalidOperationException("Unknown evidence kind.") };
    private static EvidenceLinkRole ParseEvidenceRole(string value) => value switch
    {
        "supporting" => EvidenceLinkRole.Supporting,
        "confirming" => EvidenceLinkRole.Confirming,
        _ => throw new InvalidOperationException("Unknown evidence link role.")
    };
    private static TransactionLifecycleAction ParseLifecycleAction(string value) => value switch
    {
        "void" => TransactionLifecycleAction.Void,
        "superseded" => TransactionLifecycleAction.Superseded,
        "statement_authoritative_replacement" => TransactionLifecycleAction.StatementAuthoritativeReplacement,
        _ => throw new InvalidOperationException("Unknown transaction lifecycle action.")
    };
    private static TransactionAssignmentAction ParseAssignmentAction(string value) => value switch
    {
        "initialize" => TransactionAssignmentAction.Initialize,
        "assign" => TransactionAssignmentAction.Assign,
        "correct" => TransactionAssignmentAction.Correct,
        "carry_forward" => TransactionAssignmentAction.CarryForward,
        _ => throw new InvalidOperationException("Unknown transaction assignment action.")
    };
    private static TransactionCategoryAction ParseCategoryAction(string value) => value switch
    {
        "assign" => TransactionCategoryAction.Assign,
        "correct" => TransactionCategoryAction.Correct,
        "carry_forward" => TransactionCategoryAction.CarryForward,
        _ => throw new InvalidOperationException("Unknown transaction category action.")
    };
}
