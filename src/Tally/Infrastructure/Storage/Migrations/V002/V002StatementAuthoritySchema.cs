using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V002;

public sealed class V002StatementAuthoritySchema : ILedgerSchemaFragment
{
    public const string FragmentName = "statement_authority";
    public int Version => 2;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE reconciliation_decision_authority (
                decision_id TEXT PRIMARY KEY REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                disposition_detail TEXT NOT NULL CHECK (disposition_detail IN ('confirmed_existing', 'corrected_from_statement', 'statement_only', 'ambiguous', 'exception', 'owner_confirmed_match', 'rejected', 'revoked', 'replaced')),
                prior_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                active_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                authority_kind TEXT NOT NULL CHECK (authority_kind IN ('deterministic_policy', 'owner')),
                statement_authority_basis TEXT,
                schema_origin TEXT NOT NULL CHECK (schema_origin IN ('legacy_v1', 'v2')),
                recorded_at TEXT NOT NULL,
                CHECK (prior_transaction_id IS NULL OR prior_transaction_id <> active_transaction_id),
                CHECK (statement_authority_basis IS NULL OR length(trim(statement_authority_basis)) > 0),
                CHECK (schema_origin = 'legacy_v1' OR recorded_at GLOB '????-??-??T??:??:??*Z')
            );
            INSERT INTO reconciliation_decision_authority(
                decision_id,
                disposition_detail,
                prior_transaction_id,
                active_transaction_id,
                authority_kind,
                statement_authority_basis,
                schema_origin,
                recorded_at)
            SELECT
                decision_id,
                CASE disposition
                    WHEN 'deterministic_match' THEN 'confirmed_existing'
                    WHEN 'owner_confirmed' THEN 'owner_confirmed_match'
                    ELSE disposition
                END,
                NULL,
                transaction_id,
                CASE deterministic WHEN 1 THEN 'deterministic_policy' ELSE 'owner' END,
                NULL,
                'legacy_v1',
                decided_at
            FROM reconciliation_decision;

            CREATE TABLE statement_unknown_attribution_authority (
                attribution_event_id TEXT PRIMARY KEY REFERENCES transaction_attribution_event(attribution_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                source_transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                decision_id TEXT NOT NULL REFERENCES reconciliation_decision_authority(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor_context TEXT NOT NULL CHECK (length(trim(actor_context)) > 0),
                recorded_at TEXT NOT NULL CHECK (recorded_at GLOB '????-??-??T??:??:??*Z')
            );
            CREATE TABLE statement_correction (
                correction_id TEXT PRIMARY KEY,
                decision_id TEXT NOT NULL UNIQUE REFERENCES reconciliation_decision_authority(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                prior_transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                active_transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                supersession_lifecycle_event_id TEXT NOT NULL UNIQUE REFERENCES transaction_lifecycle_event(lifecycle_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                category_resolution TEXT NOT NULL CHECK (category_resolution IN ('uncategorized', 'carry_forward')),
                category_allocation_event_id TEXT UNIQUE REFERENCES category_allocation_event(allocation_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                pool_assignment_event_id TEXT NOT NULL UNIQUE REFERENCES pool_assignment_event(pool_assignment_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                payment_resolution TEXT NOT NULL CHECK (payment_resolution IN ('carry_forward', 'unknown_initialization')),
                attribution_event_id TEXT NOT NULL UNIQUE REFERENCES transaction_attribution_event(attribution_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                authority_basis TEXT NOT NULL CHECK (length(trim(authority_basis)) > 0),
                previous_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor_context TEXT NOT NULL CHECK (length(trim(actor_context)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                CHECK (prior_transaction_id <> active_transaction_id),
                CHECK (
                    (category_resolution = 'uncategorized' AND category_allocation_event_id IS NULL) OR
                    (category_resolution = 'carry_forward' AND category_allocation_event_id IS NOT NULL)
                )
            );
            CREATE TABLE statement_correction_relationship_event (
                correction_id TEXT NOT NULL REFERENCES statement_correction(correction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (typeof(ordinal) = 'integer' AND ordinal >= 0),
                relationship_lifecycle_event_id TEXT NOT NULL UNIQUE REFERENCES relationship_lifecycle_event(lifecycle_event_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                PRIMARY KEY (correction_id, ordinal)
            );

            CREATE VIEW reconciliation_decision_v2 AS
                SELECT
                    decision.decision_id,
                    decision.evidence_id,
                    authority.prior_transaction_id,
                    authority.active_transaction_id,
                    authority.disposition_detail AS disposition,
                    decision.policy_id,
                    decision.policy_version,
                    decision.match_basis,
                    authority.authority_kind,
                    authority.statement_authority_basis,
                    decision.reason,
                    decision.decided_by AS actor_context,
                    decision.decided_at,
                    decision.previous_decision_id,
                    authority.schema_origin
                FROM reconciliation_decision AS decision
                JOIN reconciliation_decision_authority AS authority ON authority.decision_id = decision.decision_id;
            CREATE VIEW reconciliation_current_v2 AS
                SELECT decision.*
                FROM reconciliation_decision_v2 AS decision
                WHERE NOT EXISTS (
                    SELECT 1 FROM reconciliation_decision AS successor
                    WHERE successor.previous_decision_id = decision.decision_id
                );
            CREATE VIEW evidence_link_history_v2 AS
                SELECT
                    link.link_event_id,
                    link.evidence_id,
                    evidence.kind AS evidence_kind,
                    link.transaction_id,
                    link.role,
                    link.action,
                    link.decision_id,
                    link.reason,
                    link.recorded_by AS actor_context,
                    link.recorded_at,
                    link.previous_link_event_id,
                    CASE
                        WHEN link.action IN ('link', 'replace') AND NOT EXISTS (
                            SELECT 1 FROM evidence_link_event AS successor
                            WHERE successor.previous_link_event_id = link.link_event_id
                        ) THEN 1
                        ELSE 0
                    END AS is_active
                FROM evidence_link_event AS link
                JOIN evidence_record AS evidence ON evidence.evidence_id = link.evidence_id;

            CREATE TRIGGER reconciliation_decision_authority_v2_contract_before_insert
            BEFORE INSERT ON reconciliation_decision_authority
            WHEN NEW.schema_origin = 'v2'
            BEGIN
                SELECT RAISE(ABORT, 'decision authority does not match the base decision')
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM reconciliation_decision AS decision
                    WHERE decision.decision_id = NEW.decision_id
                      AND ((NEW.authority_kind = 'deterministic_policy' AND decision.deterministic = 1 AND decision.policy_id IS NOT NULL) OR
                           (NEW.authority_kind = 'owner' AND decision.deterministic = 0))
                      AND (decision.transaction_id IS NULL OR decision.transaction_id IS NEW.active_transaction_id)
                      AND decision.decided_at = NEW.recorded_at
                );
                SELECT RAISE(ABORT, 'confirmed-existing authority requires one active transaction')
                WHERE NEW.disposition_detail = 'confirmed_existing'
                  AND (NEW.active_transaction_id IS NULL OR NEW.prior_transaction_id IS NOT NULL);
                SELECT RAISE(ABORT, 'statement correction authority is incomplete')
                WHERE NEW.disposition_detail = 'corrected_from_statement'
                  AND (NEW.prior_transaction_id IS NULL OR NEW.active_transaction_id IS NULL OR NEW.statement_authority_basis IS NULL);
                SELECT RAISE(ABORT, 'statement-only authority requires one active transaction')
                WHERE NEW.disposition_detail = 'statement_only'
                  AND (NEW.active_transaction_id IS NULL OR NEW.prior_transaction_id IS NOT NULL);
                SELECT RAISE(ABORT, 'owner confirmation requires owner authority and one active transaction')
                WHERE NEW.disposition_detail = 'owner_confirmed_match'
                  AND (NEW.authority_kind <> 'owner' OR NEW.active_transaction_id IS NULL);
            END;
            CREATE TRIGGER statement_unknown_attribution_contract_before_insert
            BEFORE INSERT ON statement_unknown_attribution_authority
            BEGIN
                SELECT RAISE(ABORT, 'statement unknown attribution must annotate an unknown initialization')
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM transaction_attribution_event AS attribution
                    JOIN reconciliation_decision_authority AS authority ON authority.decision_id = NEW.decision_id
                    WHERE attribution.attribution_event_id = NEW.attribution_event_id
                      AND attribution.action = 'initialize'
                      AND attribution.instrument_state = 'unknown'
                      AND attribution.cardholder_state = 'unknown'
                      AND attribution.transaction_id = authority.active_transaction_id
                      AND NEW.source_transaction_id = authority.prior_transaction_id
                );
            END;
            CREATE TRIGGER statement_correction_contract_before_insert
            BEFORE INSERT ON statement_correction
            BEGIN
                SELECT RAISE(ABORT, 'statement correction authority is inconsistent')
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM reconciliation_decision_authority AS authority
                    JOIN reconciliation_decision AS decision ON decision.decision_id = authority.decision_id
                    WHERE authority.decision_id = NEW.decision_id
                      AND authority.disposition_detail = 'corrected_from_statement'
                      AND authority.prior_transaction_id = NEW.prior_transaction_id
                      AND authority.active_transaction_id = NEW.active_transaction_id
                      AND authority.statement_authority_basis = NEW.authority_basis
                      AND decision.previous_decision_id IS NEW.previous_decision_id
                      AND decision.decided_by = NEW.actor_context
                      AND decision.decided_at = NEW.occurred_at
                );
                SELECT RAISE(ABORT, 'statement correction supersession is inconsistent')
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM transaction_lifecycle_event AS lifecycle
                    WHERE lifecycle.lifecycle_event_id = NEW.supersession_lifecycle_event_id
                      AND lifecycle.transaction_id = NEW.prior_transaction_id
                      AND lifecycle.action = 'statement_authoritative_replacement'
                      AND lifecycle.replacement_transaction_id = NEW.active_transaction_id
                      AND lifecycle.reconciliation_decision_id = NEW.decision_id
                );
                SELECT RAISE(ABORT, 'statement correction category carry-forward is inconsistent')
                WHERE NEW.category_resolution = 'carry_forward'
                  AND NOT EXISTS (
                      SELECT 1 FROM category_allocation_event AS allocation
                      WHERE allocation.allocation_event_id = NEW.category_allocation_event_id
                        AND allocation.transaction_id = NEW.active_transaction_id
                        AND allocation.action = 'carry_forward'
                        AND allocation.source_transaction_id = NEW.prior_transaction_id
                        AND allocation.reconciliation_decision_id = NEW.decision_id
                        AND EXISTS (
                            SELECT 1 FROM current_category_allocation AS current
                            WHERE current.allocation_event_id = allocation.allocation_event_id
                        )
                  );
                SELECT RAISE(ABORT, 'statement correction uncategorized state is inconsistent')
                WHERE NEW.category_resolution = 'uncategorized'
                  AND EXISTS (
                      SELECT 1 FROM current_category_allocation
                      WHERE transaction_id = NEW.active_transaction_id
                  );
                SELECT RAISE(ABORT, 'statement correction pool carry-forward is inconsistent')
                WHERE NOT EXISTS (
                    SELECT 1 FROM pool_assignment_event AS assignment
                    WHERE assignment.pool_assignment_event_id = NEW.pool_assignment_event_id
                      AND assignment.transaction_id = NEW.active_transaction_id
                      AND assignment.action = 'carry_forward'
                      AND assignment.source_transaction_id = NEW.prior_transaction_id
                      AND assignment.reconciliation_decision_id = NEW.decision_id
                      AND EXISTS (
                          SELECT 1 FROM current_pool_assignment AS current
                          WHERE current.pool_assignment_event_id = assignment.pool_assignment_event_id
                      )
                );
                SELECT RAISE(ABORT, 'statement correction payment carry-forward is inconsistent')
                WHERE NEW.payment_resolution = 'carry_forward'
                  AND NOT EXISTS (
                      SELECT 1 FROM transaction_attribution_event AS attribution
                      WHERE attribution.attribution_event_id = NEW.attribution_event_id
                        AND attribution.transaction_id = NEW.active_transaction_id
                        AND attribution.action = 'carry_forward'
                        AND attribution.source_transaction_id = NEW.prior_transaction_id
                        AND attribution.reconciliation_decision_id = NEW.decision_id
                        AND EXISTS (
                            SELECT 1 FROM current_transaction_attribution AS current
                            WHERE current.attribution_event_id = attribution.attribution_event_id
                        )
                  );
                SELECT RAISE(ABORT, 'statement correction unknown attribution is inconsistent')
                WHERE NEW.payment_resolution = 'unknown_initialization'
                  AND NOT EXISTS (
                      SELECT 1 FROM statement_unknown_attribution_authority AS unknown_authority
                      WHERE unknown_authority.attribution_event_id = NEW.attribution_event_id
                        AND unknown_authority.source_transaction_id = NEW.prior_transaction_id
                        AND unknown_authority.decision_id = NEW.decision_id
                        AND EXISTS (
                            SELECT 1 FROM current_transaction_attribution AS current
                            WHERE current.attribution_event_id = unknown_authority.attribution_event_id
                        )
                  );
            END;
            CREATE TRIGGER statement_correction_relationship_contract_before_insert
            BEFORE INSERT ON statement_correction_relationship_event
            BEGIN
                SELECT RAISE(ABORT, 'statement correction relationship replacement is inconsistent')
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM statement_correction AS correction
                    JOIN relationship_lifecycle_event AS lifecycle ON lifecycle.lifecycle_event_id = NEW.relationship_lifecycle_event_id
                    WHERE correction.correction_id = NEW.correction_id
                      AND lifecycle.event_type = 'replaced'
                      AND lifecycle.reconciliation_decision_id = correction.decision_id
                );
            END;

            CREATE TRIGGER reconciliation_decision_authority_is_immutable_before_update BEFORE UPDATE ON reconciliation_decision_authority BEGIN SELECT RAISE(ABORT, 'decision authority is immutable'); END;
            CREATE TRIGGER reconciliation_decision_authority_is_immutable_before_delete BEFORE DELETE ON reconciliation_decision_authority BEGIN SELECT RAISE(ABORT, 'decision authority cannot be deleted'); END;
            CREATE TRIGGER statement_unknown_attribution_is_immutable_before_update BEFORE UPDATE ON statement_unknown_attribution_authority BEGIN SELECT RAISE(ABORT, 'statement unknown attribution authority is immutable'); END;
            CREATE TRIGGER statement_unknown_attribution_is_immutable_before_delete BEFORE DELETE ON statement_unknown_attribution_authority BEGIN SELECT RAISE(ABORT, 'statement unknown attribution authority cannot be deleted'); END;
            CREATE TRIGGER statement_correction_is_immutable_before_update BEFORE UPDATE ON statement_correction BEGIN SELECT RAISE(ABORT, 'statement corrections are immutable'); END;
            CREATE TRIGGER statement_correction_is_immutable_before_delete BEFORE DELETE ON statement_correction BEGIN SELECT RAISE(ABORT, 'statement corrections cannot be deleted'); END;
            CREATE TRIGGER statement_correction_relationship_is_immutable_before_update BEFORE UPDATE ON statement_correction_relationship_event BEGIN SELECT RAISE(ABORT, 'statement correction relationship history is immutable'); END;
            CREATE TRIGGER statement_correction_relationship_is_immutable_before_delete BEFORE DELETE ON statement_correction_relationship_event BEGIN SELECT RAISE(ABORT, 'statement correction relationship history cannot be deleted'); END;

            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (2, 'statement_authority', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
