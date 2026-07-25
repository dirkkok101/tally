using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Ingest.Storage.Migrations;

public sealed class IngestMigrationV001
{
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE ingest_batch (
                batch_id TEXT PRIMARY KEY,
                source_fingerprint TEXT NOT NULL,
                selected_account_id TEXT NOT NULL,
                adapter_identity TEXT NOT NULL,
                ledger_contract_version TEXT NOT NULL,
                manifest_schema_version TEXT NOT NULL,
                period_start TEXT,
                period_end TEXT,
                status INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3, 4, 5, 6)),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE manifest_revision (
                manifest_revision_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL REFERENCES ingest_batch(batch_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                revision_number INTEGER NOT NULL,
                canonical_digest TEXT NOT NULL,
                committable INTEGER NOT NULL CHECK (committable IN (0, 1)),
                created_at TEXT NOT NULL,
                UNIQUE (batch_id, revision_number)
            );
            CREATE TABLE source_record_outcome (
                manifest_revision_id TEXT NOT NULL REFERENCES manifest_revision(manifest_revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                source_record_id TEXT NOT NULL,
                record_order INTEGER NOT NULL,
                disposition INTEGER NOT NULL CHECK (disposition IN (0, 1, 2, 3)),
                reason_code TEXT NOT NULL,
                candidate_id TEXT,
                prior_canonical_ref TEXT,
                PRIMARY KEY (manifest_revision_id, source_record_id)
            );
            CREATE TABLE import_candidate (
                candidate_id TEXT PRIMARY KEY,
                manifest_revision_id TEXT NOT NULL REFERENCES manifest_revision(manifest_revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                source_record_id TEXT NOT NULL,
                immutable_facts_json TEXT NOT NULL,
                frozen_ledger_request_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                commit_state INTEGER NOT NULL CHECK (commit_state IN (0, 1, 2, 3, 4, 5, 6))
            );
            CREATE TABLE reconciliation_control (
                manifest_revision_id TEXT NOT NULL REFERENCES manifest_revision(manifest_revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                control_order INTEGER NOT NULL,
                kind INTEGER NOT NULL CHECK (kind IN (0, 1, 2, 3, 4)),
                availability INTEGER NOT NULL CHECK (availability IN (0, 1, 2)),
                evidence_json TEXT,
                PRIMARY KEY (manifest_revision_id, control_order)
            );
            CREATE TABLE manifest_approval (
                approval_id TEXT PRIMARY KEY,
                manifest_revision_id TEXT NOT NULL REFERENCES manifest_revision(manifest_revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                manifest_digest TEXT NOT NULL,
                actor TEXT NOT NULL,
                trusted_os_identity TEXT NOT NULL,
                approved_at TEXT NOT NULL,
                active INTEGER NOT NULL CHECK (active IN (0, 1))
            );
            CREATE TABLE import_receipt (
                receipt_id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL REFERENCES ingest_batch(batch_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                status INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3, 4)),
                summary_json TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE TABLE candidate_receipt (
                receipt_id TEXT NOT NULL REFERENCES import_receipt(receipt_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                candidate_id TEXT NOT NULL,
                outcome INTEGER NOT NULL CHECK (outcome IN (0, 1, 2, 3, 4, 5, 6)),
                ledger_transaction_id TEXT,
                error_code TEXT,
                attempted_at TEXT,
                terminal_at TEXT,
                PRIMARY KEY (receipt_id, candidate_id)
            );
            CREATE TRIGGER manifest_revision_number_is_immutable
            BEFORE UPDATE OF revision_number ON manifest_revision
            BEGIN
                SELECT RAISE(ABORT, 'manifest revision number is immutable');
            END;
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
