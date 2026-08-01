using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Ordered PRAGMA user_version migrations for the CLASSIFY state store (DM-CLASSIFY-STATE-STORE + nine data models).
/// RESTRICT foreign keys; immutable tables reject UPDATE/DELETE; mutable run transitions are guarded.
/// </summary>
public static class ClassifySchema
{
    public const int CurrentVersion = 3;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        var userVersion = Convert.ToInt32(
            await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
            CultureInfo.InvariantCulture);

        if (userVersion > CurrentVersion)
        {
            throw new InvalidOperationException("The classify database schema version is newer than this runtime supports.");
        }

        while (userVersion < CurrentVersion)
        {
            var targetVersion = userVersion + 1;
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                switch (targetVersion)
                {
                    case 1:
                        await ApplyV001Async(connection, transaction, cancellationToken);
                        break;
                    case 2:
                        await ApplyV002Async(connection, transaction, cancellationToken);
                        break;
                    case 3:
                        await ApplyV003Async(connection, transaction, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("The classify database schema version is not supported by this runtime.");
                }

                await ExecuteAsync(connection, $"PRAGMA user_version = {targetVersion};", cancellationToken, transaction);
                await transaction.CommitAsync(cancellationToken);
                userVersion = targetVersion;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }

    private static async Task ApplyV001Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE classify_store_meta (
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                store_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(store_id)) > 0),
                created_at TEXT NOT NULL
            );

            CREATE TABLE operation_idempotency (
                idempotency_key TEXT PRIMARY KEY CHECK (length(trim(idempotency_key)) > 0),
                operation_id TEXT NOT NULL CHECK (length(trim(operation_id)) > 0),
                contract_version TEXT NOT NULL CHECK (length(trim(contract_version)) > 0),
                request_fingerprint TEXT NOT NULL CHECK (length(request_fingerprint) = 64),
                terminal_result TEXT NOT NULL CHECK (length(terminal_result) > 0),
                created_at TEXT NOT NULL
            );

            CREATE TABLE active_normalization (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                normalization_version TEXT NOT NULL CHECK (length(trim(normalization_version)) > 0),
                activation_epoch INTEGER NOT NULL CHECK (activation_epoch >= 0)
            );

            CREATE TABLE abandonment_tombstone (
                tombstone_id TEXT PRIMARY KEY CHECK (length(trim(tombstone_id)) > 0),
                subject_type TEXT NOT NULL CHECK (subject_type IN (
                    'rule', 'validation', 'evaluation', 'preview', 'apply', 'feedback', 'abandonment', 'cleanup')),
                subject_id TEXT NOT NULL CHECK (length(trim(subject_id)) > 0),
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                abandoned_at TEXT NOT NULL,
                removed_payload_count INTEGER NOT NULL CHECK (removed_payload_count >= 0),
                UNIQUE (subject_type, subject_id)
            );

            CREATE TABLE cleanup_event (
                cleanup_id TEXT PRIMARY KEY CHECK (length(trim(cleanup_id)) > 0),
                policy_version TEXT NOT NULL CHECK (length(trim(policy_version)) > 0),
                recognized_removed_count INTEGER NOT NULL CHECK (recognized_removed_count >= 0),
                expired_preview_count INTEGER NOT NULL CHECK (expired_preview_count >= 0),
                abandoned_payload_count INTEGER NOT NULL CHECK (abandoned_payload_count >= 0),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL
            );

            CREATE TABLE classification_rule (
                rule_id TEXT PRIMARY KEY CHECK (length(trim(rule_id)) > 0),
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by)) > 0)
            );

            CREATE TABLE rule_version (
                rule_version_id TEXT PRIMARY KEY CHECK (length(trim(rule_version_id)) > 0),
                rule_id TEXT NOT NULL REFERENCES classification_rule(rule_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                prior_version_id TEXT REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                normalization_version TEXT NOT NULL CHECK (length(trim(normalization_version)) > 0),
                category_id TEXT NOT NULL CHECK (length(trim(category_id)) > 0),
                scope_hash TEXT NOT NULL CHECK (length(scope_hash) = 64),
                rule_origin TEXT NOT NULL CHECK (rule_origin IN ('owner_authored', 'feedback_derived')),
                source_feedback_id TEXT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('draft', 'validated', 'active', 'retired')),
                broad_apply_allowed INTEGER NOT NULL CHECK (broad_apply_allowed IN (0, 1)),
                validation_run_id TEXT,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by)) > 0)
            );

            CREATE TABLE rule_condition (
                rule_version_id TEXT NOT NULL REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                field_key TEXT NOT NULL CHECK (field_key IN (
                    'description.normalized', 'account.id', 'amount.direction', 'amount.absolute_minor')),
                predicate_kind TEXT NOT NULL CHECK (predicate_kind IN (
                    'equals', 'starts_with', 'contains_token_sequence', 'between_inclusive')),
                value_text TEXT,
                value_minor_min INTEGER,
                value_minor_max INTEGER,
                enum_value TEXT CHECK (enum_value IS NULL OR enum_value IN ('inflow', 'outflow')),
                PRIMARY KEY (rule_version_id, ordinal)
            );

            CREATE TABLE rule_set_version (
                rule_set_version_id TEXT PRIMARY KEY CHECK (length(trim(rule_set_version_id)) > 0),
                prior_rule_set_version_id TEXT REFERENCES rule_set_version(rule_set_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                normalization_version TEXT NOT NULL CHECK (length(trim(normalization_version)) > 0),
                validation_run_id TEXT NOT NULL CHECK (length(trim(validation_run_id)) > 0),
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL CHECK (length(trim(created_by)) > 0)
            );

            CREATE TABLE rule_set_member (
                rule_set_version_id TEXT NOT NULL REFERENCES rule_set_version(rule_set_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                rule_version_id TEXT NOT NULL REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                PRIMARY KEY (rule_set_version_id, rule_version_id)
            );

            CREATE TABLE active_rule_set (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                rule_set_version_id TEXT NOT NULL REFERENCES rule_set_version(rule_set_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                activation_epoch INTEGER NOT NULL CHECK (activation_epoch >= 0)
            );

            CREATE TABLE rule_lifecycle_event (
                event_id TEXT PRIMARY KEY CHECK (length(trim(event_id)) > 0),
                subject_id TEXT NOT NULL CHECK (length(trim(subject_id)) > 0),
                prior_state TEXT,
                resulting_state TEXT NOT NULL,
                replacement_id TEXT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                occurred_at TEXT NOT NULL
            );

            CREATE TABLE validation_run (
                validation_run_id TEXT PRIMARY KEY CHECK (length(trim(validation_run_id)) > 0),
                candidate_fingerprint TEXT NOT NULL CHECK (length(candidate_fingerprint) = 64),
                rule_origin TEXT NOT NULL CHECK (rule_origin IN ('owner_authored', 'feedback_derived')),
                corpus_fingerprint TEXT NOT NULL CHECK (length(corpus_fingerprint) = 64),
                expected_outcome_fingerprint TEXT NOT NULL CHECK (length(expected_outcome_fingerprint) = 64),
                projection_contract_version TEXT NOT NULL,
                category_lifecycle_fingerprint TEXT NOT NULL CHECK (length(category_lifecycle_fingerprint) = 64),
                normalization_version TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('running', 'completed', 'failed', 'abandoned')),
                actor TEXT NOT NULL
            );

            CREATE TABLE validation_report (
                validation_run_id TEXT PRIMARY KEY REFERENCES validation_run(validation_run_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                total_rows INTEGER NOT NULL CHECK (total_rows >= 0),
                accounted_rows INTEGER NOT NULL CHECK (accounted_rows >= 0),
                suggestion_count INTEGER NOT NULL CHECK (suggestion_count >= 0),
                no_suggestion_count INTEGER NOT NULL CHECK (no_suggestion_count >= 0),
                conflict_count INTEGER NOT NULL CHECK (conflict_count >= 0),
                stale_count INTEGER NOT NULL CHECK (stale_count >= 0),
                coverage_basis_points INTEGER NOT NULL CHECK (coverage_basis_points >= 0 AND coverage_basis_points <= 10000),
                drift_canary_count INTEGER NOT NULL CHECK (drift_canary_count >= 0),
                incorrect_application_canary_count INTEGER NOT NULL CHECK (incorrect_application_canary_count >= 0),
                unexplained_conflict_count INTEGER NOT NULL CHECK (unexplained_conflict_count >= 0),
                owner_decision_count_before INTEGER NOT NULL CHECK (owner_decision_count_before >= 0),
                owner_decision_count_after INTEGER NOT NULL CHECK (owner_decision_count_after >= 0),
                owner_minutes_before REAL,
                owner_minutes_after REAL,
                report_fingerprint TEXT NOT NULL CHECK (length(report_fingerprint) = 64)
            );

            CREATE TABLE evaluation_run (
                evaluation_id TEXT PRIMARY KEY CHECK (length(trim(evaluation_id)) > 0),
                operation_idempotency_key TEXT UNIQUE REFERENCES operation_idempotency(idempotency_key) ON DELETE RESTRICT ON UPDATE RESTRICT,
                rule_set_version_id TEXT NOT NULL REFERENCES rule_set_version(rule_set_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                normalization_version TEXT NOT NULL,
                ledger_contract_version TEXT NOT NULL,
                projection_version TEXT NOT NULL,
                store_generation_fingerprint TEXT NOT NULL CHECK (length(store_generation_fingerprint) = 64),
                snapshot_id TEXT NOT NULL,
                snapshot_expires_at TEXT NOT NULL,
                category_lifecycle_fingerprint TEXT NOT NULL CHECK (length(category_lifecycle_fingerprint) = 64),
                ordered_items_fingerprint TEXT NOT NULL CHECK (length(ordered_items_fingerprint) = 64),
                input_count INTEGER NOT NULL CHECK (input_count >= 0),
                suggestion_count INTEGER NOT NULL CHECK (suggestion_count >= 0),
                no_suggestion_count INTEGER NOT NULL CHECK (no_suggestion_count >= 0),
                conflict_count INTEGER NOT NULL CHECK (conflict_count >= 0),
                stale_count INTEGER NOT NULL CHECK (stale_count >= 0),
                lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('running', 'completed', 'failed', 'abandoned')),
                actor TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE classification_outcome (
                outcome_id TEXT PRIMARY KEY CHECK (length(trim(outcome_id)) > 0),
                evaluation_id TEXT NOT NULL REFERENCES evaluation_run(evaluation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                transaction_id TEXT NOT NULL CHECK (length(trim(transaction_id)) > 0),
                outcome_type TEXT NOT NULL CHECK (outcome_type IN ('suggestion', 'no_suggestion', 'conflict', 'stale')),
                category_id TEXT,
                item_lifecycle_fingerprint TEXT NOT NULL CHECK (length(item_lifecycle_fingerprint) = 64),
                safe_reason TEXT NOT NULL,
                UNIQUE (evaluation_id, ordinal),
                UNIQUE (evaluation_id, transaction_id)
            );

            CREATE TABLE match_evidence (
                outcome_id TEXT NOT NULL REFERENCES classification_outcome(outcome_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                rule_version_id TEXT NOT NULL REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                condition_id TEXT NOT NULL CHECK (length(trim(condition_id)) > 0),
                field_key TEXT NOT NULL,
                predicate_kind TEXT NOT NULL,
                normalized_value_hash TEXT NOT NULL CHECK (length(normalized_value_hash) = 64),
                PRIMARY KEY (outcome_id, rule_version_id, condition_id)
            );

            CREATE TABLE apply_preview (
                preview_id TEXT PRIMARY KEY CHECK (length(trim(preview_id)) > 0),
                operation_idempotency_key TEXT UNIQUE REFERENCES operation_idempotency(idempotency_key) ON DELETE RESTRICT ON UPDATE RESTRICT,
                evaluation_id TEXT NOT NULL REFERENCES evaluation_run(evaluation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                evaluation_fingerprint TEXT NOT NULL CHECK (length(evaluation_fingerprint) = 64),
                selection_mode TEXT NOT NULL CHECK (selection_mode IN ('selected_outcomes', 'exact_rule', 'explicit_corrections')),
                selection_hash TEXT NOT NULL CHECK (length(selection_hash) = 64),
                ledger_contract_version TEXT NOT NULL,
                projection_version TEXT NOT NULL,
                store_generation_fingerprint TEXT NOT NULL CHECK (length(store_generation_fingerprint) = 64),
                preflight_snapshot_id TEXT NOT NULL,
                preflight_expires_at TEXT NOT NULL,
                category_lifecycle_fingerprint TEXT NOT NULL CHECK (length(category_lifecycle_fingerprint) = 64),
                target_category_fingerprint TEXT NOT NULL CHECK (length(target_category_fingerprint) = 64),
                rule_authority_fingerprint TEXT NOT NULL CHECK (length(rule_authority_fingerprint) = 64),
                expires_at TEXT NOT NULL,
                selected_count INTEGER NOT NULL CHECK (selected_count >= 0),
                exclusion_count INTEGER NOT NULL CHECK (exclusion_count >= 0),
                no_suggestion_count INTEGER NOT NULL CHECK (no_suggestion_count >= 0),
                conflict_count INTEGER NOT NULL CHECK (conflict_count >= 0),
                actor TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE apply_preview_item (
                preview_id TEXT NOT NULL REFERENCES apply_preview(preview_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                outcome_id TEXT NOT NULL REFERENCES classification_outcome(outcome_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                transaction_id TEXT NOT NULL,
                mode TEXT NOT NULL CHECK (mode IN ('assign', 'correct')),
                category_id TEXT NOT NULL,
                rule_version_id TEXT REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                expected_current_category_id TEXT,
                expected_active_allocation_id TEXT,
                expected_transaction_revision TEXT NOT NULL,
                expected_relationship_revision TEXT NOT NULL,
                expected_allocation_revision TEXT NOT NULL,
                correction_reason TEXT,
                PRIMARY KEY (preview_id, ordinal)
            );

            CREATE TABLE apply_run (
                apply_id TEXT PRIMARY KEY CHECK (length(trim(apply_id)) > 0),
                preview_id TEXT NOT NULL REFERENCES apply_preview(preview_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                request_fingerprint TEXT NOT NULL CHECK (length(request_fingerprint) = 64),
                lifecycle_state TEXT NOT NULL CHECK (lifecycle_state IN ('running', 'completed', 'failed', 'abandoned')),
                unresolved_frontier INTEGER NOT NULL CHECK (unresolved_frontier >= 0),
                actor TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE TABLE apply_item (
                apply_id TEXT NOT NULL REFERENCES apply_run(apply_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                transaction_id TEXT NOT NULL,
                ledger_operation_id TEXT NOT NULL,
                category_id TEXT NOT NULL,
                expected_active_allocation_id TEXT,
                expected_transaction_revision TEXT NOT NULL,
                expected_relationship_revision TEXT NOT NULL,
                expected_allocation_revision TEXT NOT NULL,
                correction_reason TEXT,
                ledger_request_fingerprint TEXT NOT NULL CHECK (length(ledger_request_fingerprint) = 64),
                ledger_idempotency_key TEXT NOT NULL,
                item_state TEXT NOT NULL CHECK (item_state IN (
                    'planned', 'applied', 'already_applied', 'rejected', 'failed', 'unresolved')),
                ledger_result_fingerprint TEXT,
                ledger_allocation_id TEXT,
                prior_ledger_allocation_id TEXT,
                safe_error_code TEXT,
                PRIMARY KEY (apply_id, ordinal)
            );

            CREATE TABLE classification_feedback (
                feedback_id TEXT PRIMARY KEY CHECK (length(trim(feedback_id)) > 0),
                outcome_id TEXT NOT NULL REFERENCES classification_outcome(outcome_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                transaction_id TEXT NOT NULL,
                evaluation_id TEXT NOT NULL REFERENCES evaluation_run(evaluation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                normalization_version TEXT NOT NULL,
                rule_set_version_id TEXT NOT NULL REFERENCES rule_set_version(rule_set_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                decision_type TEXT NOT NULL CHECK (decision_type IN ('accept', 'reject', 'correct')),
                prior_ledger_allocation_id TEXT,
                resulting_ledger_allocation_id TEXT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                actor TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );

            CREATE TABLE rule_proposal (
                proposal_id TEXT PRIMARY KEY CHECK (length(trim(proposal_id)) > 0),
                feedback_id TEXT NOT NULL UNIQUE REFERENCES classification_feedback(feedback_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                rule_origin TEXT NOT NULL CHECK (rule_origin = 'feedback_derived'),
                proposal_type TEXT NOT NULL CHECK (proposal_type IN ('none', 'retire', 'narrow', 'replace')),
                source_rule_version_id TEXT REFERENCES rule_version(rule_version_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                proposed_scope_fingerprint TEXT NOT NULL CHECK (length(proposed_scope_fingerprint) = 64),
                proposed_category_id TEXT,
                lifecycle_state TEXT NOT NULL CHECK (lifecycle_state = 'draft'),
                created_at TEXT NOT NULL
            );

            -- Immutable tables: reject UPDATE and DELETE (ADR-CORE-0020 / NFR-CLASSIFY-ATTRIBUTABLE-HISTORY)
            CREATE TRIGGER operation_idempotency_no_update BEFORE UPDATE ON operation_idempotency
            BEGIN SELECT RAISE(ABORT, 'operation_idempotency rows are immutable'); END;
            CREATE TRIGGER operation_idempotency_no_delete BEFORE DELETE ON operation_idempotency
            BEGIN SELECT RAISE(ABORT, 'operation_idempotency rows are immutable'); END;

            CREATE TRIGGER abandonment_tombstone_no_update BEFORE UPDATE ON abandonment_tombstone
            BEGIN SELECT RAISE(ABORT, 'abandonment_tombstone rows are immutable'); END;
            CREATE TRIGGER abandonment_tombstone_no_delete BEFORE DELETE ON abandonment_tombstone
            BEGIN SELECT RAISE(ABORT, 'abandonment_tombstone rows are immutable'); END;

            CREATE TRIGGER cleanup_event_no_update BEFORE UPDATE ON cleanup_event
            BEGIN SELECT RAISE(ABORT, 'cleanup_event rows are immutable'); END;
            CREATE TRIGGER cleanup_event_no_delete BEFORE DELETE ON cleanup_event
            BEGIN SELECT RAISE(ABORT, 'cleanup_event rows are immutable'); END;

            CREATE TRIGGER classification_rule_no_update BEFORE UPDATE ON classification_rule
            BEGIN SELECT RAISE(ABORT, 'classification_rule rows are immutable'); END;
            CREATE TRIGGER classification_rule_no_delete BEFORE DELETE ON classification_rule
            BEGIN SELECT RAISE(ABORT, 'classification_rule rows are immutable'); END;

            CREATE TRIGGER rule_version_no_update BEFORE UPDATE ON rule_version
            BEGIN SELECT RAISE(ABORT, 'rule_version rows are immutable'); END;
            CREATE TRIGGER rule_version_no_delete BEFORE DELETE ON rule_version
            BEGIN SELECT RAISE(ABORT, 'rule_version rows are immutable'); END;

            CREATE TRIGGER rule_condition_no_update BEFORE UPDATE ON rule_condition
            BEGIN SELECT RAISE(ABORT, 'rule_condition rows are immutable'); END;
            CREATE TRIGGER rule_condition_no_delete BEFORE DELETE ON rule_condition
            BEGIN SELECT RAISE(ABORT, 'rule_condition rows are immutable'); END;

            CREATE TRIGGER rule_set_version_no_update BEFORE UPDATE ON rule_set_version
            BEGIN SELECT RAISE(ABORT, 'rule_set_version rows are immutable'); END;
            CREATE TRIGGER rule_set_version_no_delete BEFORE DELETE ON rule_set_version
            BEGIN SELECT RAISE(ABORT, 'rule_set_version rows are immutable'); END;

            CREATE TRIGGER rule_set_member_no_update BEFORE UPDATE ON rule_set_member
            BEGIN SELECT RAISE(ABORT, 'rule_set_member rows are immutable'); END;
            CREATE TRIGGER rule_set_member_no_delete BEFORE DELETE ON rule_set_member
            BEGIN SELECT RAISE(ABORT, 'rule_set_member rows are immutable'); END;

            CREATE TRIGGER rule_lifecycle_event_no_update BEFORE UPDATE ON rule_lifecycle_event
            BEGIN SELECT RAISE(ABORT, 'rule_lifecycle_event rows are immutable'); END;
            CREATE TRIGGER rule_lifecycle_event_no_delete BEFORE DELETE ON rule_lifecycle_event
            BEGIN SELECT RAISE(ABORT, 'rule_lifecycle_event rows are immutable'); END;

            CREATE TRIGGER validation_report_no_update BEFORE UPDATE ON validation_report
            BEGIN SELECT RAISE(ABORT, 'validation_report rows are immutable'); END;
            CREATE TRIGGER validation_report_no_delete BEFORE DELETE ON validation_report
            BEGIN SELECT RAISE(ABORT, 'validation_report rows are immutable'); END;

            CREATE TRIGGER classification_outcome_no_update BEFORE UPDATE ON classification_outcome
            BEGIN SELECT RAISE(ABORT, 'classification_outcome rows are immutable'); END;
            CREATE TRIGGER classification_outcome_no_delete BEFORE DELETE ON classification_outcome
            BEGIN SELECT RAISE(ABORT, 'classification_outcome rows are immutable'); END;

            CREATE TRIGGER match_evidence_no_update BEFORE UPDATE ON match_evidence
            BEGIN SELECT RAISE(ABORT, 'match_evidence rows are immutable'); END;
            CREATE TRIGGER match_evidence_no_delete BEFORE DELETE ON match_evidence
            BEGIN SELECT RAISE(ABORT, 'match_evidence rows are immutable'); END;

            CREATE TRIGGER apply_preview_no_update BEFORE UPDATE ON apply_preview
            BEGIN SELECT RAISE(ABORT, 'apply_preview rows are immutable'); END;
            CREATE TRIGGER apply_preview_no_delete BEFORE DELETE ON apply_preview
            BEGIN SELECT RAISE(ABORT, 'apply_preview rows are immutable'); END;

            CREATE TRIGGER apply_preview_item_no_update BEFORE UPDATE ON apply_preview_item
            BEGIN SELECT RAISE(ABORT, 'apply_preview_item rows are immutable'); END;
            CREATE TRIGGER apply_preview_item_no_delete BEFORE DELETE ON apply_preview_item
            BEGIN SELECT RAISE(ABORT, 'apply_preview_item rows are immutable'); END;

            CREATE TRIGGER classification_feedback_no_update BEFORE UPDATE ON classification_feedback
            BEGIN SELECT RAISE(ABORT, 'classification_feedback rows are immutable'); END;
            CREATE TRIGGER classification_feedback_no_delete BEFORE DELETE ON classification_feedback
            BEGIN SELECT RAISE(ABORT, 'classification_feedback rows are immutable'); END;

            CREATE TRIGGER rule_proposal_no_update BEFORE UPDATE ON rule_proposal
            BEGIN SELECT RAISE(ABORT, 'rule_proposal rows are immutable'); END;
            CREATE TRIGGER rule_proposal_no_delete BEFORE DELETE ON rule_proposal
            BEGIN SELECT RAISE(ABORT, 'rule_proposal rows are immutable'); END;

            -- Mutable run tables: only lifecycle_state (and related completion fields) may change with expected prior state
            CREATE TRIGGER evaluation_run_content_immutable
            BEFORE UPDATE OF evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint, input_count,
                suggestion_count, no_suggestion_count, conflict_count, stale_count, actor, created_at
            ON evaluation_run
            BEGIN SELECT RAISE(ABORT, 'evaluation_run content is immutable'); END;

            CREATE TRIGGER evaluation_run_lifecycle_transition
            BEFORE UPDATE OF lifecycle_state ON evaluation_run
            BEGIN
                SELECT RAISE(ABORT, 'invalid evaluation_run lifecycle transition')
                WHERE NOT (
                    (OLD.lifecycle_state = 'running' AND NEW.lifecycle_state IN ('completed', 'failed', 'abandoned'))
                );
            END;

            CREATE TRIGGER evaluation_run_no_delete BEFORE DELETE ON evaluation_run
            BEGIN SELECT RAISE(ABORT, 'evaluation_run rows cannot be hard-deleted'); END;

            CREATE TRIGGER validation_run_content_immutable
            BEFORE UPDATE OF validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, actor
            ON validation_run
            BEGIN SELECT RAISE(ABORT, 'validation_run content is immutable'); END;

            CREATE TRIGGER validation_run_lifecycle_transition
            BEFORE UPDATE OF lifecycle_state ON validation_run
            BEGIN
                SELECT RAISE(ABORT, 'invalid validation_run lifecycle transition')
                WHERE NOT (
                    (OLD.lifecycle_state = 'running'
                        AND NEW.lifecycle_state IN ('completed', 'failed', 'abandoned')
                        AND NEW.completed_at IS NOT NULL)
                );
            END;

            CREATE TRIGGER validation_run_completion_transition_scoped
            BEFORE UPDATE OF completed_at ON validation_run
            BEGIN
                SELECT RAISE(ABORT, 'validation_run completion is transition-scoped')
                WHERE NOT (
                    OLD.lifecycle_state = 'running'
                    AND NEW.lifecycle_state IN ('completed', 'failed', 'abandoned')
                    AND OLD.completed_at IS NULL
                    AND NEW.completed_at IS NOT NULL
                );
            END;

            CREATE TRIGGER validation_run_no_delete BEFORE DELETE ON validation_run
            BEGIN SELECT RAISE(ABORT, 'validation_run rows cannot be hard-deleted'); END;

            CREATE TRIGGER apply_run_content_immutable
            BEFORE UPDATE OF apply_id, preview_id, request_fingerprint, actor, started_at ON apply_run
            BEGIN SELECT RAISE(ABORT, 'apply_run content is immutable'); END;

            CREATE TRIGGER apply_run_lifecycle_transition
            BEFORE UPDATE OF lifecycle_state ON apply_run
            BEGIN
                SELECT RAISE(ABORT, 'invalid apply_run lifecycle transition')
                WHERE NOT (
                    (OLD.lifecycle_state = 'running'
                        AND NEW.lifecycle_state IN ('completed', 'failed', 'abandoned')
                        AND NEW.completed_at IS NOT NULL)
                );
            END;

            CREATE TRIGGER apply_run_completion_transition_scoped
            BEFORE UPDATE OF completed_at ON apply_run
            BEGIN
                SELECT RAISE(ABORT, 'apply_run completion is transition-scoped')
                WHERE NOT (
                    OLD.lifecycle_state = 'running'
                    AND NEW.lifecycle_state IN ('completed', 'failed', 'abandoned')
                    AND OLD.completed_at IS NULL
                    AND NEW.completed_at IS NOT NULL
                );
            END;

            CREATE TRIGGER apply_run_no_delete BEFORE DELETE ON apply_run
            BEGIN SELECT RAISE(ABORT, 'apply_run rows cannot be hard-deleted'); END;

            -- Terminal apply_item states are immutable; planned/unresolved may advance
            CREATE TRIGGER apply_item_terminal_immutable
            BEFORE UPDATE ON apply_item
            WHEN OLD.item_state IN ('applied', 'already_applied', 'rejected', 'failed')
            BEGIN SELECT RAISE(ABORT, 'terminal apply_item rows are immutable'); END;

            CREATE TRIGGER apply_item_request_immutable
            BEFORE UPDATE OF apply_id, ordinal, transaction_id, ledger_operation_id, category_id,
                expected_active_allocation_id, expected_transaction_revision, expected_relationship_revision,
                expected_allocation_revision, correction_reason, ledger_request_fingerprint, ledger_idempotency_key
            ON apply_item
            BEGIN SELECT RAISE(ABORT, 'apply_item replay request is immutable'); END;

            CREATE TRIGGER apply_item_transition
            BEFORE UPDATE OF item_state ON apply_item
            WHEN OLD.item_state IN ('planned', 'unresolved')
            BEGIN
                SELECT RAISE(ABORT, 'invalid apply_item state transition')
                WHERE NOT (
                    (OLD.item_state = 'planned'
                        AND NEW.item_state IN ('applied', 'already_applied', 'rejected', 'failed', 'unresolved'))
                    OR (OLD.item_state = 'unresolved'
                        AND NEW.item_state IN ('applied', 'already_applied', 'rejected', 'failed'))
                );
            END;

            CREATE TRIGGER apply_item_no_delete BEFORE DELETE ON apply_item
            BEGIN SELECT RAISE(ABORT, 'apply_item rows cannot be hard-deleted'); END;

            CREATE TRIGGER classify_store_meta_no_delete BEFORE DELETE ON classify_store_meta
            BEGIN SELECT RAISE(ABORT, 'classify_store_meta rows cannot be deleted'); END;
            """;

        await ExecuteAsync(connection, sql, cancellationToken, transaction);
    }

    /// <summary>
    /// Additive migration: durable aggregate validation evidence for receipt reconstruction,
    /// immutable owner-rulebook gate receipt table, and rule_set_version receipt provenance.
    /// Historical rows keep NULL receipt/evidence columns — never backfill or infer authority.
    /// Never stores private corpus path, candidate IDs, description, amount, expected outcome, or token.
    /// </summary>
    private static async Task ApplyV002Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE validation_run ADD COLUMN snapshot_id TEXT;
            ALTER TABLE validation_run ADD COLUMN snapshot_expires_at TEXT;
            ALTER TABLE validation_run ADD COLUMN store_generation_fingerprint TEXT;

            ALTER TABLE validation_report ADD COLUMN outcomes_canonical_hash TEXT;
            ALTER TABLE validation_report ADD COLUMN activation_eligible INTEGER;

            ALTER TABLE rule_set_version ADD COLUMN owner_rulebook_gate_receipt_id TEXT;
            ALTER TABLE rule_set_version ADD COLUMN owner_rulebook_gate_receipt_fingerprint TEXT;

            CREATE TABLE owner_rulebook_gate_receipt (
                receipt_id TEXT PRIMARY KEY CHECK (length(trim(receipt_id)) > 0),
                receipt_fingerprint TEXT NOT NULL UNIQUE CHECK (length(receipt_fingerprint) = 64),
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                receipt_kind TEXT NOT NULL CHECK (receipt_kind = 'VerifiedOwnerRulebookGateReceipt'),
                authority_granted INTEGER NOT NULL CHECK (authority_granted IN (0, 1)),
                safety_passed INTEGER NOT NULL CHECK (safety_passed IN (0, 1)),
                benefit_sufficient INTEGER NOT NULL CHECK (benefit_sufficient IN (0, 1)),
                requires_explicit_owner_benefit_decision INTEGER NOT NULL CHECK (requires_explicit_owner_benefit_decision IN (0, 1)),
                block_code TEXT,
                eligible_rows INTEGER NOT NULL CHECK (eligible_rows >= 0),
                suggested_rows INTEGER NOT NULL CHECK (suggested_rows >= 0),
                correction_rows INTEGER NOT NULL CHECK (correction_rows >= 0),
                no_suggestion_rows INTEGER NOT NULL CHECK (no_suggestion_rows >= 0),
                conflict_rows INTEGER NOT NULL CHECK (conflict_rows >= 0),
                excluded_rows INTEGER NOT NULL CHECK (excluded_rows >= 0),
                stale_rows INTEGER NOT NULL CHECK (stale_rows >= 0),
                incorrect_application_canaries INTEGER NOT NULL CHECK (incorrect_application_canaries >= 0),
                unexplained_conflict_count INTEGER NOT NULL CHECK (unexplained_conflict_count >= 0),
                drift_canary_count INTEGER NOT NULL CHECK (drift_canary_count >= 0),
                unauthorized_mutation_count INTEGER NOT NULL CHECK (unauthorized_mutation_count >= 0),
                description_inferred_relationship_count INTEGER NOT NULL CHECK (description_inferred_relationship_count >= 0),
                coverage_basis_points INTEGER NOT NULL CHECK (coverage_basis_points >= 0 AND coverage_basis_points <= 10000),
                owner_decision_count_before INTEGER NOT NULL CHECK (owner_decision_count_before >= 0),
                owner_decision_count_after INTEGER NOT NULL CHECK (owner_decision_count_after >= 0),
                elapsed_owner_minutes_before REAL,
                elapsed_owner_minutes_after REAL,
                candidate_fingerprint TEXT CHECK (candidate_fingerprint IS NULL OR length(candidate_fingerprint) = 64),
                corpus_fingerprint TEXT CHECK (corpus_fingerprint IS NULL OR length(corpus_fingerprint) = 64),
                hold_out_fingerprint TEXT CHECK (hold_out_fingerprint IS NULL OR length(hold_out_fingerprint) = 64),
                report_fingerprint TEXT CHECK (report_fingerprint IS NULL OR length(report_fingerprint) = 64),
                outcomes_canonical_hash TEXT CHECK (outcomes_canonical_hash IS NULL OR length(outcomes_canonical_hash) = 64),
                deterministic_replay_passed INTEGER NOT NULL CHECK (deterministic_replay_passed IN (0, 1)),
                disclosure_passed INTEGER NOT NULL CHECK (disclosure_passed IN (0, 1)),
                locality_passed INTEGER NOT NULL CHECK (locality_passed IN (0, 1)),
                projection_version TEXT NOT NULL,
                snapshot_id TEXT,
                store_generation_fingerprint TEXT CHECK (store_generation_fingerprint IS NULL OR length(store_generation_fingerprint) = 64),
                category_lifecycle_fingerprint TEXT CHECK (category_lifecycle_fingerprint IS NULL OR length(category_lifecycle_fingerprint) = 64),
                normalization_version TEXT,
                representative_validation_run_id TEXT NOT NULL
                    REFERENCES validation_run(validation_run_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                independent_replay_validation_run_id TEXT NOT NULL
                    REFERENCES validation_run(validation_run_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                hold_out_validation_run_id TEXT NOT NULL
                    REFERENCES validation_run(validation_run_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                explicit_benefit_decision TEXT,
                actor TEXT NOT NULL CHECK (length(trim(actor)) > 0),
                created_at TEXT NOT NULL
            );

            CREATE TRIGGER owner_rulebook_gate_receipt_no_update BEFORE UPDATE ON owner_rulebook_gate_receipt
            BEGIN SELECT RAISE(ABORT, 'owner_rulebook_gate_receipt rows are immutable'); END;
            CREATE TRIGGER owner_rulebook_gate_receipt_no_delete BEFORE DELETE ON owner_rulebook_gate_receipt
            BEGIN SELECT RAISE(ABORT, 'owner_rulebook_gate_receipt rows are immutable'); END;
            """;

        await ExecuteAsync(connection, sql, cancellationToken, transaction);
    }

    /// <summary>
    /// Additive migration: complete cleanup receipt columns for aggregate removed/retained counts.
    /// Historical cleanup_event rows keep DEFAULT 0 — never invent path or payload metadata.
    /// </summary>
    private static async Task ApplyV003Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE cleanup_event ADD COLUMN removed_artifact_count INTEGER NOT NULL DEFAULT 0
                CHECK (removed_artifact_count >= 0);
            ALTER TABLE cleanup_event ADD COLUMN retained_artifact_count INTEGER NOT NULL DEFAULT 0
                CHECK (retained_artifact_count >= 0);
            """;
        await ExecuteAsync(connection, sql, cancellationToken, transaction);
    }

    internal static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
