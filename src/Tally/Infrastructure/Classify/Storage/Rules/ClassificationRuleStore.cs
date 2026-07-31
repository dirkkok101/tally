using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Domain.Classify.Rules;

namespace Tally.Infrastructure.Classify.Storage.Rules;

/// <summary>
/// Immutable classification rule persistence for draft save
/// (DM-CLASSIFY-RULE-LIFECYCLE / TASK-CLASSIFY-RULEBOOK-RULE-DRAFT-SAVE).
/// Append-only inserts; never updates rule or condition rows; never touches active_rule_set.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationRuleStore
{
    public const string LifecycleDraft = "draft";
    public const string OriginOwnerAuthored = "owner_authored";
    public const string OriginFeedbackDerived = "feedback_derived";

    public async Task<ClassifyRuleRow?> GetRuleAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_id, created_at, created_by
            FROM classification_rule
            WHERE rule_id = $rule_id;
            """;
        command.Parameters.AddWithValue("$rule_id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapRule(reader) : null;
    }

    public async Task InsertRuleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_rule (rule_id, created_at, created_by)
            VALUES ($rule_id, $created_at, $created_by);
            """;
        command.Parameters.AddWithValue("$rule_id", row.RuleId);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        command.Parameters.AddWithValue("$created_by", row.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ClassifyRuleVersionRow?> GetRuleVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                   scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                   broad_apply_allowed, validation_run_id, created_at, created_by
            FROM rule_version
            WHERE rule_version_id = $id;
            """;
        command.Parameters.AddWithValue("$id", ruleVersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapRuleVersion(reader) : null;
    }

    /// <summary>
    /// Append one immutable draft rule_version and its rule_condition rows.
    /// Always lifecycle_state=draft, broad_apply_allowed=0, rule_origin as supplied.
    /// Does not mutate active_rule_set or any prior version row.
    /// </summary>
    public async Task InsertDraftVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleVersionRow version,
        IReadOnlyList<RuleCondition> conditions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(conditions);
        if (!string.Equals(version.LifecycleState, LifecycleDraft, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ClassificationRuleStore only inserts draft lifecycle rows.");
        }

        if (version.BroadApplyAllowed != 0)
        {
            throw new InvalidOperationException("Draft rule versions must not grant broad_apply_allowed.");
        }

        if (version.ScopeHash.Length != 64)
        {
            throw new InvalidOperationException("scope_hash must be a 64-character hex SHA-256 digest.");
        }

        if (conditions.Count == 0)
        {
            throw new InvalidOperationException("A draft rule version must have at least one condition.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO rule_version (
                    rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                    scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                    broad_apply_allowed, validation_run_id, created_at, created_by
                ) VALUES (
                    $rule_version_id, $rule_id, $prior_version_id, $normalization_version, $category_id,
                    $scope_hash, $rule_origin, $source_feedback_id, $reason, $lifecycle_state,
                    $broad_apply_allowed, $validation_run_id, $created_at, $created_by
                );
                """;
            command.Parameters.AddWithValue("$rule_version_id", version.RuleVersionId);
            command.Parameters.AddWithValue("$rule_id", version.RuleId);
            command.Parameters.AddWithValue("$prior_version_id", (object?)version.PriorVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$normalization_version", version.NormalizationVersion);
            command.Parameters.AddWithValue("$category_id", version.CategoryId);
            command.Parameters.AddWithValue("$scope_hash", version.ScopeHash);
            command.Parameters.AddWithValue("$rule_origin", version.RuleOrigin);
            command.Parameters.AddWithValue("$source_feedback_id", (object?)version.SourceFeedbackId ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", version.Reason);
            command.Parameters.AddWithValue("$lifecycle_state", version.LifecycleState);
            command.Parameters.AddWithValue("$broad_apply_allowed", version.BroadApplyAllowed);
            command.Parameters.AddWithValue("$validation_run_id", (object?)version.ValidationRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at", version.CreatedAt);
            command.Parameters.AddWithValue("$created_by", version.CreatedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var condition in conditions.OrderBy(c => c.Ordinal).ThenBy(c => c.FieldKey, StringComparer.Ordinal))
        {
            await InsertConditionAsync(connection, transaction, version.RuleVersionId, condition, cancellationToken);
        }
    }

    public async Task InsertConditionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ruleVersionId,
        RuleCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        ArgumentNullException.ThrowIfNull(condition);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rule_condition (
                rule_version_id, ordinal, field_key, predicate_kind,
                value_text, value_minor_min, value_minor_max, enum_value
            ) VALUES (
                $rule_version_id, $ordinal, $field_key, $predicate_kind,
                $value_text, $value_minor_min, $value_minor_max, $enum_value
            );
            """;
        command.Parameters.AddWithValue("$rule_version_id", ruleVersionId);
        command.Parameters.AddWithValue("$ordinal", condition.Ordinal);
        command.Parameters.AddWithValue("$field_key", condition.FieldKey);
        command.Parameters.AddWithValue("$predicate_kind", condition.PredicateKind);
        command.Parameters.AddWithValue("$value_text", (object?)condition.ValueText ?? DBNull.Value);
        command.Parameters.AddWithValue("$value_minor_min", (object?)condition.ValueMinorMin ?? DBNull.Value);
        command.Parameters.AddWithValue("$value_minor_max", (object?)condition.ValueMinorMax ?? DBNull.Value);
        command.Parameters.AddWithValue("$enum_value", (object?)condition.EnumValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RuleCondition>> ListConditionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, field_key, predicate_kind, value_text, value_minor_min, value_minor_max, enum_value
            FROM rule_condition
            WHERE rule_version_id = $id
            ORDER BY ordinal ASC, field_key ASC;
            """;
        command.Parameters.AddWithValue("$id", ruleVersionId);
        var rows = new List<RuleCondition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(RuleCondition.Create(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                valueText: reader.IsDBNull(3) ? null : reader.GetString(3),
                valueMinorMin: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                valueMinorMax: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                enumValue: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    /// <summary>Returns the singleton active_rule_set pointer, or null when no rule set is active.</summary>
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

    public async Task<long> CountRuleVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_version;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountRulesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM classification_rule;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountConditionsForVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_condition WHERE rule_version_id = $id;";
        command.Parameters.AddWithValue("$id", ruleVersionId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}

/// <summary>Read-only view of the active_rule_set singleton pointer.</summary>
public sealed record ClassifyActiveRuleSetPointer(
    int SingletonId,
    string RuleSetVersionId,
    long ActivationEpoch);
