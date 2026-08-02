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
    /// Snapshot-bound filtered count (high-water + AND filters). Does not materialize rows.
    /// Effective lifecycle: active membership ⇒ active; otherwise stored lifecycle_state mapping.
    /// </summary>
    public async Task<int> CountFilteredBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        string? logicalRuleId,
        string? categoryId,
        bool? activeMembership,
        string? effectiveLifecycleFilter,
        string? activeRuleSetVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterCreatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterRuleVersionId);

        var sql = new StringBuilder("""
            SELECT COUNT(*)
            FROM rule_version rv
            WHERE (rv.created_at < $hw_created
                   OR (rv.created_at = $hw_created AND rv.rule_version_id <= $hw_rule))
            """);
        AppendFilterClauses(
            sql,
            logicalRuleId,
            categoryId,
            activeMembership,
            effectiveLifecycleFilter,
            activeRuleSetVersionId);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ToString();
        BindFilterParameters(
            command,
            highWaterCreatedAt,
            highWaterRuleVersionId,
            logicalRuleId,
            categoryId,
            activeMembership,
            effectiveLifecycleFilter,
            activeRuleSetVersionId,
            afterCreatedAt: null,
            afterRuleVersionId: null,
            limit: null);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Keyset page: at most <paramref name="limit"/> rows after high-water + AND filters + resume key.
    /// Ordered by created_at ASC, rule_version_id ASC. No OFFSET.
    /// </summary>
    public async Task<IReadOnlyList<ClassifyRuleVersionRow>> ListRuleVersionsKeysetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        string? logicalRuleId,
        string? categoryId,
        bool? activeMembership,
        string? effectiveLifecycleFilter,
        string? activeRuleSetVersionId,
        string? afterCreatedAt,
        string? afterRuleVersionId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterCreatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(highWaterRuleVersionId);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Keyset limit must be >= 1.");
        }

        var sql = new StringBuilder("""
            SELECT rv.rule_version_id, rv.rule_id, rv.prior_version_id, rv.normalization_version, rv.category_id,
                   rv.scope_hash, rv.rule_origin, rv.source_feedback_id, rv.reason, rv.lifecycle_state,
                   rv.broad_apply_allowed, rv.validation_run_id, rv.created_at, rv.created_by
            FROM rule_version rv
            WHERE (rv.created_at < $hw_created
                   OR (rv.created_at = $hw_created AND rv.rule_version_id <= $hw_rule))
            """);
        AppendFilterClauses(
            sql,
            logicalRuleId,
            categoryId,
            activeMembership,
            effectiveLifecycleFilter,
            activeRuleSetVersionId);

        if (!string.IsNullOrWhiteSpace(afterCreatedAt) && !string.IsNullOrWhiteSpace(afterRuleVersionId))
        {
            sql.Append("""

  AND (rv.created_at > $after_created
       OR (rv.created_at = $after_created AND rv.rule_version_id > $after_rule))
""");
        }

        sql.Append("\nORDER BY rv.created_at ASC, rv.rule_version_id ASC\nLIMIT $limit;");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ToString();
        BindFilterParameters(
            command,
            highWaterCreatedAt,
            highWaterRuleVersionId,
            logicalRuleId,
            categoryId,
            activeMembership,
            effectiveLifecycleFilter,
            activeRuleSetVersionId,
            afterCreatedAt,
            afterRuleVersionId,
            limit);

        var rows = new List<ClassifyRuleVersionRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ClassifyRowMapper.MapRuleVersion(reader));
        }

        return rows;
    }

    /// <summary>
    /// Legacy unbounded listing kept only for callers that need full HW-bounded sets.
    /// Prefer <see cref="ListRuleVersionsKeysetAsync"/> for owner-local pages.
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
        // Map stored lifecycle filter name when provided (exact lifecycle_state equality).
        return await ListRuleVersionsKeysetAsync(
            connection,
            transaction,
            highWaterCreatedAt,
            highWaterRuleVersionId,
            logicalRuleId,
            categoryId,
            activeMembership: null,
            effectiveLifecycleFilter: lifecycleState,
            activeRuleSetVersionId: null,
            afterCreatedAt: null,
            afterRuleVersionId: null,
            limit: int.MaxValue,
            cancellationToken);
    }

    private static void AppendFilterClauses(
        StringBuilder sql,
        string? logicalRuleId,
        string? categoryId,
        bool? activeMembership,
        string? effectiveLifecycleFilter,
        string? activeRuleSetVersionId)
    {
        if (!string.IsNullOrWhiteSpace(logicalRuleId))
        {
            sql.Append("\n  AND rv.rule_id = $rule_id");
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            sql.Append("\n  AND rv.category_id = $category_id");
        }

        // Active membership: EXISTS against frozen active rule-set members (when pointer present).
        if (activeMembership is true)
        {
            if (string.IsNullOrWhiteSpace(activeRuleSetVersionId))
            {
                sql.Append("\n  AND 0"); // no active set ⇒ no members
            }
            else
            {
                sql.Append("""

  AND EXISTS (
    SELECT 1 FROM rule_set_member m
    WHERE m.rule_set_version_id = $active_rsv
      AND m.rule_version_id = rv.rule_version_id)
""");
            }
        }
        else if (activeMembership is false)
        {
            if (string.IsNullOrWhiteSpace(activeRuleSetVersionId))
            {
                // No active set ⇒ every row is non-member; no extra clause.
            }
            else
            {
                sql.Append("""

  AND NOT EXISTS (
    SELECT 1 FROM rule_set_member m
    WHERE m.rule_set_version_id = $active_rsv
      AND m.rule_version_id = rv.rule_version_id)
""");
            }
        }

        // Effective lifecycle filter (public enum wire values: draft|active|retired|superseded).
        // Membership implies effective active regardless of stored lifecycle_state.
        if (!string.IsNullOrWhiteSpace(effectiveLifecycleFilter))
        {
            if (string.Equals(effectiveLifecycleFilter, "active", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(activeRuleSetVersionId))
                {
                    sql.Append("""

  AND rv.lifecycle_state IN ('active', 'active_with_broad_apply')
""");
                }
                else
                {
                    sql.Append("""

  AND (
    EXISTS (
      SELECT 1 FROM rule_set_member m
      WHERE m.rule_set_version_id = $active_rsv
        AND m.rule_version_id = rv.rule_version_id)
    OR (
      NOT EXISTS (
        SELECT 1 FROM rule_set_member m
        WHERE m.rule_set_version_id = $active_rsv
          AND m.rule_version_id = rv.rule_version_id)
      AND rv.lifecycle_state IN ('active', 'active_with_broad_apply')))
""");
                }
            }
            else if (string.Equals(effectiveLifecycleFilter, "draft", StringComparison.Ordinal))
            {
                // Non-member + stored draft/validated
                if (string.IsNullOrWhiteSpace(activeRuleSetVersionId))
                {
                    sql.Append("\n  AND rv.lifecycle_state IN ('draft', 'validated')");
                }
                else
                {
                    sql.Append("""

  AND NOT EXISTS (
    SELECT 1 FROM rule_set_member m
    WHERE m.rule_set_version_id = $active_rsv
      AND m.rule_version_id = rv.rule_version_id)
  AND rv.lifecycle_state IN ('draft', 'validated')
""");
                }
            }
            else if (string.Equals(effectiveLifecycleFilter, "retired", StringComparison.Ordinal)
                     || string.Equals(effectiveLifecycleFilter, "superseded", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(activeRuleSetVersionId))
                {
                    sql.Append("\n  AND rv.lifecycle_state = $lifecycle");
                }
                else
                {
                    sql.Append("""

  AND NOT EXISTS (
    SELECT 1 FROM rule_set_member m
    WHERE m.rule_set_version_id = $active_rsv
      AND m.rule_version_id = rv.rule_version_id)
  AND rv.lifecycle_state = $lifecycle
""");
                }
            }
        }
    }

    private static void BindFilterParameters(
        SqliteCommand command,
        string highWaterCreatedAt,
        string highWaterRuleVersionId,
        string? logicalRuleId,
        string? categoryId,
        bool? activeMembership,
        string? effectiveLifecycleFilter,
        string? activeRuleSetVersionId,
        string? afterCreatedAt,
        string? afterRuleVersionId,
        int? limit)
    {
        command.Parameters.AddWithValue("$hw_created", highWaterCreatedAt);
        command.Parameters.AddWithValue("$hw_rule", highWaterRuleVersionId);
        if (!string.IsNullOrWhiteSpace(logicalRuleId))
        {
            command.Parameters.AddWithValue("$rule_id", logicalRuleId);
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            command.Parameters.AddWithValue("$category_id", categoryId);
        }

        // Bind whenever a non-null active set id is supplied; filter clauses reference it only when needed.
        if (!string.IsNullOrWhiteSpace(activeRuleSetVersionId)
            && (activeMembership is not null || !string.IsNullOrWhiteSpace(effectiveLifecycleFilter)))
        {
            command.Parameters.AddWithValue("$active_rsv", activeRuleSetVersionId);
        }

        if (string.Equals(effectiveLifecycleFilter, "retired", StringComparison.Ordinal)
            || string.Equals(effectiveLifecycleFilter, "superseded", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$lifecycle", effectiveLifecycleFilter);
        }

        if (!string.IsNullOrWhiteSpace(afterCreatedAt) && !string.IsNullOrWhiteSpace(afterRuleVersionId))
        {
            command.Parameters.AddWithValue("$after_created", afterCreatedAt);
            command.Parameters.AddWithValue("$after_rule", afterRuleVersionId);
        }

        if (limit is not null)
        {
            command.Parameters.AddWithValue("$limit", limit.Value);
        }
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
