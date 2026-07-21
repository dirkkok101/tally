using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V001;

public sealed class V001RelationshipActualsSchema : ILedgerSchemaFragment
{
    public const string FragmentName = "v001_relationship_actuals";
    public int Version => 1;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE financial_relationship (
                relationship_id TEXT PRIMARY KEY,
                relationship_type TEXT NOT NULL CHECK (relationship_type IN ('transfer', 'refund')),
                source_transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                source_role TEXT NOT NULL CHECK (source_role IN ('transfer_outflow', 'refund_original')),
                target_transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                target_role TEXT NOT NULL CHECK (target_role IN ('transfer_inflow', 'refund_credit')),
                amount_minor INTEGER NOT NULL CHECK (typeof(amount_minor) = 'integer' AND amount_minor > 0),
                state TEXT NOT NULL CHECK (state = 'active'),
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z'),
                actor_context TEXT NOT NULL CHECK (length(trim(actor_context)) > 0),
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (source_transaction_id <> target_transaction_id),
                CHECK (
                    (relationship_type = 'transfer' AND source_role = 'transfer_outflow' AND target_role = 'transfer_inflow') OR
                    (relationship_type = 'refund' AND source_role = 'refund_original' AND target_role = 'refund_credit')
                )
            );
            CREATE TABLE relationship_lifecycle_event (
                lifecycle_event_id TEXT PRIMARY KEY,
                relationship_id TEXT NOT NULL UNIQUE REFERENCES financial_relationship(relationship_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                event_type TEXT NOT NULL CHECK (event_type IN ('revoked', 'replaced')),
                replacement_relationship_id TEXT REFERENCES financial_relationship(relationship_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor_context TEXT NOT NULL CHECK (length(trim(actor_context)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                CHECK (replacement_relationship_id IS NULL OR replacement_relationship_id <> relationship_id),
                CHECK (
                    (event_type = 'revoked' AND replacement_relationship_id IS NULL) OR
                    (event_type = 'replaced' AND replacement_relationship_id IS NOT NULL)
                )
            );

            CREATE VIEW financial_relationship_current AS
                SELECT
                    relationship.relationship_id,
                    relationship.relationship_type,
                    relationship.source_transaction_id,
                    relationship.source_role,
                    relationship.target_transaction_id,
                    relationship.target_role,
                    relationship.amount_minor,
                    CASE WHEN lifecycle.lifecycle_event_id IS NULL THEN 'active' ELSE 'retired' END AS state,
                    relationship.created_at,
                    relationship.actor_context,
                    relationship.reconciliation_decision_id,
                    lifecycle.lifecycle_event_id,
                    lifecycle.event_type,
                    lifecycle.replacement_relationship_id
                FROM financial_relationship AS relationship
                LEFT JOIN relationship_lifecycle_event AS lifecycle ON lifecycle.relationship_id = relationship.relationship_id;
            CREATE VIEW refund_current_dimensions AS
                SELECT
                    relationship.relationship_id,
                    relationship.source_transaction_id AS original_transaction_id,
                    relationship.target_transaction_id AS refund_transaction_id,
                    category.category_id,
                    COALESCE(pool.assignment_state, 'unassigned') AS pool_state,
                    pool.pool_id
                FROM financial_relationship_current AS relationship
                LEFT JOIN current_category_allocation AS category ON category.transaction_id = relationship.source_transaction_id
                LEFT JOIN current_pool_assignment AS pool ON pool.transaction_id = relationship.source_transaction_id
                WHERE relationship.relationship_type = 'refund' AND relationship.state = 'active';

            CREATE TRIGGER financial_relationship_requires_active_transactions_before_insert
            BEFORE INSERT ON financial_relationship
            BEGIN
                SELECT RAISE(ABORT, 'relationship transactions must be active')
                WHERE EXISTS (
                    SELECT 1 FROM transaction_lifecycle_event
                    WHERE transaction_id IN (NEW.source_transaction_id, NEW.target_transaction_id)
                );
            END;
            CREATE TRIGGER financial_relationship_roles_are_exclusive_before_insert
            BEFORE INSERT ON financial_relationship
            BEGIN
                SELECT RAISE(ABORT, 'active relationship role already exists for transaction')
                WHERE EXISTS (
                    SELECT 1
                    FROM financial_relationship_current AS active_relationship
                    WHERE active_relationship.state = 'active'
                      AND (
                          active_relationship.source_transaction_id IN (NEW.source_transaction_id, NEW.target_transaction_id) OR
                          active_relationship.target_transaction_id IN (NEW.source_transaction_id, NEW.target_transaction_id)
                      )
                );
            END;
            CREATE TRIGGER financial_relationship_is_immutable_before_update BEFORE UPDATE ON financial_relationship BEGIN SELECT RAISE(ABORT, 'financial relationships are immutable'); END;
            CREATE TRIGGER financial_relationship_is_immutable_before_delete BEFORE DELETE ON financial_relationship BEGIN SELECT RAISE(ABORT, 'financial relationships cannot be deleted'); END;
            CREATE TRIGGER relationship_lifecycle_is_immutable_before_update BEFORE UPDATE ON relationship_lifecycle_event BEGIN SELECT RAISE(ABORT, 'relationship lifecycle is immutable'); END;
            CREATE TRIGGER relationship_lifecycle_is_immutable_before_delete BEFORE DELETE ON relationship_lifecycle_event BEGIN SELECT RAISE(ABORT, 'relationship lifecycle is immutable'); END;

            CREATE TABLE query_snapshot (
                snapshot_id TEXT PRIMARY KEY,
                contract_version TEXT NOT NULL CHECK (length(trim(contract_version)) > 0),
                canonical_filter_hash TEXT NOT NULL CHECK (length(trim(canonical_filter_hash)) > 0),
                generation_fingerprint TEXT NOT NULL CHECK (length(trim(generation_fingerprint)) > 0),
                category_hierarchy_fingerprint TEXT NOT NULL CHECK (length(trim(category_hierarchy_fingerprint)) > 0),
                persistence_scope TEXT NOT NULL CHECK (persistence_scope = 'ephemeral'),
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z'),
                expires_at TEXT NOT NULL CHECK (expires_at GLOB '????-??-??T??:??:??*Z'),
                net_account_movement_minor INTEGER NOT NULL CHECK (typeof(net_account_movement_minor) = 'integer'),
                external_spend_minor INTEGER NOT NULL CHECK (typeof(external_spend_minor) = 'integer'),
                budget_actual_minor INTEGER NOT NULL CHECK (typeof(budget_actual_minor) = 'integer'),
                CHECK (expires_at > created_at)
            );
            CREATE TABLE query_snapshot_item (
                snapshot_id TEXT NOT NULL REFERENCES query_snapshot(snapshot_id) ON DELETE CASCADE ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (typeof(ordinal) = 'integer' AND ordinal >= 0),
                transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                effective_date TEXT NOT NULL CHECK (effective_date GLOB '????-??-??' AND date(effective_date) = effective_date),
                category_state TEXT NOT NULL CHECK (category_state IN ('categorized', 'uncategorized')),
                category_id TEXT REFERENCES spend_category(category_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                frozen_ancestry_ids_json TEXT NOT NULL CHECK (json_valid(frozen_ancestry_ids_json) AND json_type(frozen_ancestry_ids_json) = 'array'),
                pool_state TEXT NOT NULL CHECK (pool_state IN ('assigned', 'unassigned')),
                pool_id TEXT REFERENCES spend_pool(pool_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                instrument_state TEXT NOT NULL CHECK (instrument_state IN ('known', 'unknown')),
                instrument_id TEXT REFERENCES payment_instrument(instrument_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                cardholder_state TEXT NOT NULL CHECK (cardholder_state IN ('known', 'unknown')),
                cardholder_id TEXT REFERENCES cardholder(cardholder_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                evidence_kinds_json TEXT NOT NULL CHECK (json_valid(evidence_kinds_json) AND json_type(evidence_kinds_json) = 'array'),
                reconciliation_state TEXT NOT NULL CHECK (reconciliation_state IN ('recorded_unreconciled', 'statement_reconciled', 'statement_only', 'recorded_absent_from_statement', 'ambiguous_match', 'owner_confirmed_match', 'reconciliation_exception')),
                relationship_state TEXT NOT NULL CHECK (relationship_state IN ('none', 'transfer_outflow', 'transfer_inflow', 'refund_original', 'refund_credit')),
                net_account_movement_minor INTEGER NOT NULL CHECK (typeof(net_account_movement_minor) = 'integer'),
                external_spend_minor INTEGER NOT NULL CHECK (typeof(external_spend_minor) = 'integer'),
                budget_actual_minor INTEGER NOT NULL CHECK (typeof(budget_actual_minor) = 'integer'),
                PRIMARY KEY (snapshot_id, ordinal),
                UNIQUE (snapshot_id, transaction_id),
                CHECK ((category_state = 'categorized' AND category_id IS NOT NULL) OR (category_state = 'uncategorized' AND category_id IS NULL)),
                CHECK ((category_state = 'categorized' AND json_array_length(frozen_ancestry_ids_json) > 0) OR (category_state = 'uncategorized' AND json_array_length(frozen_ancestry_ids_json) = 0)),
                CHECK ((pool_state = 'assigned' AND pool_id IS NOT NULL) OR (pool_state = 'unassigned' AND pool_id IS NULL)),
                CHECK ((instrument_state = 'known' AND instrument_id IS NOT NULL) OR (instrument_state = 'unknown' AND instrument_id IS NULL)),
                CHECK ((cardholder_state = 'known' AND cardholder_id IS NOT NULL) OR (cardholder_state = 'unknown' AND cardholder_id IS NULL))
            );
            CREATE INDEX ix_query_snapshot_item_stable_order
                ON query_snapshot_item(snapshot_id, effective_date DESC, transaction_id DESC);
            CREATE TABLE query_snapshot_group (
                snapshot_id TEXT NOT NULL REFERENCES query_snapshot(snapshot_id) ON DELETE CASCADE ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (typeof(ordinal) = 'integer' AND ordinal >= 0),
                group_kind TEXT NOT NULL CHECK (group_kind IN ('none', 'pool', 'category_direct', 'category_subtree', 'pool_category')),
                pool_bucket TEXT NOT NULL CHECK (pool_bucket IN ('not_applicable', 'assigned', 'unassigned')),
                pool_id TEXT REFERENCES spend_pool(pool_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                category_bucket TEXT NOT NULL CHECK (category_bucket IN ('not_applicable', 'categorized', 'uncategorized')),
                category_id TEXT REFERENCES spend_category(category_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                net_account_movement_minor INTEGER NOT NULL CHECK (typeof(net_account_movement_minor) = 'integer'),
                external_spend_minor INTEGER NOT NULL CHECK (typeof(external_spend_minor) = 'integer'),
                budget_actual_minor INTEGER NOT NULL CHECK (typeof(budget_actual_minor) = 'integer'),
                PRIMARY KEY (snapshot_id, ordinal),
                CHECK (
                    (pool_bucket = 'assigned' AND pool_id IS NOT NULL) OR
                    (pool_bucket IN ('not_applicable', 'unassigned') AND pool_id IS NULL)
                ),
                CHECK (
                    (category_bucket = 'categorized' AND category_id IS NOT NULL) OR
                    (category_bucket IN ('not_applicable', 'uncategorized') AND category_id IS NULL)
                ),
                CHECK (
                    (group_kind = 'none' AND pool_bucket = 'not_applicable' AND category_bucket = 'not_applicable') OR
                    (group_kind = 'pool' AND pool_bucket <> 'not_applicable' AND category_bucket = 'not_applicable') OR
                    (group_kind IN ('category_direct', 'category_subtree') AND pool_bucket = 'not_applicable' AND category_bucket <> 'not_applicable') OR
                    (group_kind = 'pool_category' AND pool_bucket <> 'not_applicable' AND category_bucket <> 'not_applicable')
                )
            );
            CREATE UNIQUE INDEX ux_query_snapshot_group_dimension
                ON query_snapshot_group(
                    snapshot_id,
                    group_kind,
                    pool_bucket,
                    COALESCE(pool_id, ''),
                    category_bucket,
                    COALESCE(category_id, '')
                );

            CREATE TRIGGER query_snapshot_is_immutable_before_update BEFORE UPDATE ON query_snapshot BEGIN SELECT RAISE(ABORT, 'query snapshots are immutable'); END;
            CREATE TRIGGER query_snapshot_item_is_immutable_before_update BEFORE UPDATE ON query_snapshot_item BEGIN SELECT RAISE(ABORT, 'query snapshot items are immutable'); END;
            CREATE TRIGGER query_snapshot_group_is_immutable_before_update BEFORE UPDATE ON query_snapshot_group BEGIN SELECT RAISE(ABORT, 'query snapshot groups are immutable'); END;

            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (1, 'v001_relationship_actuals', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
