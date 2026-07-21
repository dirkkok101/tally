using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V001;

public sealed class V001TransactionSchema : ILedgerSchemaFragment
{
    public const string FragmentName = "v001_transaction";
    public int Version => 1;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE transaction_fact (
                transaction_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL REFERENCES account(account_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                signed_amount_minor INTEGER NOT NULL CHECK (typeof(signed_amount_minor) = 'integer' AND signed_amount_minor <> 0),
                currency_code TEXT NOT NULL CHECK (currency_code = 'ZAR'),
                transaction_date TEXT NOT NULL CHECK (transaction_date GLOB '????-??-??' AND date(transaction_date) = transaction_date),
                posting_date TEXT CHECK (posting_date IS NULL OR (posting_date GLOB '????-??-??' AND date(posting_date) = posting_date)),
                effective_date TEXT GENERATED ALWAYS AS (transaction_date) STORED,
                original_description TEXT NOT NULL CHECK (length(trim(original_description)) > 0),
                recorded_at TEXT NOT NULL CHECK (recorded_at GLOB '????-??-??T??:??:??*Z'),
                recorded_by_os_identity TEXT NOT NULL CHECK (length(trim(recorded_by_os_identity)) > 0)
            );
            CREATE TABLE transaction_lifecycle_event (
                lifecycle_event_id TEXT PRIMARY KEY,
                transaction_id TEXT NOT NULL UNIQUE REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                action TEXT NOT NULL CHECK (action IN ('void', 'superseded', 'statement_authoritative_replacement')),
                replacement_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                CHECK (replacement_transaction_id IS NULL OR replacement_transaction_id <> transaction_id),
                CHECK (
                    (action = 'void' AND replacement_transaction_id IS NULL AND reconciliation_decision_id IS NULL) OR
                    (action = 'superseded' AND replacement_transaction_id IS NOT NULL AND reconciliation_decision_id IS NULL) OR
                    (action = 'statement_authoritative_replacement' AND replacement_transaction_id IS NOT NULL AND reconciliation_decision_id IS NOT NULL)
                )
            );
            CREATE TABLE category_allocation_event (
                allocation_event_id TEXT PRIMARY KEY,
                transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                category_id TEXT NOT NULL REFERENCES spend_category(category_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                action TEXT NOT NULL CHECK (action IN ('assign', 'correct', 'carry_forward')),
                previous_event_id TEXT,
                source_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                UNIQUE (allocation_event_id, transaction_id),
                UNIQUE (previous_event_id),
                FOREIGN KEY (previous_event_id, transaction_id)
                    REFERENCES category_allocation_event(allocation_event_id, transaction_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (previous_event_id IS NULL OR previous_event_id <> allocation_event_id),
                CHECK (source_transaction_id IS NULL OR source_transaction_id <> transaction_id),
                CHECK (
                    (action = 'assign' AND previous_event_id IS NULL AND source_transaction_id IS NULL AND reconciliation_decision_id IS NULL) OR
                    (action = 'correct' AND previous_event_id IS NOT NULL AND source_transaction_id IS NULL) OR
                    (action = 'carry_forward' AND previous_event_id IS NULL AND source_transaction_id IS NOT NULL AND reconciliation_decision_id IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX ux_category_allocation_root_per_transaction
                ON category_allocation_event(transaction_id) WHERE previous_event_id IS NULL;
            CREATE TABLE transaction_attribution_event (
                attribution_event_id TEXT PRIMARY KEY,
                transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                instrument_state TEXT NOT NULL CHECK (instrument_state IN ('known', 'unknown')),
                instrument_id TEXT REFERENCES payment_instrument(instrument_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                cardholder_state TEXT NOT NULL CHECK (cardholder_state IN ('known', 'unknown')),
                cardholder_id TEXT REFERENCES cardholder(cardholder_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                action TEXT NOT NULL CHECK (action IN ('initialize', 'assign', 'correct', 'carry_forward')),
                previous_event_id TEXT,
                source_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                UNIQUE (attribution_event_id, transaction_id),
                UNIQUE (previous_event_id),
                FOREIGN KEY (previous_event_id, transaction_id)
                    REFERENCES transaction_attribution_event(attribution_event_id, transaction_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK ((instrument_state = 'known' AND instrument_id IS NOT NULL) OR (instrument_state = 'unknown' AND instrument_id IS NULL)),
                CHECK ((cardholder_state = 'known' AND cardholder_id IS NOT NULL) OR (cardholder_state = 'unknown' AND cardholder_id IS NULL)),
                CHECK (previous_event_id IS NULL OR previous_event_id <> attribution_event_id),
                CHECK (source_transaction_id IS NULL OR source_transaction_id <> transaction_id),
                CHECK (
                    (action = 'initialize' AND previous_event_id IS NULL AND source_transaction_id IS NULL AND reconciliation_decision_id IS NULL AND instrument_state = 'unknown' AND cardholder_state = 'unknown') OR
                    (action IN ('assign', 'correct') AND previous_event_id IS NOT NULL AND source_transaction_id IS NULL) OR
                    (action = 'carry_forward' AND previous_event_id IS NULL AND source_transaction_id IS NOT NULL AND reconciliation_decision_id IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX ux_transaction_attribution_root_per_transaction
                ON transaction_attribution_event(transaction_id) WHERE previous_event_id IS NULL;
            CREATE TABLE pool_assignment_event (
                pool_assignment_event_id TEXT PRIMARY KEY,
                transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                assignment_state TEXT NOT NULL CHECK (assignment_state IN ('assigned', 'unassigned')),
                pool_id TEXT REFERENCES spend_pool(pool_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                action TEXT NOT NULL CHECK (action IN ('initialize', 'assign', 'correct', 'carry_forward')),
                previous_event_id TEXT,
                source_transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reconciliation_decision_id TEXT REFERENCES reconciliation_decision(decision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                UNIQUE (pool_assignment_event_id, transaction_id),
                UNIQUE (previous_event_id),
                FOREIGN KEY (previous_event_id, transaction_id)
                    REFERENCES pool_assignment_event(pool_assignment_event_id, transaction_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK ((assignment_state = 'assigned' AND pool_id IS NOT NULL) OR (assignment_state = 'unassigned' AND pool_id IS NULL)),
                CHECK (previous_event_id IS NULL OR previous_event_id <> pool_assignment_event_id),
                CHECK (source_transaction_id IS NULL OR source_transaction_id <> transaction_id),
                CHECK (
                    (action = 'initialize' AND previous_event_id IS NULL AND source_transaction_id IS NULL AND reconciliation_decision_id IS NULL AND assignment_state = 'unassigned') OR
                    (action IN ('assign', 'correct') AND previous_event_id IS NOT NULL AND source_transaction_id IS NULL) OR
                    (action = 'carry_forward' AND previous_event_id IS NULL AND source_transaction_id IS NOT NULL AND reconciliation_decision_id IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX ux_pool_assignment_root_per_transaction
                ON pool_assignment_event(transaction_id) WHERE previous_event_id IS NULL;

            CREATE VIEW current_category_allocation AS
                SELECT allocation_event_id, transaction_id, category_id, action, occurred_at
                FROM category_allocation_event AS event
                WHERE NOT EXISTS (
                    SELECT 1 FROM category_allocation_event AS successor
                    WHERE successor.previous_event_id = event.allocation_event_id
                );
            CREATE VIEW current_transaction_attribution AS
                SELECT attribution_event_id, transaction_id, instrument_state, instrument_id, cardholder_state, cardholder_id, action, occurred_at
                FROM transaction_attribution_event AS event
                WHERE NOT EXISTS (
                    SELECT 1 FROM transaction_attribution_event AS successor
                    WHERE successor.previous_event_id = event.attribution_event_id
                );
            CREATE VIEW current_pool_assignment AS
                SELECT pool_assignment_event_id, transaction_id, assignment_state, pool_id, action, occurred_at
                FROM pool_assignment_event AS event
                WHERE NOT EXISTS (
                    SELECT 1 FROM pool_assignment_event AS successor
                    WHERE successor.previous_event_id = event.pool_assignment_event_id
                );

            CREATE TRIGGER transaction_fact_requires_active_account_before_insert
            BEFORE INSERT ON transaction_fact
            BEGIN
                SELECT RAISE(ABORT, 'transaction account must be active')
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalogue_current
                    WHERE catalogue_kind = 'account' AND entity_id = NEW.account_id AND status = 'active'
                );
            END;
            CREATE TRIGGER transaction_lifecycle_replacement_must_be_active_before_insert
            BEFORE INSERT ON transaction_lifecycle_event
            WHEN NEW.replacement_transaction_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'replacement transaction must be active')
                WHERE EXISTS (
                    SELECT 1 FROM transaction_lifecycle_event
                    WHERE transaction_id = NEW.replacement_transaction_id
                );
                SELECT RAISE(ABORT, 'transaction replacement cycle')
                WHERE EXISTS (
                    WITH RECURSIVE replacements(transaction_id) AS (
                        SELECT NEW.replacement_transaction_id
                        UNION ALL
                        SELECT lifecycle.replacement_transaction_id
                        FROM transaction_lifecycle_event AS lifecycle
                        JOIN replacements ON replacements.transaction_id = lifecycle.transaction_id
                        WHERE lifecycle.replacement_transaction_id IS NOT NULL
                    )
                    SELECT 1 FROM replacements WHERE transaction_id = NEW.transaction_id
                );
            END;

            CREATE TRIGGER category_allocation_sequence_before_insert
            BEFORE INSERT ON category_allocation_event
            BEGIN
                SELECT RAISE(ABORT, 'category allocation predecessor is not current')
                WHERE NEW.action = 'correct'
                  AND NOT EXISTS (
                      SELECT 1 FROM current_category_allocation
                      WHERE allocation_event_id = NEW.previous_event_id AND transaction_id = NEW.transaction_id
                  );
                SELECT RAISE(ABORT, 'category allocation requires an active transaction')
                WHERE EXISTS (SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = NEW.transaction_id);
                SELECT RAISE(ABORT, 'category allocation requires an active category')
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalogue_current
                    WHERE catalogue_kind = 'category' AND entity_id = NEW.category_id AND status = 'active'
                );
            END;
            CREATE TRIGGER transaction_attribution_sequence_before_insert
            BEFORE INSERT ON transaction_attribution_event
            BEGIN
                SELECT RAISE(ABORT, 'transaction attribution predecessor is not current')
                WHERE NEW.action IN ('assign', 'correct')
                  AND NOT EXISTS (
                      SELECT 1 FROM current_transaction_attribution
                      WHERE attribution_event_id = NEW.previous_event_id AND transaction_id = NEW.transaction_id
                  );
                SELECT RAISE(ABORT, 'transaction attribution requires an active transaction')
                WHERE EXISTS (SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = NEW.transaction_id);
                SELECT RAISE(ABORT, 'transaction attribution requires an active payment instrument')
                WHERE NEW.instrument_state = 'known'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE catalogue_kind = 'payment_instrument' AND entity_id = NEW.instrument_id AND status = 'active'
                  );
                SELECT RAISE(ABORT, 'transaction attribution requires an active cardholder')
                WHERE NEW.cardholder_state = 'known'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE catalogue_kind = 'cardholder' AND entity_id = NEW.cardholder_id AND status = 'active'
                  );
            END;
            CREATE TRIGGER pool_assignment_sequence_before_insert
            BEFORE INSERT ON pool_assignment_event
            BEGIN
                SELECT RAISE(ABORT, 'pool assignment predecessor is not current')
                WHERE NEW.action IN ('assign', 'correct')
                  AND NOT EXISTS (
                      SELECT 1 FROM current_pool_assignment
                      WHERE pool_assignment_event_id = NEW.previous_event_id AND transaction_id = NEW.transaction_id
                  );
                SELECT RAISE(ABORT, 'pool assignment requires an active transaction')
                WHERE EXISTS (SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = NEW.transaction_id);
                SELECT RAISE(ABORT, 'pool assignment requires an active Spend Pool')
                WHERE NEW.assignment_state = 'assigned'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE catalogue_kind = 'spend_pool' AND entity_id = NEW.pool_id AND status = 'active'
                  );
            END;

            CREATE TRIGGER transaction_fact_is_immutable_before_update BEFORE UPDATE ON transaction_fact BEGIN SELECT RAISE(ABORT, 'transaction facts are immutable'); END;
            CREATE TRIGGER transaction_fact_is_immutable_before_delete BEFORE DELETE ON transaction_fact BEGIN SELECT RAISE(ABORT, 'transaction facts cannot be deleted'); END;
            CREATE TRIGGER transaction_lifecycle_is_immutable_before_update BEFORE UPDATE ON transaction_lifecycle_event BEGIN SELECT RAISE(ABORT, 'transaction lifecycle is immutable'); END;
            CREATE TRIGGER transaction_lifecycle_is_immutable_before_delete BEFORE DELETE ON transaction_lifecycle_event BEGIN SELECT RAISE(ABORT, 'transaction lifecycle is immutable'); END;
            CREATE TRIGGER category_allocation_is_immutable_before_update BEFORE UPDATE ON category_allocation_event BEGIN SELECT RAISE(ABORT, 'category allocation history is immutable'); END;
            CREATE TRIGGER category_allocation_is_immutable_before_delete BEFORE DELETE ON category_allocation_event BEGIN SELECT RAISE(ABORT, 'category allocation history is immutable'); END;
            CREATE TRIGGER transaction_attribution_is_immutable_before_update BEFORE UPDATE ON transaction_attribution_event BEGIN SELECT RAISE(ABORT, 'transaction attribution history is immutable'); END;
            CREATE TRIGGER transaction_attribution_is_immutable_before_delete BEFORE DELETE ON transaction_attribution_event BEGIN SELECT RAISE(ABORT, 'transaction attribution history is immutable'); END;
            CREATE TRIGGER pool_assignment_is_immutable_before_update BEFORE UPDATE ON pool_assignment_event BEGIN SELECT RAISE(ABORT, 'pool assignment history is immutable'); END;
            CREATE TRIGGER pool_assignment_is_immutable_before_delete BEFORE DELETE ON pool_assignment_event BEGIN SELECT RAISE(ABORT, 'pool assignment history is immutable'); END;

            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (1, 'v001_transaction', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
