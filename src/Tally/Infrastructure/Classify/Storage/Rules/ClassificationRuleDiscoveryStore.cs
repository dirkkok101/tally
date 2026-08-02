using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Data.Sqlite;
using Tally.Domain.Classify.Evaluation;

namespace Tally.Infrastructure.Classify.Storage.Rules;

/// <summary>
/// Bounded append-only reads for classify.rule.list and rule-set.active.get
/// (DM-CLASSIFY-RULE-DISCOVERY / FR-CLASSIFY-RULEBOOK-DISCOVERY / bd-2vbg).
/// Freezes catalogue high-water; never mutates rule authority or Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationRuleDiscoveryStore
{
    /// <summary>
    /// Global catalogue high-water: max (created_at, rule_version_id) over all rule_version rows.
    /// Null when the catalogue is empty.
    /// </summary>
    public async Task<(string CreatedAt, string RuleVersionId)?> GetCatalogueHighWaterAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT created_at, rule_version_id
            FROM rule_version
            ORDER BY created_at DESC, rule_version_id DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<int> CountAllRuleVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_version;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Overall catalogue count at or before the frozen high-water (snapshot-bound total).
    /// Concurrent appends after first page are excluded.
    /// </summary>
    public async Task<int> CountRuleVersionsBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterCreatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterRuleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM rule_version
            WHERE created_at < $hw_created
               OR (created_at = $hw_created AND rule_version_id <= $hw_rule);
            """;
        command.Parameters.AddWithValue("$hw_created", highWaterCreatedAt);
        command.Parameters.AddWithValue("$hw_rule", highWaterRuleVersionId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Distinct category IDs for every rule_version at or before high-water.
    /// Used for cursor CategoryLifecycleFingerprint so draft/non-member categories bind the traversal.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListCategoryIdsBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterCreatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterRuleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT category_id
            FROM rule_version
            WHERE created_at < $hw_created
               OR (created_at = $hw_created AND rule_version_id <= $hw_rule)
            ORDER BY category_id ASC;
            """;
        command.Parameters.AddWithValue("$hw_created", highWaterCreatedAt);
        command.Parameters.AddWithValue("$hw_rule", highWaterRuleVersionId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// List rule versions at or before high-water with optional static AND filters.
    /// Ordered by created_at ASC, rule_version_id ASC. Active membership is evaluated in-process.
    /// </summary>
    public async Task<IReadOnlyList<ClassifyRuleVersionRow>> ListRuleVersionsBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        string? logicalRuleId,
        string? lifecycleState,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterCreatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterRuleVersionId);

        var sql = new StringBuilder("""
            SELECT rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                   scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                   broad_apply_allowed, validation_run_id, created_at, created_by
            FROM rule_version
            WHERE (created_at < $hw_created
                   OR (created_at = $hw_created AND rule_version_id <= $hw_rule))
            """);

        if (!string.IsNullOrWhiteSpace(logicalRuleId))
        {
            sql.Append("\n  AND rule_id = $rule_id");
        }

        if (!string.IsNullOrWhiteSpace(lifecycleState))
        {
            sql.Append("\n  AND lifecycle_state = $lifecycle");
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            sql.Append("\n  AND category_id = $category_id");
        }

        sql.Append("\nORDER BY created_at ASC, rule_version_id ASC;");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$hw_created", highWaterCreatedAt);
        command.Parameters.AddWithValue("$hw_rule", highWaterRuleVersionId);
        if (!string.IsNullOrWhiteSpace(logicalRuleId))
        {
            command.Parameters.AddWithValue("$rule_id", logicalRuleId);
        }

        if (!string.IsNullOrWhiteSpace(lifecycleState))
        {
            command.Parameters.AddWithValue("$lifecycle", lifecycleState);
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            command.Parameters.AddWithValue("$category_id", categoryId);
        }

        var rows = new List<ClassifyRuleVersionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ClassifyRowMapper.MapRuleVersion(reader));
        }

        return rows;
    }

    public async Task<IReadOnlySet<string>> GetActiveMemberIdsAsync(
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
            WHERE rule_set_version_id = $id;
            """;
        command.Parameters.AddWithValue("$id", ruleSetVersionId);
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>
    /// Latest lifecycle timestamps for a rule version from append-only events
    /// (validated/activated/retired). Never returns owner reason prose.
    /// </summary>
    public async Task<RuleLifecycleTimestamps> GetLifecycleTimestampsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT resulting_state, occurred_at
            FROM rule_lifecycle_event
            WHERE subject_id = $id
            ORDER BY occurred_at ASC, event_id ASC;
            """;
        command.Parameters.AddWithValue("$id", ruleVersionId);
        string? validatedAt = null;
        string? activatedAt = null;
        string? retiredAt = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = reader.GetString(0);
            var at = reader.GetString(1);
            if (state.Contains("validated", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Validated", StringComparison.Ordinal))
            {
                validatedAt = at;
            }

            if (state.Contains("active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "RuleSetActivated", StringComparison.Ordinal)
                || string.Equals(state, "RuleVersionActivated", StringComparison.Ordinal))
            {
                activatedAt = at;
            }

            if (state.Contains("retired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "RuleVersionRetired", StringComparison.Ordinal))
            {
                retiredAt = at;
            }
        }

        return new RuleLifecycleTimestamps(validatedAt, activatedAt, retiredAt);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Domain.Classify.Rules.RuleCondition>>> ListConditionsForVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> ruleVersionIds,
        ClassificationRuleStore ruleStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleVersionIds);
        ArgumentNullException.ThrowIfNull(ruleStore);
        var map = new Dictionary<string, IReadOnlyList<Domain.Classify.Rules.RuleCondition>>(
            ruleVersionIds.Count,
            StringComparer.Ordinal);
        foreach (var id in ruleVersionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            map[id] = await ruleStore.ListConditionsAsync(connection, transaction, id, cancellationToken);
        }

        return map;
    }

    /// <summary>Authority fingerprint over durable active pointer (version + epoch).</summary>
    public static string AuthorityFingerprint(string? ruleSetVersionId, long activationEpoch) =>
        CanonicalClassificationHasher.HashParts(
            "active_authority",
            ruleSetVersionId,
            activationEpoch.ToString(CultureInfo.InvariantCulture));

    /// <summary>Category lifecycle fingerprint over ordered (categoryId, lifecycle) tuples.</summary>
    public static string CategoryLifecycleFingerprint(
        IEnumerable<(string CategoryId, string Lifecycle)> categories) =>
        EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(categories);
}

/// <summary>Derived lifecycle timestamps from append-only events (no owner prose).</summary>
public sealed record RuleLifecycleTimestamps(
    string? ValidatedAt,
    string? ActivatedAt,
    string? RetiredAt);
