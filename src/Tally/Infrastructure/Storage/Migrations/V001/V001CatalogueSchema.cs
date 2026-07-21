using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V001;

public sealed class V001CatalogueSchema : ILedgerSchemaFragment
{
    public const string FragmentName = "v001_catalogue";
    public int Version => 1;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE account (
                account_id TEXT PRIMARY KEY,
                institution_name TEXT NOT NULL CHECK (length(trim(institution_name)) > 0),
                account_type TEXT NOT NULL CHECK (account_type IN ('cheque', 'savings', 'credit_card', 'other_asset', 'other_liability')),
                account_class TEXT NOT NULL CHECK (account_class IN ('asset', 'liability')),
                masked_identifier TEXT NOT NULL CHECK (length(trim(masked_identifier)) > 0),
                currency_code TEXT NOT NULL CHECK (currency_code = 'ZAR'),
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z'),
                CHECK (
                    (account_type IN ('cheque', 'savings', 'other_asset') AND account_class = 'asset') OR
                    (account_type IN ('credit_card', 'other_liability') AND account_class = 'liability')
                )
            );
            CREATE TABLE spend_category (
                category_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z')
            );
            CREATE TABLE payment_instrument (
                instrument_id TEXT PRIMARY KEY,
                account_id TEXT REFERENCES account(account_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                masked_suffix TEXT CHECK (masked_suffix IS NULL OR (length(masked_suffix) BETWEEN 1 AND 4 AND masked_suffix NOT GLOB '*[^0-9]*')),
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z')
            );
            CREATE TABLE cardholder (
                cardholder_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z')
            );
            CREATE TABLE spend_pool (
                pool_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??*Z')
            );
            CREATE TABLE catalogue_lifecycle_event (
                lifecycle_event_id TEXT PRIMARY KEY,
                catalogue_kind TEXT NOT NULL CHECK (catalogue_kind IN ('account', 'category', 'payment_instrument', 'cardholder', 'spend_pool')),
                entity_id TEXT NOT NULL,
                action TEXT NOT NULL CHECK (action IN ('create', 'rename', 'archive', 'reactivate')),
                previous_label TEXT,
                new_label TEXT,
                normalized_label TEXT NOT NULL CHECK (length(trim(normalized_label)) > 0),
                reason TEXT CHECK (reason IS NULL OR length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                previous_event_id TEXT,
                UNIQUE (lifecycle_event_id, catalogue_kind, entity_id),
                UNIQUE (previous_event_id),
                FOREIGN KEY (previous_event_id, catalogue_kind, entity_id)
                    REFERENCES catalogue_lifecycle_event(lifecycle_event_id, catalogue_kind, entity_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (previous_event_id IS NULL OR previous_event_id <> lifecycle_event_id),
                CHECK (
                    (action = 'create' AND previous_event_id IS NULL AND previous_label IS NULL AND new_label IS NOT NULL) OR
                    (action = 'rename' AND previous_event_id IS NOT NULL AND previous_label IS NOT NULL AND new_label IS NOT NULL) OR
                    (action = 'archive' AND previous_event_id IS NOT NULL AND previous_label IS NOT NULL AND new_label IS NULL) OR
                    (action = 'reactivate' AND previous_event_id IS NOT NULL AND previous_label IS NOT NULL AND new_label IS NOT NULL)
                ),
                CHECK (action = 'create' OR reason IS NOT NULL),
                CHECK (action <> 'reactivate' OR new_label = previous_label),
                CHECK (normalized_label = lower(trim(COALESCE(new_label, previous_label))))
            );
            CREATE TABLE category_parent_event (
                parent_event_id TEXT PRIMARY KEY,
                category_id TEXT NOT NULL REFERENCES spend_category(category_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                parent_category_id TEXT REFERENCES spend_category(category_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                action TEXT NOT NULL CHECK (action IN ('initialize', 'reparent')),
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL CHECK (occurred_at GLOB '????-??-??T??:??:??*Z'),
                previous_parent_event_id TEXT,
                UNIQUE (parent_event_id, category_id),
                UNIQUE (previous_parent_event_id),
                FOREIGN KEY (previous_parent_event_id, category_id)
                    REFERENCES category_parent_event(parent_event_id, category_id)
                    ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (category_id <> parent_category_id),
                CHECK (previous_parent_event_id IS NULL OR previous_parent_event_id <> parent_event_id),
                CHECK (
                    (action = 'initialize' AND previous_parent_event_id IS NULL) OR
                    (action = 'reparent' AND previous_parent_event_id IS NOT NULL)
                )
            );

            CREATE VIEW catalogue_current AS
                SELECT
                    event.lifecycle_event_id,
                    event.catalogue_kind,
                    event.entity_id,
                    COALESCE(event.new_label, event.previous_label) AS label,
                    event.normalized_label,
                    CASE event.action WHEN 'archive' THEN 'archived' ELSE 'active' END AS status,
                    event.occurred_at AS changed_at
                FROM catalogue_lifecycle_event AS event
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM catalogue_lifecycle_event AS successor
                    WHERE successor.previous_event_id = event.lifecycle_event_id
                );
            CREATE VIEW category_parent_current AS
                SELECT event.parent_event_id, event.category_id, event.parent_category_id
                FROM category_parent_event AS event
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM category_parent_event AS successor
                    WHERE successor.previous_parent_event_id = event.parent_event_id
                );
            CREATE VIEW current_category_projection AS
                WITH RECURSIVE category_tree(category_id, name, normalized_sibling_name, parent_category_id, depth, ancestry_ids, status) AS (
                    SELECT
                        category.category_id,
                        lifecycle.label,
                        lifecycle.normalized_label,
                        parent.parent_category_id,
                        0,
                        '/' || category.category_id || '/',
                        lifecycle.status
                    FROM spend_category AS category
                    JOIN catalogue_current AS lifecycle
                        ON lifecycle.catalogue_kind = 'category' AND lifecycle.entity_id = category.category_id
                    JOIN category_parent_current AS parent ON parent.category_id = category.category_id
                    WHERE parent.parent_category_id IS NULL
                    UNION ALL
                    SELECT
                        category.category_id,
                        lifecycle.label,
                        lifecycle.normalized_label,
                        parent.parent_category_id,
                        tree.depth + 1,
                        tree.ancestry_ids || category.category_id || '/',
                        lifecycle.status
                    FROM spend_category AS category
                    JOIN catalogue_current AS lifecycle
                        ON lifecycle.catalogue_kind = 'category' AND lifecycle.entity_id = category.category_id
                    JOIN category_parent_current AS parent ON parent.category_id = category.category_id
                    JOIN category_tree AS tree ON tree.category_id = parent.parent_category_id
                )
                SELECT category_id, name, normalized_sibling_name, parent_category_id, depth, ancestry_ids, status
                FROM category_tree;

            CREATE TRIGGER catalogue_lifecycle_entity_exists_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            BEGIN
                SELECT RAISE(ABORT, 'catalogue lifecycle entity does not exist')
                WHERE
                    (NEW.catalogue_kind = 'account' AND NOT EXISTS (SELECT 1 FROM account WHERE account_id = NEW.entity_id)) OR
                    (NEW.catalogue_kind = 'category' AND NOT EXISTS (SELECT 1 FROM spend_category WHERE category_id = NEW.entity_id)) OR
                    (NEW.catalogue_kind = 'payment_instrument' AND NOT EXISTS (SELECT 1 FROM payment_instrument WHERE instrument_id = NEW.entity_id)) OR
                    (NEW.catalogue_kind = 'cardholder' AND NOT EXISTS (SELECT 1 FROM cardholder WHERE cardholder_id = NEW.entity_id)) OR
                    (NEW.catalogue_kind = 'spend_pool' AND NOT EXISTS (SELECT 1 FROM spend_pool WHERE pool_id = NEW.entity_id));
            END;
            CREATE TRIGGER catalogue_lifecycle_sequence_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            BEGIN
                SELECT RAISE(ABORT, 'catalogue lifecycle already initialized')
                WHERE NEW.action = 'create'
                  AND EXISTS (
                      SELECT 1 FROM catalogue_lifecycle_event
                      WHERE catalogue_kind = NEW.catalogue_kind AND entity_id = NEW.entity_id
                  );
                SELECT RAISE(ABORT, 'catalogue lifecycle predecessor is not current')
                WHERE NEW.action <> 'create'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE lifecycle_event_id = NEW.previous_event_id
                        AND catalogue_kind = NEW.catalogue_kind
                        AND entity_id = NEW.entity_id
                  );
                SELECT RAISE(ABORT, 'catalogue lifecycle transition is invalid')
                WHERE
                    (NEW.action IN ('rename', 'archive') AND NOT EXISTS (
                        SELECT 1 FROM catalogue_current
                        WHERE lifecycle_event_id = NEW.previous_event_id AND status = 'active'
                    )) OR
                    (NEW.action = 'reactivate' AND NOT EXISTS (
                        SELECT 1 FROM catalogue_current
                        WHERE lifecycle_event_id = NEW.previous_event_id AND status = 'archived'
                    ));
                SELECT RAISE(ABORT, 'catalogue lifecycle previous label is stale')
                WHERE NEW.action <> 'create'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE lifecycle_event_id = NEW.previous_event_id AND label = NEW.previous_label
                  );
            END;
            CREATE TRIGGER account_active_masked_identity_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            WHEN NEW.catalogue_kind = 'account' AND NEW.action IN ('create', 'reactivate')
            BEGIN
                SELECT RAISE(ABORT, 'active account masked identity already exists')
                WHERE EXISTS (
                    SELECT 1
                    FROM account AS candidate
                    JOIN account AS requested ON requested.account_id = NEW.entity_id
                    JOIN catalogue_current AS lifecycle
                        ON lifecycle.catalogue_kind = 'account'
                       AND lifecycle.entity_id = candidate.account_id
                       AND lifecycle.status = 'active'
                    WHERE candidate.account_id <> NEW.entity_id
                      AND candidate.institution_name = requested.institution_name
                      AND candidate.masked_identifier = requested.masked_identifier
                );
            END;
            CREATE TRIGGER category_lifecycle_requires_parent_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            WHEN NEW.catalogue_kind = 'category' AND NEW.action = 'create'
            BEGIN
                SELECT RAISE(ABORT, 'category parent must be initialized first')
                WHERE NOT EXISTS (SELECT 1 FROM category_parent_current WHERE category_id = NEW.entity_id);
            END;
            CREATE TRIGGER category_lifecycle_requires_active_parent_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            WHEN NEW.catalogue_kind = 'category' AND NEW.action IN ('create', 'reactivate')
            BEGIN
                SELECT RAISE(ABORT, 'category ancestry must be active')
                WHERE EXISTS (
                    SELECT 1
                    FROM category_parent_current AS parent
                    WHERE parent.category_id = NEW.entity_id
                      AND parent.parent_category_id IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM catalogue_current AS lifecycle
                          WHERE lifecycle.catalogue_kind = 'category'
                            AND lifecycle.entity_id = parent.parent_category_id
                            AND lifecycle.status = 'active'
                      )
                );
            END;
            CREATE TRIGGER category_active_sibling_name_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            WHEN NEW.catalogue_kind = 'category' AND NEW.action IN ('create', 'rename', 'reactivate')
            BEGIN
                SELECT RAISE(ABORT, 'active category name already exists among siblings')
                WHERE EXISTS (
                    SELECT 1
                    FROM catalogue_current AS sibling_lifecycle
                    JOIN category_parent_current AS sibling_parent ON sibling_parent.category_id = sibling_lifecycle.entity_id
                    JOIN category_parent_current AS requested_parent ON requested_parent.category_id = NEW.entity_id
                    WHERE sibling_lifecycle.catalogue_kind = 'category'
                      AND sibling_lifecycle.status = 'active'
                      AND sibling_lifecycle.entity_id <> NEW.entity_id
                      AND sibling_lifecycle.normalized_label = NEW.normalized_label
                      AND sibling_parent.parent_category_id IS requested_parent.parent_category_id
                );
            END;
            CREATE TRIGGER category_archive_without_active_children_before_insert
            BEFORE INSERT ON catalogue_lifecycle_event
            WHEN NEW.catalogue_kind = 'category' AND NEW.action = 'archive'
            BEGIN
                SELECT RAISE(ABORT, 'category with active children cannot be archived')
                WHERE EXISTS (
                    SELECT 1
                    FROM category_parent_current AS child_parent
                    JOIN catalogue_current AS child_lifecycle
                        ON child_lifecycle.catalogue_kind = 'category'
                       AND child_lifecycle.entity_id = child_parent.category_id
                       AND child_lifecycle.status = 'active'
                    WHERE child_parent.parent_category_id = NEW.entity_id
                );
            END;

            CREATE TRIGGER category_parent_sequence_before_insert
            BEFORE INSERT ON category_parent_event
            BEGIN
                SELECT RAISE(ABORT, 'category parent already initialized')
                WHERE NEW.action = 'initialize'
                  AND EXISTS (SELECT 1 FROM category_parent_event WHERE category_id = NEW.category_id);
                SELECT RAISE(ABORT, 'category parent predecessor is not current')
                WHERE NEW.action = 'reparent'
                  AND NOT EXISTS (
                      SELECT 1 FROM category_parent_current
                      WHERE parent_event_id = NEW.previous_parent_event_id AND category_id = NEW.category_id
                  );
            END;
            CREATE TRIGGER category_parent_requires_active_nodes_before_insert
            BEFORE INSERT ON category_parent_event
            BEGIN
                SELECT RAISE(ABORT, 'category reparent target must be active')
                WHERE NEW.action = 'reparent'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE catalogue_kind = 'category' AND entity_id = NEW.category_id AND status = 'active'
                  );
                SELECT RAISE(ABORT, 'category parent must be active')
                WHERE NEW.parent_category_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM catalogue_current
                      WHERE catalogue_kind = 'category' AND entity_id = NEW.parent_category_id AND status = 'active'
                  );
            END;
            CREATE TRIGGER category_parent_cycle_before_insert
            BEFORE INSERT ON category_parent_event
            WHEN NEW.parent_category_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'category hierarchy cycle')
                WHERE EXISTS (
                    WITH RECURSIVE ancestors(category_id) AS (
                        SELECT NEW.parent_category_id
                        UNION ALL
                        SELECT parent.parent_category_id
                        FROM category_parent_current AS parent
                        JOIN ancestors ON ancestors.category_id = parent.category_id
                        WHERE parent.parent_category_id IS NOT NULL
                    )
                    SELECT 1 FROM ancestors WHERE category_id = NEW.category_id
                );
            END;
            CREATE TRIGGER category_reparent_unique_sibling_name_before_insert
            BEFORE INSERT ON category_parent_event
            WHEN NEW.action = 'reparent'
            BEGIN
                SELECT RAISE(ABORT, 'active category name already exists among siblings')
                WHERE EXISTS (
                    SELECT 1
                    FROM catalogue_current AS requested_lifecycle
                    JOIN catalogue_current AS sibling_lifecycle
                        ON sibling_lifecycle.catalogue_kind = 'category'
                       AND sibling_lifecycle.status = 'active'
                       AND sibling_lifecycle.normalized_label = requested_lifecycle.normalized_label
                       AND sibling_lifecycle.entity_id <> NEW.category_id
                    JOIN category_parent_current AS sibling_parent ON sibling_parent.category_id = sibling_lifecycle.entity_id
                    WHERE requested_lifecycle.catalogue_kind = 'category'
                      AND requested_lifecycle.entity_id = NEW.category_id
                      AND requested_lifecycle.status = 'active'
                      AND sibling_parent.parent_category_id IS NEW.parent_category_id
                );
            END;

            CREATE TRIGGER account_is_immutable_before_update BEFORE UPDATE ON account BEGIN SELECT RAISE(ABORT, 'accounts are immutable'); END;
            CREATE TRIGGER account_is_immutable_before_delete BEFORE DELETE ON account BEGIN SELECT RAISE(ABORT, 'accounts cannot be deleted'); END;
            CREATE TRIGGER spend_category_is_immutable_before_update BEFORE UPDATE ON spend_category BEGIN SELECT RAISE(ABORT, 'categories are immutable'); END;
            CREATE TRIGGER spend_category_is_immutable_before_delete BEFORE DELETE ON spend_category BEGIN SELECT RAISE(ABORT, 'categories cannot be deleted'); END;
            CREATE TRIGGER payment_instrument_is_immutable_before_update BEFORE UPDATE ON payment_instrument BEGIN SELECT RAISE(ABORT, 'payment instruments are immutable'); END;
            CREATE TRIGGER payment_instrument_is_immutable_before_delete BEFORE DELETE ON payment_instrument BEGIN SELECT RAISE(ABORT, 'payment instruments cannot be deleted'); END;
            CREATE TRIGGER cardholder_is_immutable_before_update BEFORE UPDATE ON cardholder BEGIN SELECT RAISE(ABORT, 'cardholders are immutable'); END;
            CREATE TRIGGER cardholder_is_immutable_before_delete BEFORE DELETE ON cardholder BEGIN SELECT RAISE(ABORT, 'cardholders cannot be deleted'); END;
            CREATE TRIGGER spend_pool_is_immutable_before_update BEFORE UPDATE ON spend_pool BEGIN SELECT RAISE(ABORT, 'spend pools are immutable'); END;
            CREATE TRIGGER spend_pool_is_immutable_before_delete BEFORE DELETE ON spend_pool BEGIN SELECT RAISE(ABORT, 'spend pools cannot be deleted'); END;
            CREATE TRIGGER catalogue_lifecycle_is_immutable_before_update BEFORE UPDATE ON catalogue_lifecycle_event BEGIN SELECT RAISE(ABORT, 'catalogue lifecycle is immutable'); END;
            CREATE TRIGGER catalogue_lifecycle_is_immutable_before_delete BEFORE DELETE ON catalogue_lifecycle_event BEGIN SELECT RAISE(ABORT, 'catalogue lifecycle is immutable'); END;
            CREATE TRIGGER category_parent_is_immutable_before_update BEFORE UPDATE ON category_parent_event BEGIN SELECT RAISE(ABORT, 'category parent history is immutable'); END;
            CREATE TRIGGER category_parent_is_immutable_before_delete BEFORE DELETE ON category_parent_event BEGIN SELECT RAISE(ABORT, 'category parent history is immutable'); END;

            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (1, 'v001_catalogue', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
