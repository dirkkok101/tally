using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Infrastructure.Classify.Storage.Recovery;

/// <summary>
/// Extended cleanup_event row including aggregate removed/retained counts (schema v3).
/// Kept here so ClassifyRowMapper (unreserved) stays unchanged.
/// </summary>
public sealed record ClassifyCleanupEventReceiptRow(
    string CleanupId,
    string PolicyVersion,
    int RecognizedRemovedCount,
    int ExpiredPreviewCount,
    int AbandonedPayloadCount,
    string Actor,
    string OccurredAt,
    int RemovedArtifactCount,
    int RetainedArtifactCount);

/// <summary>
/// Abandonment tombstones and cleanup events plus RESTRICT reference probes
/// (DM-CLASSIFY-STATE-STORE / TASK-CLASSIFY-RULEBOOK-ABANDON-CLEANUP).
/// Never hard-deletes referenced rule/evaluation/apply/feedback history.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationRecoveryStore
{
    public async Task InsertTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyAbandonmentTombstoneRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO abandonment_tombstone (
                tombstone_id, subject_type, subject_id, reason, actor, abandoned_at, removed_payload_count
            ) VALUES (
                $tombstone_id, $subject_type, $subject_id, $reason, $actor, $abandoned_at, $removed_payload_count
            );
            """;
        command.Parameters.AddWithValue("$tombstone_id", row.TombstoneId);
        command.Parameters.AddWithValue("$subject_type", row.SubjectType);
        command.Parameters.AddWithValue("$subject_id", row.SubjectId);
        command.Parameters.AddWithValue("$reason", row.Reason);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$abandoned_at", row.AbandonedAt);
        command.Parameters.AddWithValue("$removed_payload_count", row.RemovedPayloadCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertCleanupEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyCleanupEventReceiptRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cleanup_event (
                cleanup_id, policy_version, recognized_removed_count, expired_preview_count,
                abandoned_payload_count, actor, occurred_at,
                removed_artifact_count, retained_artifact_count
            ) VALUES (
                $cleanup_id, $policy_version, $recognized_removed_count, $expired_preview_count,
                $abandoned_payload_count, $actor, $occurred_at,
                $removed_artifact_count, $retained_artifact_count
            );
            """;
        command.Parameters.AddWithValue("$cleanup_id", row.CleanupId);
        command.Parameters.AddWithValue("$policy_version", row.PolicyVersion);
        command.Parameters.AddWithValue("$recognized_removed_count", row.RecognizedRemovedCount);
        command.Parameters.AddWithValue("$expired_preview_count", row.ExpiredPreviewCount);
        command.Parameters.AddWithValue("$abandoned_payload_count", row.AbandonedPayloadCount);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$occurred_at", row.OccurredAt);
        command.Parameters.AddWithValue("$removed_artifact_count", row.RemovedArtifactCount);
        command.Parameters.AddWithValue("$retained_artifact_count", row.RetainedArtifactCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasCleanupEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string cleanupId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM cleanup_event WHERE cleanup_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", cleanupId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null and not DBNull;
    }

    public async Task<bool> HasTombstoneIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tombstoneId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tombstoneId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM abandonment_tombstone WHERE tombstone_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", tombstoneId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null and not DBNull;
    }

    public async Task<bool> HasRuleVersionTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        var row = await GetTombstoneAsync(
            connection, transaction, ClassifyRetentionPolicy.SubjectTypeRule, ruleVersionId, cancellationToken);
        return row is not null;
    }

    public async Task<ClassifyAbandonmentTombstoneRow?> GetTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tombstone_id, subject_type, subject_id, reason, actor, abandoned_at, removed_payload_count
            FROM abandonment_tombstone
            WHERE subject_type = $type AND subject_id = $id;
            """;
        command.Parameters.AddWithValue("$type", subjectType);
        command.Parameters.AddWithValue("$id", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ClassifyRowMapper.MapAbandonment(reader)
            : null;
    }

    public async Task<ClassifyRetentionPolicy.ReferenceFlags> ProbeRuleVersionReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        var flags = ClassifyRetentionPolicy.ReferenceFlags.None;

        if (!await ExistsAsync(connection, transaction,
                "SELECT 1 FROM rule_version WHERE rule_version_id = $id LIMIT 1;",
                ruleVersionId, cancellationToken))
        {
            return ClassifyRetentionPolicy.ReferenceFlags.NotFound;
        }

        var lifecycle = await ScalarStringAsync(connection, transaction,
            "SELECT lifecycle_state FROM rule_version WHERE rule_version_id = $id;",
            ruleVersionId, cancellationToken);
        if (!string.Equals(lifecycle, "draft", StringComparison.Ordinal))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.NotDraft;
        }

        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM rule_set_member WHERE rule_version_id = $id LIMIT 1;",
                ruleVersionId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.ActiveRuleSetMember;
        }

        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM match_evidence WHERE rule_version_id = $id LIMIT 1;",
                ruleVersionId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.MatchEvidence;
        }

        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM rule_proposal WHERE source_rule_version_id = $id LIMIT 1;",
                ruleVersionId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.RuleProposal;
        }

        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM apply_preview_item WHERE rule_version_id = $id LIMIT 1;",
                ruleVersionId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.ApplyPreviewItem;
        }

        return flags;
    }

    public async Task<ClassifyRetentionPolicy.ReferenceFlags> ProbeValidationReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string validationRunId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        if (!await ExistsAsync(connection, transaction,
                "SELECT 1 FROM validation_run WHERE validation_run_id = $id LIMIT 1;",
                validationRunId, cancellationToken))
        {
            return ClassifyRetentionPolicy.ReferenceFlags.NotFound;
        }

        var flags = ClassifyRetentionPolicy.ReferenceFlags.None;
        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM rule_set_version WHERE validation_run_id = $id LIMIT 1;",
                validationRunId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.RuleSetValidation;
        }

        return flags;
    }

    public async Task<ClassifyRetentionPolicy.ReferenceFlags> ProbeEvaluationReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        if (!await ExistsAsync(connection, transaction,
                "SELECT 1 FROM evaluation_run WHERE evaluation_id = $id LIMIT 1;",
                evaluationId, cancellationToken))
        {
            return ClassifyRetentionPolicy.ReferenceFlags.NotFound;
        }

        var flags = ClassifyRetentionPolicy.ReferenceFlags.None;
        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM apply_preview WHERE evaluation_id = $id LIMIT 1;",
                evaluationId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.ApplyPreviewEvaluation;
        }

        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM classification_feedback WHERE evaluation_id = $id LIMIT 1;",
                evaluationId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.Feedback;
        }

        return flags;
    }

    public async Task<ClassifyRetentionPolicy.ReferenceFlags> ProbePreviewReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string previewId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        if (!await ExistsAsync(connection, transaction,
                "SELECT 1 FROM apply_preview WHERE preview_id = $id LIMIT 1;",
                previewId, cancellationToken))
        {
            return ClassifyRetentionPolicy.ReferenceFlags.NotFound;
        }

        var flags = ClassifyRetentionPolicy.ReferenceFlags.None;
        if (await ExistsAsync(connection, transaction,
                "SELECT 1 FROM apply_run WHERE preview_id = $id LIMIT 1;",
                previewId, cancellationToken))
        {
            flags |= ClassifyRetentionPolicy.ReferenceFlags.ApplyRun;
            flags |= ClassifyRetentionPolicy.ReferenceFlags.LedgerProvenance;
        }

        return flags;
    }

    public async Task<bool> TryAbandonEvaluationLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE evaluation_run
            SET lifecycle_state = 'abandoned'
            WHERE evaluation_id = $id
              AND lifecycle_state = 'running';
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryAbandonValidationLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string validationRunId,
        string completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE validation_run
            SET lifecycle_state = 'abandoned',
                completed_at = $completed
            WHERE validation_run_id = $id
              AND lifecycle_state = 'running'
              AND completed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", validationRunId);
        command.Parameters.AddWithValue("$completed", completedAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Expired previews with no apply_run reference and no existing tombstone.
    /// Returns preview ids only (no payloads).
    /// </summary>
    public async Task<IReadOnlyList<(string PreviewId, string ExpiresAt)>> ListExpiredUnreferencedPreviewsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.preview_id, p.expires_at
            FROM apply_preview p
            WHERE NOT EXISTS (SELECT 1 FROM apply_run r WHERE r.preview_id = p.preview_id)
              AND NOT EXISTS (
                  SELECT 1 FROM abandonment_tombstone t
                  WHERE t.subject_type = 'preview' AND t.subject_id = p.preview_id)
            ORDER BY p.preview_id ASC;
            """;
        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var expires = reader.GetString(1);
            if (ClassifyRetentionPolicy.IsExpired(expires, nowUtc))
            {
                rows.Add((id, expires));
            }
        }

        return rows;
    }

    public async Task<long> CountTombstonesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM abandonment_tombstone;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountCleanupEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM cleanup_event;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null && scalar is not DBNull;
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }
}
