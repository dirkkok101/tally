using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;

namespace Tally.Infrastructure.Storage.Actuals;

public sealed class ActualsProjectionStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<ActualsItem>> ProjectAsync(ActualsFilter filter, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        return await ProjectAsync(connection, null, filter, cancellationToken);
    }

    public static async Task<IReadOnlyList<ActualsItem>> ProjectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActualsFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.IsValid()) throw new ArgumentException(ActualsFilter.InvalidError, nameof(filter));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql(filter);
        BindSqlFilter(command, filter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ActualsItem>();
        var row = 0;
        while (reader.Read())
        {
            if ((row++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var item = Read(reader);
            if (filter.Matches(item)) items.Add(item);
        }

        return items;
    }

    internal static void BindSqlFilter(SqliteCommand command, ActualsFilter filter)
    {
        command.Parameters.AddWithValue("$effectiveFrom", filter.EffectiveFrom is null ? DBNull.Value : filter.EffectiveFrom.Value.ToString());
        command.Parameters.AddWithValue("$effectiveTo", filter.EffectiveTo is null ? DBNull.Value : filter.EffectiveTo.Value.ToString());
        if (filter.AccountIds is not null)
        {
            var index = 0;
            foreach (var accountId in filter.AccountIds.Order(StringComparer.Ordinal))
            {
                command.Parameters.AddWithValue("$account" + index++, accountId);
            }
        }
    }

    internal static string Sql(ActualsFilter filter, bool compact = false)
    {
        var accountPredicate = filter.AccountIds is null
            ? "1 = 1"
            : "fact.account_id IN (" + string.Join(", ", Enumerable.Range(0, filter.AccountIds.Count).Select(index => "$account" + index)) + ")";
        var lifecyclePredicate = filter.LifecycleStates is null
            ? "lifecycle.transaction_id IS NULL"
            : "1 = 1";
        var projection = compact
            ? """
              current.transaction_id,
              current.account_id,
              current.signed_amount_minor,
              current.effective_date,
              current.category_id,
              category.ancestry_ids,
              current.pool_state,
              current.pool_id,
              current.instrument_state,
              current.instrument_id,
              current.cardholder_state,
              current.cardholder_id,
              current.evidence_kinds_json,
              current.reconciliation_state,
              current.relationship_state
              """
            : """
              current.transaction_id,
              current.account_id,
              current.signed_amount_minor,
              current.effective_date,
              current.original_description,
              current.lifecycle_action,
              current.category_id,
              category.ancestry_ids,
              current.pool_state,
              current.pool_id,
              current.instrument_state,
              current.instrument_id,
              current.cardholder_state,
              current.cardholder_id,
              current.evidence_kinds_json,
              current.reconciliation_state,
              current.relationship_state
              """;
        return $$"""
            WITH active_evidence_distinct AS (
                SELECT link.transaction_id, evidence.kind
                FROM evidence_link_event AS link
                JOIN evidence_record AS evidence ON evidence.evidence_id = link.evidence_id
                WHERE link.action IN ('link', 'replace')
                  AND NOT EXISTS (
                      SELECT 1 FROM evidence_link_event AS successor
                      WHERE successor.previous_link_event_id = link.link_event_id)
                GROUP BY link.transaction_id, evidence.kind
            ),
            active_evidence AS (
                SELECT transaction_id, json_group_array(kind) AS evidence_kinds_json
                FROM (
                    SELECT transaction_id, kind
                    FROM active_evidence_distinct
                    ORDER BY transaction_id, kind)
                GROUP BY transaction_id
            ),
            active_reconciliation AS (
                SELECT transaction_id, reconciliation_state
                FROM (
                    SELECT decision.active_transaction_id AS transaction_id,
                           CASE decision.disposition
                               WHEN 'confirmed_existing' THEN 'statement_reconciled'
                               WHEN 'corrected_from_statement' THEN 'statement_reconciled'
                               WHEN 'statement_only' THEN 'statement_only'
                               WHEN 'ambiguous' THEN 'ambiguous_match'
                               WHEN 'owner_confirmed_match' THEN 'owner_confirmed_match'
                               WHEN 'replaced' THEN 'owner_confirmed_match'
                               WHEN 'exception' THEN 'reconciliation_exception'
                               ELSE 'recorded_unreconciled' END AS reconciliation_state,
                           ROW_NUMBER() OVER (
                               PARTITION BY decision.active_transaction_id
                               ORDER BY decision.decided_at DESC, decision.decision_id DESC) AS ordinal
                    FROM reconciliation_current_v2 AS decision
                    WHERE decision.active_transaction_id IS NOT NULL)
                WHERE ordinal = 1
            ),
            prior_reconciliation AS (
                SELECT transaction_id, reconciliation_state
                FROM (
                    SELECT decision.prior_transaction_id AS transaction_id,
                           CASE decision.disposition
                               WHEN 'corrected_from_statement' THEN 'statement_reconciled'
                               WHEN 'replaced' THEN 'recorded_absent_from_statement'
                               WHEN 'revoked' THEN 'recorded_absent_from_statement'
                               ELSE 'reconciliation_exception' END AS reconciliation_state,
                           ROW_NUMBER() OVER (
                               PARTITION BY decision.prior_transaction_id
                               ORDER BY decision.decided_at DESC, decision.decision_id DESC) AS ordinal
                    FROM reconciliation_current_v2 AS decision
                    WHERE decision.prior_transaction_id IS NOT NULL)
                WHERE ordinal = 1
            ),
            latest_coverage AS (
                SELECT transaction_id, outcome
                FROM (
                    SELECT coverage.transaction_id, coverage.outcome,
                           ROW_NUMBER() OVER (
                               PARTITION BY coverage.transaction_id
                               ORDER BY coverage.recorded_at DESC, coverage.coverage_entry_id DESC) AS ordinal
                    FROM coverage_entry AS coverage
                    WHERE coverage.transaction_id IS NOT NULL)
                WHERE ordinal = 1
            ),
            current_rows AS (
                SELECT fact.transaction_id,
                       fact.account_id,
                       fact.signed_amount_minor,
                       fact.effective_date,
                       fact.original_description,
                       lifecycle.action AS lifecycle_action,
                       CASE
                           WHEN relationship.relationship_type = 'refund'
                            AND relationship.target_transaction_id = fact.transaction_id
                               THEN refund.category_id
                           ELSE allocation.category_id END AS category_id,
                       CASE
                           WHEN relationship.relationship_type = 'refund'
                            AND relationship.target_transaction_id = fact.transaction_id
                               THEN refund.pool_state
                           ELSE pool.assignment_state END AS pool_state,
                       CASE
                           WHEN relationship.relationship_type = 'refund'
                            AND relationship.target_transaction_id = fact.transaction_id
                               THEN refund.pool_id
                           ELSE pool.pool_id END AS pool_id,
                       attribution.instrument_state,
                       attribution.instrument_id,
                       attribution.cardholder_state,
                       attribution.cardholder_id,
                       COALESCE(evidence.evidence_kinds_json, '[]') AS evidence_kinds_json,
                       COALESCE(active_reconciliation.reconciliation_state,
                                prior_reconciliation.reconciliation_state,
                                latest_coverage.outcome,
                                'recorded_unreconciled') AS reconciliation_state,
                       CASE
                           WHEN relationship.relationship_id IS NULL THEN 'none'
                           WHEN relationship.source_transaction_id = fact.transaction_id THEN relationship.source_role
                           ELSE relationship.target_role END AS relationship_state
                FROM transaction_fact AS fact
                LEFT JOIN transaction_lifecycle_event AS lifecycle ON lifecycle.transaction_id = fact.transaction_id
                LEFT JOIN current_category_allocation AS allocation ON allocation.transaction_id = fact.transaction_id
                JOIN current_pool_assignment AS pool ON pool.transaction_id = fact.transaction_id
                JOIN current_transaction_attribution AS attribution ON attribution.transaction_id = fact.transaction_id
                LEFT JOIN financial_relationship_current AS relationship
                  ON relationship.state = 'active'
                 AND (relationship.source_transaction_id = fact.transaction_id
                   OR relationship.target_transaction_id = fact.transaction_id)
                LEFT JOIN refund_current_dimensions AS refund ON refund.relationship_id = relationship.relationship_id
                LEFT JOIN active_evidence AS evidence ON evidence.transaction_id = fact.transaction_id
                LEFT JOIN active_reconciliation ON active_reconciliation.transaction_id = fact.transaction_id
                LEFT JOIN prior_reconciliation ON prior_reconciliation.transaction_id = fact.transaction_id
                LEFT JOIN latest_coverage ON latest_coverage.transaction_id = fact.transaction_id
                WHERE ($effectiveFrom IS NULL OR fact.effective_date >= $effectiveFrom)
                  AND ($effectiveTo IS NULL OR fact.effective_date <= $effectiveTo)
                  AND {{accountPredicate}}
                  AND {{lifecyclePredicate}}
            )
            SELECT {{projection}}
            FROM current_rows AS current
            LEFT JOIN current_category_projection AS category ON category.category_id = current.category_id
            ORDER BY current.effective_date DESC, current.transaction_id DESC;
            """;
    }

    private static ActualsItem Read(SqliteDataReader reader)
    {
        if (!EffectiveDate.TryParse(reader.GetString(3), out var effectiveDate, out _))
        {
            throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored Effective Date is invalid.");
        }
        var categoryId = Optional(reader, 6);
        return new(
            reader.GetString(0),
            reader.GetString(1),
            Money.FromMinorUnits(reader.GetInt64(2)),
            effectiveDate,
            reader.GetString(4),
            LifecycleStatus(Optional(reader, 5)),
            categoryId is null ? TransactionCategoryState.Uncategorized : TransactionCategoryState.Categorized,
            categoryId,
            reader.IsDBNull(7) ? [] : ParseAncestry(reader.GetString(7)),
            PoolState(reader.GetString(8)),
            Optional(reader, 9),
            KnowledgeState(reader.GetString(10)),
            Optional(reader, 11),
            KnowledgeState(reader.GetString(12)),
            Optional(reader, 13),
            ParseEvidenceKinds(reader.GetString(14)),
            ReconciliationState(reader.GetString(15)),
            RelationshipState(reader.GetString(16)));
    }

    private static IReadOnlyList<string> ParseAncestry(string value) => value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<EvidenceKind> ParseEvidenceKinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element => EvidenceKindValue(element.GetString()!)).ToArray();
    }

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static TransactionLifecycleStatus LifecycleStatus(string? action) => action switch
    {
        null => TransactionLifecycleStatus.Active,
        "void" => TransactionLifecycleStatus.Voided,
        "superseded" or "statement_authoritative_replacement" => TransactionLifecycleStatus.Superseded,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored lifecycle state is invalid.")
    };

    private static TransactionPoolState PoolState(string value) => value switch
    {
        "assigned" => TransactionPoolState.Assigned,
        "unassigned" => TransactionPoolState.Unassigned,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored pool state is invalid.")
    };

    private static TransactionKnowledgeState KnowledgeState(string value) => value switch
    {
        "known" => TransactionKnowledgeState.Known,
        "unknown" => TransactionKnowledgeState.Unknown,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored attribution state is invalid.")
    };

    private static EvidenceKind EvidenceKindValue(string value) => value switch
    {
        "agent_capture" => EvidenceKind.AgentCapture,
        "statement_row" => EvidenceKind.StatementRow,
        "receipt" => EvidenceKind.Receipt,
        "external_document" => EvidenceKind.ExternalDocument,
        "owner_assertion" => EvidenceKind.OwnerAssertion,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored evidence kind is invalid.")
    };

    private static TransactionReconciliationState ReconciliationState(string value) => value switch
    {
        "recorded_unreconciled" => TransactionReconciliationState.RecordedUnreconciled,
        "statement_reconciled" => TransactionReconciliationState.StatementReconciled,
        "statement_only" => TransactionReconciliationState.StatementOnly,
        "recorded_absent_from_statement" => TransactionReconciliationState.RecordedAbsentFromStatement,
        "ambiguous_match" => TransactionReconciliationState.AmbiguousMatch,
        "owner_confirmed_match" => TransactionReconciliationState.OwnerConfirmedMatch,
        "reconciliation_exception" => TransactionReconciliationState.ReconciliationException,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored reconciliation state is invalid.")
    };

    private static ActualsRelationshipState RelationshipState(string value) => value switch
    {
        "none" => ActualsRelationshipState.None,
        "transfer_outflow" => ActualsRelationshipState.TransferOutflow,
        "transfer_inflow" => ActualsRelationshipState.TransferInflow,
        "refund_original" => ActualsRelationshipState.RefundOriginal,
        "refund_credit" => ActualsRelationshipState.RefundCredit,
        _ => throw new InvalidOperationException($"{ActualsCalculator.InvariantError}: Stored relationship state is invalid.")
    };
}
