using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Infrastructure.Classify.Storage.Rules;

/// <summary>
/// Atomic immutable rule-set version / membership / active-pointer / lifecycle-event persistence
/// (DM-CLASSIFY-RULE-LIFECYCLE / TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE).
/// Append-only history: rule_set_version and members never update; active_rule_set is the
/// sole guarded pointer; prior versions are retained.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RuleSetStore
{
    public async Task<IReadOnlyList<ClassifyRuleVersionRow>> ListAllRuleVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                   scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                   broad_apply_allowed, validation_run_id, created_at, created_by
            FROM rule_version
            ORDER BY rule_version_id ASC;
            """;
        var rows = new List<ClassifyRuleVersionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ClassifyRowMapper.MapRuleVersion(reader));
        }

        return rows;
    }

    public async Task<ClassifyRuleSetVersionRow?> GetRuleSetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleSetVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_set_version_id, prior_rule_set_version_id, normalization_version,
                   validation_run_id, reason, created_at, created_by
            FROM rule_set_version
            WHERE rule_set_version_id = $id;
            """;
        command.Parameters.AddWithValue("$id", ruleSetVersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRuleSetVersion(reader) : null;
    }

    public async Task<IReadOnlyList<string>> ListMemberRuleVersionIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleSetVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_version_id
            FROM rule_set_member
            WHERE rule_set_version_id = $id
            ORDER BY rule_version_id ASC;
            """;
        command.Parameters.AddWithValue("$id", ruleSetVersionId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public async Task<ClassifyActiveRuleSetPointer?> GetActiveRuleSetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT singleton_id, rule_set_version_id, activation_epoch
            FROM active_rule_set
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassifyActiveRuleSetPointer(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt64(2));
    }

    /// <summary>
    /// Atomically append a rule-set version, its members, lifecycle events, and the active pointer.
    /// Never mutates prior rule_set_version / member / rule_version rows.
    /// </summary>
    public async Task ActivateRuleSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleSetVersionRow version,
        IReadOnlyList<string> memberRuleVersionIds,
        IReadOnlyList<ClassifyRuleLifecycleEventRow> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(memberRuleVersionIds);
        ArgumentNullException.ThrowIfNull(events);
        if (memberRuleVersionIds.Count == 0)
        {
            throw new InvalidOperationException("An activated rule set must contain at least one member.");
        }

        await InsertRuleSetVersionAsync(connection, transaction, version, cancellationToken);
        foreach (var memberId in memberRuleVersionIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            await InsertMemberAsync(connection, transaction, version.RuleSetVersionId, memberId, cancellationToken);
        }

        foreach (var lifecycleEvent in events)
        {
            await InsertLifecycleEventAsync(connection, transaction, lifecycleEvent, cancellationToken);
        }

        await UpsertActivePointerAsync(
            connection,
            transaction,
            version.RuleSetVersionId,
            cancellationToken);
    }

    /// <summary>
    /// Atomic retirement successor: new rule-set version (possibly empty members), events, pointer.
    /// Empty membership is allowed for a fully retired catalogue (history retained).
    /// </summary>
    public async Task RetireIntoSuccessorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleSetVersionRow successor,
        IReadOnlyList<string> successorMemberIds,
        IReadOnlyList<ClassifyRuleLifecycleEventRow> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(successor);
        ArgumentNullException.ThrowIfNull(successorMemberIds);
        ArgumentNullException.ThrowIfNull(events);

        await InsertRuleSetVersionAsync(connection, transaction, successor, cancellationToken);
        foreach (var memberId in successorMemberIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            await InsertMemberAsync(connection, transaction, successor.RuleSetVersionId, memberId, cancellationToken);
        }

        foreach (var lifecycleEvent in events)
        {
            await InsertLifecycleEventAsync(connection, transaction, lifecycleEvent, cancellationToken);
        }

        await UpsertActivePointerAsync(
            connection,
            transaction,
            successor.RuleSetVersionId,
            cancellationToken);
    }

    public async Task InsertRuleSetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleSetVersionRow version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                $rule_set_version_id, $prior_rule_set_version_id, $normalization_version,
                $validation_run_id, $reason, $created_at, $created_by
            );
            """;
        command.Parameters.AddWithValue("$rule_set_version_id", version.RuleSetVersionId);
        command.Parameters.AddWithValue(
            "$prior_rule_set_version_id",
            (object?)version.PriorRuleSetVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalization_version", version.NormalizationVersion);
        command.Parameters.AddWithValue("$validation_run_id", version.ValidationRunId);
        command.Parameters.AddWithValue("$reason", version.Reason);
        command.Parameters.AddWithValue("$created_at", version.CreatedAt);
        command.Parameters.AddWithValue("$created_by", version.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertMemberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ruleSetVersionId,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rule_set_member (rule_set_version_id, rule_version_id)
            VALUES ($rule_set_version_id, $rule_version_id);
            """;
        command.Parameters.AddWithValue("$rule_set_version_id", ruleSetVersionId);
        command.Parameters.AddWithValue("$rule_version_id", ruleVersionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertLifecycleEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleLifecycleEventRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rule_lifecycle_event (
                event_id, subject_id, prior_state, resulting_state, replacement_id, reason, actor, occurred_at
            ) VALUES (
                $event_id, $subject_id, $prior_state, $resulting_state, $replacement_id, $reason, $actor, $occurred_at
            );
            """;
        command.Parameters.AddWithValue("$event_id", row.EventId);
        command.Parameters.AddWithValue("$subject_id", row.SubjectId);
        command.Parameters.AddWithValue("$prior_state", (object?)row.PriorState ?? DBNull.Value);
        command.Parameters.AddWithValue("$resulting_state", row.ResultingState);
        command.Parameters.AddWithValue("$replacement_id", (object?)row.ReplacementId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", row.Reason);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$occurred_at", row.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Guarded singleton pointer transition: insert when absent, otherwise update only when
    /// the expected prior rule_set_version_id still matches (optimistic concurrency).
    /// </summary>
    public async Task UpsertActivePointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ruleSetVersionId,
        CancellationToken cancellationToken,
        string? expectedPriorRuleSetVersionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        var current = await GetActiveRuleSetAsync(connection, transaction, cancellationToken);
        if (current is null)
        {
            if (expectedPriorRuleSetVersionId is not null)
            {
                throw new InvalidOperationException(
                    "active_rule_set expected a prior pointer but none exists.");
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO active_rule_set (singleton_id, rule_set_version_id, activation_epoch)
                VALUES (1, $rule_set_version_id, 0);
                """;
            insert.Parameters.AddWithValue("$rule_set_version_id", ruleSetVersionId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (expectedPriorRuleSetVersionId is not null
            && !string.Equals(current.RuleSetVersionId, expectedPriorRuleSetVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "active_rule_set pointer drifted before activation/retirement commit.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE active_rule_set
            SET rule_set_version_id = $next,
                activation_epoch = activation_epoch + 1
            WHERE singleton_id = 1
              AND rule_set_version_id = $expected;
            """;
        update.Parameters.AddWithValue("$next", ruleSetVersionId);
        update.Parameters.AddWithValue("$expected", current.RuleSetVersionId);
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "active_rule_set pointer update requires exactly one matching row.");
        }
    }

    public async Task<IReadOnlyList<ClassifyRuleLifecycleEventRow>> ListLifecycleEventsForSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string subjectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, subject_id, prior_state, resulting_state, replacement_id, reason, actor, occurred_at
            FROM rule_lifecycle_event
            WHERE subject_id = $subject_id
            ORDER BY occurred_at ASC, event_id ASC;
            """;
        command.Parameters.AddWithValue("$subject_id", subjectId);
        var rows = new List<ClassifyRuleLifecycleEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapLifecycleEvent(reader));
        }

        return rows;
    }

    public async Task<long> CountRuleSetVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_set_version;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountLifecycleEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_lifecycle_event;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static ClassifyRuleSetVersionRow MapRuleSetVersion(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6));

    private static ClassifyRuleLifecycleEventRow MapLifecycleEvent(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7));
}

public sealed record ClassifyRuleSetVersionRow(
    string RuleSetVersionId,
    string? PriorRuleSetVersionId,
    string NormalizationVersion,
    string ValidationRunId,
    string Reason,
    string CreatedAt,
    string CreatedBy);

public sealed record ClassifyRuleLifecycleEventRow(
    string EventId,
    string SubjectId,
    string? PriorState,
    string ResultingState,
    string? ReplacementId,
    string Reason,
    string Actor,
    string OccurredAt);
