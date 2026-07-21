using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V001;

public sealed class V001EvidenceReconciliationSchema : ILedgerSchemaFragment
{
    public const string FragmentName = "z_evidence_reconciliation";
    public int Version => 1;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE evidence_record (
                evidence_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('agent_capture', 'statement_row', 'receipt', 'external_document', 'owner_assertion')),
                logical_identity_digest TEXT NOT NULL UNIQUE,
                opaque_external_reference TEXT,
                content_fingerprint TEXT,
                recorded_by TEXT NOT NULL,
                recorded_at TEXT NOT NULL
            );
            CREATE TABLE evidence_observation (
                evidence_id TEXT PRIMARY KEY REFERENCES evidence_record(evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                account_id TEXT REFERENCES account(account_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                signed_amount_minor INTEGER,
                currency_code TEXT CHECK (currency_code IS NULL OR currency_code = 'ZAR'),
                transaction_date TEXT,
                posting_date TEXT,
                instrument_id TEXT REFERENCES payment_instrument(instrument_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                cardholder_id TEXT REFERENCES cardholder(cardholder_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                description_fingerprint TEXT
            );
            CREATE TABLE reconciliation_decision (
                decision_id TEXT PRIMARY KEY,
                evidence_id TEXT NOT NULL REFERENCES evidence_record(evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                disposition TEXT NOT NULL CHECK (disposition IN ('deterministic_match', 'statement_only', 'ambiguous', 'exception', 'owner_confirmed', 'rejected', 'revoked', 'replaced')),
                policy_id TEXT,
                policy_version TEXT,
                match_basis TEXT NOT NULL,
                deterministic INTEGER NOT NULL CHECK (deterministic IN (0, 1)),
                reason TEXT NOT NULL,
                decided_by TEXT NOT NULL,
                decided_at TEXT NOT NULL,
                previous_decision_id TEXT,
                UNIQUE (decision_id, evidence_id),
                UNIQUE (previous_decision_id),
                FOREIGN KEY (previous_decision_id, evidence_id) REFERENCES reconciliation_decision(decision_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (previous_decision_id IS NULL OR previous_decision_id <> decision_id),
                CHECK ((policy_id IS NULL AND policy_version IS NULL) OR (policy_id IS NOT NULL AND policy_version IS NOT NULL))
            );
            CREATE UNIQUE INDEX ux_reconciliation_decision_root_per_evidence
                ON reconciliation_decision(evidence_id) WHERE previous_decision_id IS NULL;
            CREATE TABLE evidence_link_event (
                link_event_id TEXT PRIMARY KEY,
                evidence_id TEXT NOT NULL REFERENCES evidence_record(evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                transaction_id TEXT NOT NULL REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                role TEXT NOT NULL CHECK (role IN ('supporting', 'confirming')),
                action TEXT NOT NULL CHECK (action IN ('link', 'revoke', 'replace')),
                decision_id TEXT,
                reason TEXT NOT NULL,
                recorded_by TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                previous_link_event_id TEXT,
                UNIQUE (link_event_id, evidence_id, role),
                UNIQUE (previous_link_event_id),
                FOREIGN KEY (decision_id, evidence_id) REFERENCES reconciliation_decision(decision_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                FOREIGN KEY (previous_link_event_id, evidence_id, role) REFERENCES evidence_link_event(link_event_id, evidence_id, role) ON DELETE RESTRICT ON UPDATE RESTRICT,
                CHECK (previous_link_event_id IS NULL OR previous_link_event_id <> link_event_id),
                CHECK (role = 'supporting' OR decision_id IS NOT NULL),
                CHECK (role = 'supporting' OR action <> 'revoke' OR previous_link_event_id IS NOT NULL)
            );
            CREATE UNIQUE INDEX ux_confirming_link_root_per_evidence
                ON evidence_link_event(evidence_id) WHERE role = 'confirming' AND previous_link_event_id IS NULL;
            CREATE VIEW evidence_active_confirming_target AS
                SELECT link_event_id, evidence_id, transaction_id, decision_id
                FROM evidence_link_event AS link_event
                WHERE role = 'confirming'
                  AND action IN ('link', 'replace')
                  AND NOT EXISTS (
                      SELECT 1 FROM evidence_link_event AS successor
                      WHERE successor.previous_link_event_id = link_event.link_event_id);
            CREATE VIEW reconciliation_current AS
                SELECT decision_id, evidence_id, transaction_id, disposition, match_basis, deterministic, decided_at
                FROM reconciliation_decision AS decision
                WHERE NOT EXISTS (
                    SELECT 1 FROM reconciliation_decision AS successor
                    WHERE successor.previous_decision_id = decision.decision_id);
            CREATE TABLE statement_scope (
                scope_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL REFERENCES account(account_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                period_start TEXT NOT NULL,
                period_end TEXT NOT NULL,
                manifest_opaque_reference TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('open', 'completed', 'replaced')),
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                CHECK (period_start <= period_end)
            );
            CREATE TABLE statement_scope_evidence (
                scope_id TEXT NOT NULL REFERENCES statement_scope(scope_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                evidence_id TEXT NOT NULL REFERENCES evidence_record(evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                PRIMARY KEY (scope_id, evidence_id)
            );
            CREATE TABLE coverage_entry (
                coverage_entry_id TEXT PRIMARY KEY,
                scope_id TEXT NOT NULL,
                evidence_id TEXT NOT NULL,
                transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                outcome TEXT NOT NULL CHECK (outcome IN ('recorded_unreconciled', 'statement_reconciled', 'statement_only', 'recorded_absent_from_statement', 'ambiguous_match', 'owner_confirmed_match', 'reconciliation_exception')),
                reason TEXT NOT NULL,
                active_decision_id TEXT,
                recorded_by TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                FOREIGN KEY (scope_id, evidence_id) REFERENCES statement_scope_evidence(scope_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                FOREIGN KEY (active_decision_id, evidence_id) REFERENCES reconciliation_decision(decision_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT
            );
            CREATE TABLE reconciliation_exception (
                exception_id TEXT PRIMARY KEY,
                scope_id TEXT NOT NULL,
                evidence_id TEXT NOT NULL,
                transaction_id TEXT REFERENCES transaction_fact(transaction_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                disposition TEXT NOT NULL CHECK (disposition IN ('ambiguous', 'exception', 'rejected')),
                reason TEXT NOT NULL,
                active_decision_id TEXT,
                recorded_by TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                FOREIGN KEY (scope_id, evidence_id) REFERENCES statement_scope_evidence(scope_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                FOREIGN KEY (active_decision_id, evidence_id) REFERENCES reconciliation_decision(decision_id, evidence_id) ON DELETE RESTRICT ON UPDATE RESTRICT
            );
            CREATE TRIGGER evidence_record_is_immutable_before_update BEFORE UPDATE ON evidence_record BEGIN SELECT RAISE(ABORT, 'evidence records are immutable'); END;
            CREATE TRIGGER evidence_record_is_immutable_before_delete BEFORE DELETE ON evidence_record BEGIN SELECT RAISE(ABORT, 'evidence records are immutable'); END;
            CREATE TRIGGER evidence_observation_is_immutable_before_update BEFORE UPDATE ON evidence_observation BEGIN SELECT RAISE(ABORT, 'evidence observations are immutable'); END;
            CREATE TRIGGER evidence_observation_is_immutable_before_delete BEFORE DELETE ON evidence_observation BEGIN SELECT RAISE(ABORT, 'evidence observations are immutable'); END;
            CREATE TRIGGER reconciliation_decision_is_immutable_before_update BEFORE UPDATE ON reconciliation_decision BEGIN SELECT RAISE(ABORT, 'reconciliation decisions are immutable'); END;
            CREATE TRIGGER reconciliation_decision_is_immutable_before_delete BEFORE DELETE ON reconciliation_decision BEGIN SELECT RAISE(ABORT, 'reconciliation decisions are immutable'); END;
            CREATE TRIGGER evidence_link_event_is_immutable_before_update BEFORE UPDATE ON evidence_link_event BEGIN SELECT RAISE(ABORT, 'evidence links are immutable'); END;
            CREATE TRIGGER evidence_link_event_is_immutable_before_delete BEFORE DELETE ON evidence_link_event BEGIN SELECT RAISE(ABORT, 'evidence links are immutable'); END;
            CREATE TRIGGER confirming_link_requires_statement_evidence BEFORE INSERT ON evidence_link_event
            WHEN NEW.role = 'confirming' AND (SELECT kind FROM evidence_record WHERE evidence_id = NEW.evidence_id) <> 'statement_row'
            BEGIN SELECT RAISE(ABORT, 'confirming links require statement evidence'); END;
            CREATE TRIGGER statement_scope_is_immutable_before_update BEFORE UPDATE ON statement_scope BEGIN SELECT RAISE(ABORT, 'statement scopes are immutable'); END;
            CREATE TRIGGER statement_scope_is_immutable_before_delete BEFORE DELETE ON statement_scope BEGIN SELECT RAISE(ABORT, 'statement scopes are immutable'); END;
            CREATE TRIGGER statement_scope_evidence_is_immutable_before_update BEFORE UPDATE ON statement_scope_evidence BEGIN SELECT RAISE(ABORT, 'statement scope evidence is immutable'); END;
            CREATE TRIGGER statement_scope_evidence_is_immutable_before_delete BEFORE DELETE ON statement_scope_evidence BEGIN SELECT RAISE(ABORT, 'statement scope evidence is immutable'); END;
            CREATE TRIGGER coverage_entry_is_immutable_before_update BEFORE UPDATE ON coverage_entry BEGIN SELECT RAISE(ABORT, 'coverage entries are immutable'); END;
            CREATE TRIGGER coverage_entry_is_immutable_before_delete BEFORE DELETE ON coverage_entry BEGIN SELECT RAISE(ABORT, 'coverage entries are immutable'); END;
            CREATE TRIGGER reconciliation_exception_is_immutable_before_update BEFORE UPDATE ON reconciliation_exception BEGIN SELECT RAISE(ABORT, 'reconciliation exceptions are immutable'); END;
            CREATE TRIGGER reconciliation_exception_is_immutable_before_delete BEFORE DELETE ON reconciliation_exception BEGIN SELECT RAISE(ABORT, 'reconciliation exceptions are immutable'); END;
            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (1, 'z_evidence_reconciliation', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
