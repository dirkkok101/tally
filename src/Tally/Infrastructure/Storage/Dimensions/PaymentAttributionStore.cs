using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;

namespace Tally.Infrastructure.Storage.Dimensions;

public sealed record PaymentAttributionCurrent(
    string AttributionEventId,
    string TransactionId,
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId,
    TransactionAssignmentAction Action);

public sealed class PaymentAttributionStore
{
    public async Task<PaymentAttributionCurrent?> FindCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT attribution_event_id, transaction_id, instrument_state, instrument_id,
                   cardholder_state, cardholder_id, action
            FROM current_transaction_attribution
            WHERE transaction_id = $transactionId;
            """, ("$transactionId", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0), reader.GetString(1), ParseKnowledge(reader.GetString(2)), Optional(reader, 3),
                ParseKnowledge(reader.GetString(4)), Optional(reader, 5), ParseAction(reader.GetString(6)))
            : null;
    }

    public async Task AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string transactionId,
        TransactionKnowledgeState instrumentState,
        string? instrumentId,
        TransactionKnowledgeState cardholderState,
        string? cardholderId,
        TransactionAssignmentAction action,
        string? previousEventId,
        string? sourceTransactionId,
        string? reconciliationDecisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO transaction_attribution_event (
                attribution_event_id, transaction_id, instrument_state, instrument_id,
                cardholder_state, cardholder_id, action, previous_event_id, source_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $instrumentState, $instrumentId,
                    $cardholderState, $cardholderId, $action, $previousEventId, $sourceTransactionId,
                    $decisionId, $reason, $actor, $occurredAt);
            """,
            ("$eventId", eventId), ("$transactionId", transactionId), ("$instrumentState", KnowledgeValue(instrumentState)),
            ("$instrumentId", instrumentId), ("$cardholderState", KnowledgeValue(cardholderState)), ("$cardholderId", cardholderId),
            ("$action", ActionValue(action)), ("$previousEventId", previousEventId), ("$sourceTransactionId", sourceTransactionId),
            ("$decisionId", reconciliationDecisionId), ("$reason", reason), ("$actor", actor), ("$occurredAt", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentAttributionCarryForwardResult> CarryForwardOrUnknownAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityStore identityStore,
        string sourceTransactionId,
        string replacementTransactionId,
        string reconciliationDecisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedStatementCorrectionAsync(
                connection, transaction, sourceTransactionId, replacementTransactionId, reconciliationDecisionId, cancellationToken))
        {
            throw new InvalidOperationException("Payment attribution carry-forward requires statement-correction authority.");
        }
        if (await FindCurrentAsync(connection, transaction, replacementTransactionId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Replacement transaction attribution is already initialized.");
        }

        var source = await FindCurrentAsync(connection, transaction, sourceTransactionId, cancellationToken)
            ?? throw new InvalidOperationException("Source transaction attribution is missing.");
        var replacementAccountId = await AccountIdAsync(connection, transaction, replacementTransactionId, cancellationToken);
        var compatible = await IsCompatibleAsync(connection, transaction, identityStore, source, replacementAccountId, cancellationToken);
        var eventId = LedgerId.New().ToString();
        if (compatible)
        {
            await AppendAsync(
                connection, transaction, eventId, replacementTransactionId,
                source.InstrumentState, source.InstrumentId, source.CardholderState, source.CardholderId,
                TransactionAssignmentAction.CarryForward, null, sourceTransactionId, reconciliationDecisionId,
                reason, actor, occurredAt, cancellationToken);
            return new(sourceTransactionId, replacementTransactionId, reconciliationDecisionId, eventId, PaymentAttributionCarryForwardResolution.CarryForward, false);
        }

        await AppendAsync(
            connection, transaction, eventId, replacementTransactionId,
            TransactionKnowledgeState.Unknown, null, TransactionKnowledgeState.Unknown, null,
            TransactionAssignmentAction.Initialize, null, null, null, reason, actor, occurredAt, cancellationToken);
        await using var authority = Command(connection, transaction, """
            INSERT INTO statement_unknown_attribution_authority (
                attribution_event_id, source_transaction_id, decision_id, reason, actor_context, recorded_at)
            VALUES ($eventId, $sourceId, $decisionId, $reason, $actor, $occurredAt);
            """, ("$eventId", eventId), ("$sourceId", sourceTransactionId), ("$decisionId", reconciliationDecisionId),
            ("$reason", reason), ("$actor", actor), ("$occurredAt", occurredAt));
        await authority.ExecuteNonQueryAsync(cancellationToken);
        return new(sourceTransactionId, replacementTransactionId, reconciliationDecisionId, eventId, PaymentAttributionCarryForwardResolution.UnknownInitialization, true);
    }

    private static async Task<bool> IsCompatibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PaymentIdentityStore identityStore,
        PaymentAttributionCurrent source,
        string replacementAccountId,
        CancellationToken cancellationToken)
    {
        if (source.InstrumentState == TransactionKnowledgeState.Known)
        {
            if (await identityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Instrument, source.InstrumentId!, cancellationToken) is not null) return false;
            var identity = await identityStore.GetInstrumentIdentityAsync(connection, transaction, source.InstrumentId!, cancellationToken);
            if (identity?.AccountId is not null && identity.AccountId != replacementAccountId) return false;
        }
        return source.CardholderState != TransactionKnowledgeState.Known
            || await identityStore.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Cardholder, source.CardholderId!, cancellationToken) is null;
    }

    private static async Task<string> AccountIdAsync(SqliteConnection connection, SqliteTransaction transaction, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT account_id FROM transaction_fact WHERE transaction_id = $id;", ("$id", transactionId));
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Replacement transaction is missing.");
    }

    private static async Task<bool> IsAuthorizedStatementCorrectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTransactionId,
        string replacementTransactionId,
        string decisionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1
                FROM reconciliation_decision_authority AS authority
                JOIN transaction_lifecycle_event AS lifecycle
                  ON lifecycle.transaction_id = authority.prior_transaction_id
                 AND lifecycle.replacement_transaction_id = authority.active_transaction_id
                 AND lifecycle.reconciliation_decision_id = authority.decision_id
                WHERE authority.decision_id = $decisionId
                  AND authority.disposition_detail = 'corrected_from_statement'
                  AND authority.prior_transaction_id = $sourceId
                  AND authority.active_transaction_id = $replacementId
                  AND lifecycle.action = 'statement_authoritative_replacement');
            """, ("$decisionId", decisionId), ("$sourceId", sourceTransactionId), ("$replacementId", replacementTransactionId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
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
    private static string KnowledgeValue(TransactionKnowledgeState state) => state switch { TransactionKnowledgeState.Known => "known", TransactionKnowledgeState.Unknown => "unknown", _ => throw new ArgumentOutOfRangeException(nameof(state)) };
    private static TransactionKnowledgeState ParseKnowledge(string value) => value switch { "known" => TransactionKnowledgeState.Known, "unknown" => TransactionKnowledgeState.Unknown, _ => throw new InvalidOperationException("Stored payment attribution state is invalid.") };
    private static string ActionValue(TransactionAssignmentAction action) => action switch { TransactionAssignmentAction.Initialize => "initialize", TransactionAssignmentAction.Assign => "assign", TransactionAssignmentAction.Correct => "correct", TransactionAssignmentAction.CarryForward => "carry_forward", _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    private static TransactionAssignmentAction ParseAction(string value) => value switch { "initialize" => TransactionAssignmentAction.Initialize, "assign" => TransactionAssignmentAction.Assign, "correct" => TransactionAssignmentAction.Correct, "carry_forward" => TransactionAssignmentAction.CarryForward, _ => throw new InvalidOperationException("Stored payment attribution action is invalid.") };
}
