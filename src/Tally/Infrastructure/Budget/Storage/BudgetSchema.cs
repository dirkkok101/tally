using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Budget.Storage;

/// <summary>
/// Ordered PRAGMA user_version migrations for the five-table BUDGET state store (DM-BUDGET-STATE-STORE).
/// </summary>
public static class BudgetSchema
{
    public const int CurrentVersion = 2;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        var userVersion = Convert.ToInt32(
            await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
            CultureInfo.InvariantCulture);

        if (userVersion > CurrentVersion)
        {
            throw new InvalidOperationException("The budget database schema version is newer than this runtime supports.");
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
                        await ApplyV002LifecycleColumnGuardsAsync(connection, transaction, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("The budget database schema version is not supported by this runtime.");
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
            CREATE TABLE budget_plan (
                plan_id TEXT PRIMARY KEY,
                period_start TEXT NOT NULL,
                period_end_exclusive TEXT NOT NULL,
                currency_code TEXT NOT NULL CHECK (currency_code = 'ZAR'),
                active_revision_id TEXT,
                created_at_utc TEXT NOT NULL,
                UNIQUE (currency_code, period_start),
                CHECK (period_start < period_end_exclusive)
            );

            CREATE TABLE budget_plan_revision (
                revision_id TEXT PRIMARY KEY,
                plan_id TEXT NOT NULL REFERENCES budget_plan(plan_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                revision_number INTEGER NOT NULL CHECK (revision_number > 0),
                status TEXT NOT NULL CHECK (status IN ('Draft', 'Active', 'Superseded')),
                actor_kind TEXT NOT NULL CHECK (length(trim(actor_kind)) > 0),
                actor_label TEXT NOT NULL CHECK (length(trim(actor_label)) > 0),
                actor_run_id TEXT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                created_at_utc TEXT NOT NULL,
                category_contract_version TEXT NOT NULL CHECK (length(trim(category_contract_version)) > 0),
                payload_hash TEXT NOT NULL CHECK (length(payload_hash) = 64),
                activated_at_utc TEXT,
                superseded_at_utc TEXT,
                superseded_by_revision_id TEXT REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                UNIQUE (plan_id, revision_number)
            );

            CREATE UNIQUE INDEX ux_budget_plan_revision_one_active
            ON budget_plan_revision(plan_id)
            WHERE status = 'Active';

            CREATE TABLE budget_plan_entry (
                revision_id TEXT NOT NULL REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                category_id TEXT NOT NULL,
                planned_minor_units INTEGER NOT NULL CHECK (planned_minor_units >= 0),
                PRIMARY KEY (revision_id, category_id)
            );

            CREATE TABLE budget_lifecycle_event (
                event_id TEXT PRIMARY KEY,
                plan_id TEXT NOT NULL REFERENCES budget_plan(plan_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                revision_id TEXT NOT NULL REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                event_type TEXT NOT NULL CHECK (event_type IN ('DraftCreated', 'RevisionActivated', 'RevisionSuperseded')),
                actor_kind TEXT NOT NULL CHECK (length(trim(actor_kind)) > 0),
                actor_label TEXT NOT NULL CHECK (length(trim(actor_label)) > 0),
                actor_run_id TEXT,
                reason TEXT NOT NULL CHECK (length(trim(reason)) > 0 AND length(reason) <= 1024),
                occurred_at_utc TEXT NOT NULL,
                prior_status TEXT CHECK (prior_status IS NULL OR prior_status IN ('Draft', 'Active', 'Superseded')),
                resulting_status TEXT CHECK (resulting_status IS NULL OR resulting_status IN ('Draft', 'Active', 'Superseded')),
                replacement_revision_id TEXT REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                event_sequence INTEGER NOT NULL CHECK (event_sequence > 0),
                UNIQUE (plan_id, event_sequence)
            );

            CREATE TABLE budget_idempotency_record (
                key_digest TEXT PRIMARY KEY CHECK (length(key_digest) = 64),
                contract_version TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                state TEXT NOT NULL CHECK (state = 'Completed'),
                plan_id TEXT REFERENCES budget_plan(plan_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                result_revision_id TEXT REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                prior_active_revision_id TEXT REFERENCES budget_plan_revision(revision_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
                lifecycle_event_ids TEXT NOT NULL,
                result_hash TEXT NOT NULL CHECK (length(result_hash) = 64),
                created_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NOT NULL
            );

            CREATE TRIGGER budget_plan_no_delete
            BEFORE DELETE ON budget_plan
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan rows are immutable');
            END;

            CREATE TRIGGER budget_plan_identity_immutable
            BEFORE UPDATE OF plan_id, period_start, period_end_exclusive, currency_code, created_at_utc ON budget_plan
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan identity columns are immutable');
            END;

            CREATE TRIGGER budget_plan_active_revision_same_plan
            BEFORE UPDATE OF active_revision_id ON budget_plan
            WHEN NEW.active_revision_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'active_revision_id must reference a revision of the same plan')
                WHERE NOT EXISTS (
                    SELECT 1 FROM budget_plan_revision
                    WHERE revision_id = NEW.active_revision_id AND plan_id = NEW.plan_id
                );
            END;

            CREATE TRIGGER budget_plan_revision_no_delete
            BEFORE DELETE ON budget_plan_revision
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan_revision rows are immutable');
            END;

            CREATE TRIGGER budget_plan_revision_content_immutable
            BEFORE UPDATE OF revision_id, plan_id, revision_number, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash ON budget_plan_revision
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan_revision content is immutable');
            END;

            CREATE TRIGGER budget_plan_revision_status_transition
            BEFORE UPDATE OF status ON budget_plan_revision
            BEGIN
                SELECT RAISE(ABORT, 'invalid budget revision status transition')
                WHERE NOT (
                    (OLD.status = 'Draft' AND NEW.status = 'Active')
                    OR (OLD.status = 'Active' AND NEW.status = 'Superseded')
                );
            END;

            CREATE TRIGGER budget_plan_entry_no_update
            BEFORE UPDATE ON budget_plan_entry
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan_entry rows are immutable');
            END;

            CREATE TRIGGER budget_plan_entry_no_delete
            BEFORE DELETE ON budget_plan_entry
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan_entry rows are immutable');
            END;

            CREATE TRIGGER budget_lifecycle_event_no_update
            BEFORE UPDATE ON budget_lifecycle_event
            BEGIN
                SELECT RAISE(ABORT, 'budget_lifecycle_event rows are immutable');
            END;

            CREATE TRIGGER budget_lifecycle_event_no_delete
            BEFORE DELETE ON budget_lifecycle_event
            BEGIN
                SELECT RAISE(ABORT, 'budget_lifecycle_event rows are immutable');
            END;

            CREATE TRIGGER budget_idempotency_record_no_update
            BEFORE UPDATE ON budget_idempotency_record
            BEGIN
                SELECT RAISE(ABORT, 'budget_idempotency_record rows are immutable');
            END;

            CREATE TRIGGER budget_idempotency_record_no_delete
            BEFORE DELETE ON budget_idempotency_record
            BEGIN
                SELECT RAISE(ABORT, 'budget_idempotency_record rows are immutable');
            END;
            """;

        await ExecuteAsync(connection, sql, cancellationToken, transaction);
    }

    /// <summary>
    /// Lifecycle timestamps and replacement references are only writable during the legal
    /// status transition that introduces them (bd-27ye).
    /// </summary>
    private static async Task ApplyV002LifecycleColumnGuardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TRIGGER budget_plan_revision_lifecycle_columns_guard
            BEFORE UPDATE OF activated_at_utc, superseded_at_utc, superseded_by_revision_id
            ON budget_plan_revision
            BEGIN
                SELECT RAISE(ABORT, 'budget_plan_revision lifecycle columns are transition-scoped')
                WHERE NOT (
                    (OLD.status = 'Draft' AND NEW.status = 'Active'
                        AND NEW.activated_at_utc IS NOT NULL
                        AND NEW.superseded_at_utc IS NULL
                        AND NEW.superseded_by_revision_id IS NULL)
                    OR (OLD.status = 'Active' AND NEW.status = 'Superseded'
                        AND NEW.superseded_at_utc IS NOT NULL
                        AND NEW.superseded_by_revision_id IS NOT NULL
                        AND OLD.activated_at_utc IS NEW.activated_at_utc)
                );
            END;
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
