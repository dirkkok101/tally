using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Ingest.Storage.Migrations;

public sealed class IngestMigrationV002
{
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE ingest_store_metadata (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                generation_id TEXT NOT NULL UNIQUE
            );
            INSERT INTO ingest_store_metadata (singleton_id, generation_id)
            VALUES (1, lower(hex(randomblob(16))));

            CREATE TABLE batch_error_event (
                error_event_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL REFERENCES ingest_batch(batch_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                code TEXT NOT NULL,
                category INTEGER NOT NULL CHECK (category BETWEEN 0 AND 12),
                safe_message TEXT NOT NULL,
                candidate_id TEXT,
                mutation_possibility INTEGER NOT NULL CHECK (mutation_possibility BETWEEN 0 AND 2),
                durable_state TEXT,
                retry_action INTEGER NOT NULL CHECK (retry_action BETWEEN 0 AND 5),
                field TEXT,
                recorded_at TEXT NOT NULL,
                UNIQUE (batch_id, sequence)
            );

            CREATE TABLE status_snapshot (
                snapshot_id TEXT PRIMARY KEY,
                contract_version TEXT NOT NULL,
                store_generation TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                total_count INTEGER NOT NULL CHECK (total_count >= 0)
            );

            CREATE TABLE status_snapshot_item (
                snapshot_id TEXT NOT NULL REFERENCES status_snapshot(snapshot_id) ON DELETE CASCADE ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                batch_status_summary_json TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, ordinal)
            );

            CREATE TRIGGER batch_error_event_is_append_only_update
            BEFORE UPDATE ON batch_error_event
            BEGIN
                SELECT RAISE(ABORT, 'batch error events are append-only');
            END;

            CREATE TRIGGER batch_error_event_is_append_only_delete
            BEFORE DELETE ON batch_error_event
            BEGIN
                SELECT RAISE(ABORT, 'batch error events are append-only');
            END;

            CREATE TRIGGER status_snapshot_is_immutable
            BEFORE UPDATE ON status_snapshot
            BEGIN
                SELECT RAISE(ABORT, 'status snapshots are immutable');
            END;

            CREATE TRIGGER status_snapshot_item_is_immutable
            BEFORE UPDATE ON status_snapshot_item
            BEGIN
                SELECT RAISE(ABORT, 'status snapshot membership is immutable');
            END;

            CREATE TRIGGER status_snapshot_item_count_is_bounded
            BEFORE INSERT ON status_snapshot_item
            WHEN NEW.ordinal >= (
                SELECT total_count
                FROM status_snapshot
                WHERE snapshot_id = NEW.snapshot_id)
            BEGIN
                SELECT RAISE(ABORT, 'status snapshot membership exceeds its declared count');
            END;

            CREATE TRIGGER status_snapshot_item_delete_requires_parent_cleanup
            BEFORE DELETE ON status_snapshot_item
            WHEN EXISTS (
                SELECT 1
                FROM status_snapshot
                WHERE snapshot_id = OLD.snapshot_id)
            BEGIN
                SELECT RAISE(ABORT, 'status snapshot membership is immutable');
            END;
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
