using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget.Plans;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Budget.Storage;

/// <summary>
/// BUDGET-owned raw-SQLite durability boundary: connections, migrations, BEGIN IMMEDIATE writers,
/// and guarded plan/revision/entry/lifecycle persistence (DD-BUDGET-STATE-STORE).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetStateStore
{
    public const string CurrencyZar = "ZAR";

    private readonly HostArtifactProtection artifactProtection;

    public BudgetStateStore(string dataRoot, HostArtifactProtection? artifactProtection = null)
    {
        Paths = new BudgetStorePaths(dataRoot);
        this.artifactProtection = artifactProtection ?? new HostArtifactProtection();
    }

    public BudgetStateStore(BudgetStorePaths paths, HostArtifactProtection? artifactProtection = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
        this.artifactProtection = artifactProtection ?? new HostArtifactProtection();
    }

    public BudgetStorePaths Paths { get; }

    public HostArtifactProtection ArtifactProtection => artifactProtection;

    [SupportedOSPlatform("linux")]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await BudgetSchema.ApplyAsync(connection, cancellationToken);
        ProtectPersistedArtifacts();
    }

    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Path safety BEFORE open/create (bd-27ye): owner-only directories, no symlink DB path,
        // and existing files must already be 0600 — never touch an unsafe target first.
        artifactProtection.EnsureDataRoot(Paths.DataRoot);
        artifactProtection.EnsureDataRoot(Paths.BudgetDirectory);
        RejectUnsafeDatabasePath(Paths.DatabasePath);
        if (File.Exists(Paths.DatabasePath))
        {
            artifactProtection.RequireOwnerOnlyArtifact(Paths.DatabasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        try
        {
            await BudgetSchema.ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await BudgetSchema.ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
            await BudgetSchema.ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await BudgetSchema.ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
            ProtectPersistedArtifacts();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Fail closed if the database path is a symbolic link or otherwise not a regular owner file candidate.
    /// </summary>
    private static void RejectUnsafeDatabasePath(string databasePath)
    {
        // ResolveLinkTarget returns non-null when path is a symlink (even if the target is missing).
        if (File.Exists(databasePath) || Directory.Exists(databasePath))
        {
            try
            {
                if (File.ResolveLinkTarget(databasePath, returnFinalTarget: false) is not null)
                {
                    throw new InvalidOperationException(
                        "The budget database path must not be a symbolic link.");
                }
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "The budget database path is not a safe regular file.", ex);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            await BudgetSchema.ApplyAsync(connection, cancellationToken);
            ProtectPersistedArtifacts();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Begins an exclusive writer transaction (BEGIN IMMEDIATE).
    /// </summary>
    public SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction(deferred: false);
    }

    /// <summary>
    /// One BEGIN IMMEDIATE spans replay lookup, mutation, active pointer, supersession, and outcome refs.
    /// </summary>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = BeginImmediate(connection);
        try
        {
            var result = await work(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ProtectPersistedArtifacts();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task InsertPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BudgetPlanRow plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.CurrencyCode, CurrencyZar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BUDGET plans require currency_code ZAR.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO budget_plan (
                plan_id, period_start, period_end_exclusive, currency_code, active_revision_id, created_at_utc
            ) VALUES (
                $plan_id, $period_start, $period_end_exclusive, $currency_code, $active_revision_id, $created_at_utc
            );
            """;
        command.Parameters.AddWithValue("$plan_id", plan.PlanId);
        command.Parameters.AddWithValue("$period_start", plan.PeriodStart);
        command.Parameters.AddWithValue("$period_end_exclusive", plan.PeriodEndExclusive);
        command.Parameters.AddWithValue("$currency_code", plan.CurrencyCode);
        command.Parameters.AddWithValue("$active_revision_id", (object?)plan.ActiveRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at_utc", plan.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertDraftRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BudgetPlanRevisionRow revision,
        IReadOnlyList<BudgetPlanEntryRow> entries,
        BudgetLifecycleEventRow draftCreatedEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(draftCreatedEvent);

        if (revision.Status != BudgetRevisionStatus.Draft)
        {
            throw new InvalidOperationException("New revisions must be inserted as Draft.");
        }

        if (!string.Equals(draftCreatedEvent.EventType, "DraftCreated", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Draft creation requires a DraftCreated lifecycle event.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO budget_plan_revision (
                    revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                    reason, created_at_utc, category_contract_version, payload_hash,
                    activated_at_utc, superseded_at_utc, superseded_by_revision_id
                ) VALUES (
                    $revision_id, $plan_id, $revision_number, $status, $actor_kind, $actor_label, $actor_run_id,
                    $reason, $created_at_utc, $category_contract_version, $payload_hash,
                    $activated_at_utc, $superseded_at_utc, $superseded_by_revision_id
                );
                """;
            BindRevision(command, revision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.RevisionId, revision.RevisionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Entry revision_id must match the draft revision.");
            }

            if (entry.PlannedMinorUnits < 0)
            {
                throw new InvalidOperationException("planned_minor_units must be >= 0.");
            }

            await using var entryCommand = connection.CreateCommand();
            entryCommand.Transaction = transaction;
            entryCommand.CommandText = """
                INSERT INTO budget_plan_entry (revision_id, category_id, planned_minor_units)
                VALUES ($revision_id, $category_id, $planned_minor_units);
                """;
            entryCommand.Parameters.AddWithValue("$revision_id", entry.RevisionId);
            entryCommand.Parameters.AddWithValue("$category_id", entry.CategoryId);
            entryCommand.Parameters.AddWithValue("$planned_minor_units", entry.PlannedMinorUnits);
            await entryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertLifecycleEventAsync(connection, transaction, draftCreatedEvent, cancellationToken);
    }

    public async Task ActivateRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string revisionId,
        string activatedAtUtc,
        string reason,
        string actorKind,
        string actorLabel,
        string? actorRunId,
        string activateEventId,
        string? supersedeEventId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activatedAtUtc);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(activateEventId);

        var revision = await GetRevisionAsync(connection, transaction, revisionId, cancellationToken)
            ?? throw new InvalidOperationException("Revision was not found.");
        if (!string.Equals(revision.PlanId, planId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Revision does not belong to the plan.");
        }

        if (revision.Status != BudgetRevisionStatus.Draft)
        {
            throw new InvalidOperationException("Only Draft revisions can be activated.");
        }

        var plan = await GetPlanAsync(connection, transaction, planId, cancellationToken)
            ?? throw new InvalidOperationException("Plan was not found.");

        string? priorActiveRevisionId = plan.ActiveRevisionId;
        if (priorActiveRevisionId is not null)
        {
            if (supersedeEventId is null)
            {
                throw new InvalidOperationException("Superseding an Active revision requires a supersession event id.");
            }

            await using (var supersede = connection.CreateCommand())
            {
                supersede.Transaction = transaction;
                supersede.CommandText = """
                    UPDATE budget_plan_revision
                    SET status = 'Superseded',
                        superseded_at_utc = $superseded_at_utc,
                        superseded_by_revision_id = $superseded_by_revision_id
                    WHERE revision_id = $revision_id AND status = 'Active';
                    """;
                supersede.Parameters.AddWithValue("$superseded_at_utc", activatedAtUtc);
                supersede.Parameters.AddWithValue("$superseded_by_revision_id", revisionId);
                supersede.Parameters.AddWithValue("$revision_id", priorActiveRevisionId);
                var superseded = await supersede.ExecuteNonQueryAsync(cancellationToken);
                if (superseded != 1)
                {
                    throw new InvalidOperationException("Prior Active revision could not be superseded.");
                }
            }

            var nextSequence = await NextEventSequenceAsync(connection, transaction, planId, cancellationToken);
            await InsertLifecycleEventAsync(
                connection,
                transaction,
                new BudgetLifecycleEventRow(
                    supersedeEventId,
                    planId,
                    priorActiveRevisionId,
                    "RevisionSuperseded",
                    actorKind,
                    actorLabel,
                    actorRunId,
                    reason,
                    activatedAtUtc,
                    BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Active),
                    BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Superseded),
                    revisionId,
                    nextSequence),
                cancellationToken);
        }

        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE budget_plan_revision
                SET status = 'Active',
                    activated_at_utc = $activated_at_utc
                WHERE revision_id = $revision_id AND status = 'Draft';
                """;
            activate.Parameters.AddWithValue("$activated_at_utc", activatedAtUtc);
            activate.Parameters.AddWithValue("$revision_id", revisionId);
            var activated = await activate.ExecuteNonQueryAsync(cancellationToken);
            if (activated != 1)
            {
                throw new InvalidOperationException("Draft revision could not be activated.");
            }
        }

        await using (var pointer = connection.CreateCommand())
        {
            pointer.Transaction = transaction;
            pointer.CommandText = """
                UPDATE budget_plan
                SET active_revision_id = $active_revision_id
                WHERE plan_id = $plan_id;
                """;
            pointer.Parameters.AddWithValue("$active_revision_id", revisionId);
            pointer.Parameters.AddWithValue("$plan_id", planId);
            await pointer.ExecuteNonQueryAsync(cancellationToken);
        }

        var activateSequence = await NextEventSequenceAsync(connection, transaction, planId, cancellationToken);
        await InsertLifecycleEventAsync(
            connection,
            transaction,
            new BudgetLifecycleEventRow(
                activateEventId,
                planId,
                revisionId,
                "RevisionActivated",
                actorKind,
                actorLabel,
                actorRunId,
                reason,
                activatedAtUtc,
                BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Draft),
                BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Active),
                null,
                activateSequence),
            cancellationToken);
    }

    public async Task InsertLifecycleEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BudgetLifecycleEventRow lifecycleEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO budget_lifecycle_event (
                event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence
            ) VALUES (
                $event_id, $plan_id, $revision_id, $event_type, $actor_kind, $actor_label, $actor_run_id,
                $reason, $occurred_at_utc, $prior_status, $resulting_status, $replacement_revision_id, $event_sequence
            );
            """;
        command.Parameters.AddWithValue("$event_id", lifecycleEvent.EventId);
        command.Parameters.AddWithValue("$plan_id", lifecycleEvent.PlanId);
        command.Parameters.AddWithValue("$revision_id", lifecycleEvent.RevisionId);
        command.Parameters.AddWithValue("$event_type", lifecycleEvent.EventType);
        command.Parameters.AddWithValue("$actor_kind", lifecycleEvent.ActorKind);
        command.Parameters.AddWithValue("$actor_label", lifecycleEvent.ActorLabel);
        command.Parameters.AddWithValue("$actor_run_id", (object?)lifecycleEvent.ActorRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", lifecycleEvent.Reason);
        command.Parameters.AddWithValue("$occurred_at_utc", lifecycleEvent.OccurredAtUtc);
        command.Parameters.AddWithValue("$prior_status", (object?)lifecycleEvent.PriorStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$resulting_status", (object?)lifecycleEvent.ResultingStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$replacement_revision_id", (object?)lifecycleEvent.ReplacementRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$event_sequence", lifecycleEvent.EventSequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BudgetPlanRow?> GetPlanAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id, period_start, period_end_exclusive, currency_code, active_revision_id, created_at_utc
            FROM budget_plan
            WHERE plan_id = $plan_id;
            """;
        command.Parameters.AddWithValue("$plan_id", planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? BudgetRowMapper.MapPlan(reader) : null;
    }

    public async Task<BudgetPlanRow?> GetPlanByPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string currencyCode,
        string periodStart,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id, period_start, period_end_exclusive, currency_code, active_revision_id, created_at_utc
            FROM budget_plan
            WHERE currency_code = $currency_code AND period_start = $period_start;
            """;
        command.Parameters.AddWithValue("$currency_code", currencyCode);
        command.Parameters.AddWithValue("$period_start", periodStart);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? BudgetRowMapper.MapPlan(reader) : null;
    }

    public async Task<BudgetPlanRevisionRow?> GetRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                   reason, created_at_utc, category_contract_version, payload_hash,
                   activated_at_utc, superseded_at_utc, superseded_by_revision_id
            FROM budget_plan_revision
            WHERE revision_id = $revision_id;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? BudgetRowMapper.MapRevision(reader) : null;
    }

    public async Task<IReadOnlyList<BudgetPlanEntryRow>> GetEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id, category_id, planned_minor_units
            FROM budget_plan_entry
            WHERE revision_id = $revision_id
            ORDER BY category_id;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BudgetPlanEntryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(BudgetRowMapper.MapEntry(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BudgetLifecycleEventRow>> GetLifecycleEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                   reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence
            FROM budget_lifecycle_event
            WHERE plan_id = $plan_id
            ORDER BY event_sequence;
            """;
        command.Parameters.AddWithValue("$plan_id", planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BudgetLifecycleEventRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(BudgetRowMapper.MapLifecycleEvent(reader));
        }

        return rows;
    }

    public async Task<int> NextEventSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(event_sequence), 0) + 1 FROM budget_lifecycle_event WHERE plan_id = $plan_id;";
        command.Parameters.AddWithValue("$plan_id", planId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<int> NextRevisionNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM budget_plan_revision WHERE plan_id = $plan_id;";
        command.Parameters.AddWithValue("$plan_id", planId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }


    public async Task<IReadOnlyList<BudgetPlanRevisionSummaryRow>> ListRevisionSummariesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string planId,
        string? statusFilter,
        int fetchLimit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                r.revision_id,
                r.revision_number,
                r.status,
                r.created_at_utc,
                COALESCE(SUM(e.planned_minor_units), 0) AS planned_total,
                COUNT(e.category_id) AS entry_count
            FROM budget_plan_revision r
            LEFT JOIN budget_plan_entry e ON e.revision_id = r.revision_id
            WHERE r.plan_id = $plan_id
              AND ($status IS NULL OR r.status = $status)
            GROUP BY
                r.revision_id,
                r.revision_number,
                r.status,
                r.created_at_utc
            ORDER BY r.created_at_utc ASC, r.revision_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$plan_id", planId);
        command.Parameters.AddWithValue("$status", (object?)statusFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", fetchLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BudgetPlanRevisionSummaryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BudgetPlanRevisionSummaryRow(
                reader.GetString(0),
                reader.GetInt32(1),
                BudgetRowMapper.ParseStatus(reader.GetString(2)),
                reader.GetString(3),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>
    /// Rejects unsafe modes on directories and recognized artifacts without repairing them.
    /// </summary>
    public void RequireOwnerOnlyArtifacts()
    {
        artifactProtection.RequireOwnerOnlyDirectory(Paths.DataRoot);
        artifactProtection.RequireOwnerOnlyDirectory(Paths.BudgetDirectory);
        foreach (var artifact in Paths.RecognizedArtifactPaths().Where(File.Exists))
        {
            artifactProtection.RequireOwnerOnlyArtifact(artifact);
        }
    }

    [SupportedOSPlatform("linux")]
    private void ProtectPersistedArtifacts()
    {
        if (File.Exists(Paths.DatabasePath))
        {
            artifactProtection.ProtectArtifact(Paths.DatabasePath);
        }

        foreach (var artifact in Paths.RecognizedArtifactPaths().Where(path => path != Paths.DatabasePath && File.Exists(path)))
        {
            artifactProtection.ProtectArtifact(artifact);
        }
    }

    private static void BindRevision(SqliteCommand command, BudgetPlanRevisionRow revision)
    {
        command.Parameters.AddWithValue("$revision_id", revision.RevisionId);
        command.Parameters.AddWithValue("$plan_id", revision.PlanId);
        command.Parameters.AddWithValue("$revision_number", revision.RevisionNumber);
        command.Parameters.AddWithValue("$status", BudgetRowMapper.FormatStatus(revision.Status));
        command.Parameters.AddWithValue("$actor_kind", revision.ActorKind);
        command.Parameters.AddWithValue("$actor_label", revision.ActorLabel);
        command.Parameters.AddWithValue("$actor_run_id", (object?)revision.ActorRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", revision.Reason);
        command.Parameters.AddWithValue("$created_at_utc", revision.CreatedAtUtc);
        command.Parameters.AddWithValue("$category_contract_version", revision.CategoryContractVersion);
        command.Parameters.AddWithValue("$payload_hash", revision.PayloadHash);
        command.Parameters.AddWithValue("$activated_at_utc", (object?)revision.ActivatedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$superseded_at_utc", (object?)revision.SupersededAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$superseded_by_revision_id", (object?)revision.SupersededByRevisionId ?? DBNull.Value);
    }
}
