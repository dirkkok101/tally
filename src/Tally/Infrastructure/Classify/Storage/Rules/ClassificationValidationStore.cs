using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Classify.Operations;

namespace Tally.Infrastructure.Classify.Storage.Rules;

/// <summary>
/// Aggregate-only validation_run / validation_report persistence
/// (DM-CLASSIFY-VALIDATION-RUN / TASK-CLASSIFY-RULEBOOK-RULE-VALIDATION).
/// Never stores paths, descriptions, tokens, amounts, expected outcomes, or raw rows.
/// Never mutates active_rule_set.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationValidationStore
{
    public const string LifecycleRunning = "running";
    public const string LifecycleCompleted = "completed";
    public const string LifecycleFailed = "failed";

    public async Task InsertRunningAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassificationValidationRunRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.LifecycleState, LifecycleRunning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Initial validation_run rows must be lifecycle_state=running.");
        }

        if (row.CompletedAt is not null)
        {
            throw new InvalidOperationException("Running validation_run must not set completed_at.");
        }

        RequireHex64(row.CandidateFingerprint, nameof(row.CandidateFingerprint));
        RequireHex64(row.CorpusFingerprint, nameof(row.CorpusFingerprint));
        RequireHex64(row.ExpectedOutcomeFingerprint, nameof(row.ExpectedOutcomeFingerprint));
        RequireHex64(row.CategoryLifecycleFingerprint, nameof(row.CategoryLifecycleFingerprint));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO validation_run (
                validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, completed_at, lifecycle_state, actor,
                snapshot_id, snapshot_expires_at, store_generation_fingerprint
            ) VALUES (
                $validation_run_id, $candidate_fingerprint, $rule_origin, $corpus_fingerprint,
                $expected_outcome_fingerprint, $projection_contract_version, $category_lifecycle_fingerprint,
                $normalization_version, $started_at, NULL, $lifecycle_state, $actor,
                $snapshot_id, $snapshot_expires_at, $store_generation_fingerprint
            );
            """;
        command.Parameters.AddWithValue("$validation_run_id", row.ValidationRunId);
        command.Parameters.AddWithValue("$candidate_fingerprint", row.CandidateFingerprint);
        command.Parameters.AddWithValue("$rule_origin", row.RuleOrigin);
        command.Parameters.AddWithValue("$corpus_fingerprint", row.CorpusFingerprint);
        command.Parameters.AddWithValue("$expected_outcome_fingerprint", row.ExpectedOutcomeFingerprint);
        command.Parameters.AddWithValue("$projection_contract_version", row.ProjectionContractVersion);
        command.Parameters.AddWithValue("$category_lifecycle_fingerprint", row.CategoryLifecycleFingerprint);
        command.Parameters.AddWithValue("$normalization_version", row.NormalizationVersion);
        command.Parameters.AddWithValue("$started_at", row.StartedAt);
        command.Parameters.AddWithValue("$lifecycle_state", row.LifecycleState);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$snapshot_id", (object?)row.SnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$snapshot_expires_at", (object?)row.SnapshotExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$store_generation_fingerprint",
            (object?)row.StoreGenerationFingerprint ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Append immutable aggregate report and transition validation_run running → completed.
    /// </summary>
    public async Task CompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string validationRunId,
        string completedAt,
        ClassificationValidationReportRow report,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAt);
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.ValidationRunId, validationRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Report validation_run_id must match the completed run.");
        }

        RequireHex64(report.ReportFingerprint, nameof(report.ReportFingerprint));

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO validation_report (
                    validation_run_id, total_rows, accounted_rows, suggestion_count, no_suggestion_count,
                    conflict_count, stale_count, coverage_basis_points, drift_canary_count,
                    incorrect_application_canary_count, unexplained_conflict_count,
                    owner_decision_count_before, owner_decision_count_after,
                    owner_minutes_before, owner_minutes_after, report_fingerprint,
                    outcomes_canonical_hash, activation_eligible
                ) VALUES (
                    $validation_run_id, $total_rows, $accounted_rows, $suggestion_count, $no_suggestion_count,
                    $conflict_count, $stale_count, $coverage_basis_points, $drift_canary_count,
                    $incorrect_application_canary_count, $unexplained_conflict_count,
                    $owner_decision_count_before, $owner_decision_count_after,
                    $owner_minutes_before, $owner_minutes_after, $report_fingerprint,
                    $outcomes_canonical_hash, $activation_eligible
                );
                """;
            insert.Parameters.AddWithValue("$validation_run_id", report.ValidationRunId);
            insert.Parameters.AddWithValue("$total_rows", report.TotalRows);
            insert.Parameters.AddWithValue("$accounted_rows", report.AccountedRows);
            insert.Parameters.AddWithValue("$suggestion_count", report.SuggestionCount);
            insert.Parameters.AddWithValue("$no_suggestion_count", report.NoSuggestionCount);
            insert.Parameters.AddWithValue("$conflict_count", report.ConflictCount);
            insert.Parameters.AddWithValue("$stale_count", report.StaleCount);
            insert.Parameters.AddWithValue("$coverage_basis_points", report.CoverageBasisPoints);
            insert.Parameters.AddWithValue("$drift_canary_count", report.DriftCanaryCount);
            insert.Parameters.AddWithValue("$incorrect_application_canary_count", report.IncorrectApplicationCanaryCount);
            insert.Parameters.AddWithValue("$unexplained_conflict_count", report.UnexplainedConflictCount);
            insert.Parameters.AddWithValue("$owner_decision_count_before", report.OwnerDecisionCountBefore);
            insert.Parameters.AddWithValue("$owner_decision_count_after", report.OwnerDecisionCountAfter);
            insert.Parameters.AddWithValue("$owner_minutes_before", (object?)report.OwnerMinutesBefore ?? DBNull.Value);
            insert.Parameters.AddWithValue("$owner_minutes_after", (object?)report.OwnerMinutesAfter ?? DBNull.Value);
            insert.Parameters.AddWithValue("$report_fingerprint", report.ReportFingerprint);
            insert.Parameters.AddWithValue(
                "$outcomes_canonical_hash",
                (object?)report.OutcomesCanonicalHash ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$activation_eligible",
                report.ActivationEligible is null
                    ? DBNull.Value
                    : report.ActivationEligible.Value ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE validation_run
                SET lifecycle_state = $next,
                    completed_at = $completed_at
                WHERE validation_run_id = $id AND lifecycle_state = $expected;
                """;
            update.Parameters.AddWithValue("$next", LifecycleCompleted);
            update.Parameters.AddWithValue("$completed_at", completedAt);
            update.Parameters.AddWithValue("$id", validationRunId);
            update.Parameters.AddWithValue("$expected", LifecycleRunning);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "validation_run completion requires exactly one running row.");
            }
        }
    }

    public async Task FailAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string validationRunId,
        string completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAt);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE validation_run
            SET lifecycle_state = $next,
                completed_at = $completed_at
            WHERE validation_run_id = $id AND lifecycle_state = $expected;
            """;
        update.Parameters.AddWithValue("$next", LifecycleFailed);
        update.Parameters.AddWithValue("$completed_at", completedAt);
        update.Parameters.AddWithValue("$id", validationRunId);
        update.Parameters.AddWithValue("$expected", LifecycleRunning);
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "validation_run failure requires exactly one running row.");
        }
    }

    public async Task<ClassificationValidationRunRow?> GetRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string validationRunId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                   expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                   normalization_version, started_at, completed_at, lifecycle_state, actor,
                   snapshot_id, snapshot_expires_at, store_generation_fingerprint
            FROM validation_run
            WHERE validation_run_id = $id;
            """;
        command.Parameters.AddWithValue("$id", validationRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassificationValidationRunRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    public async Task<ClassificationValidationReportRow?> GetReportAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string validationRunId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT validation_run_id, total_rows, accounted_rows, suggestion_count, no_suggestion_count,
                   conflict_count, stale_count, coverage_basis_points, drift_canary_count,
                   incorrect_application_canary_count, unexplained_conflict_count,
                   owner_decision_count_before, owner_decision_count_after,
                   owner_minutes_before, owner_minutes_after, report_fingerprint,
                   outcomes_canonical_hash, activation_eligible
            FROM validation_report
            WHERE validation_run_id = $id;
            """;
        command.Parameters.AddWithValue("$id", validationRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassificationValidationReportRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.IsDBNull(13) ? null : reader.GetDouble(13),
            reader.IsDBNull(14) ? null : reader.GetDouble(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt32(17) != 0);
    }

    /// <summary>
    /// Reconstruct a public aggregate validate result from durable stored evidence.
    /// Returns null when required reconstruction fields are absent (historical incomplete rows).
    /// Never embeds private corpus path, candidate IDs, or raw payload.
    /// </summary>
    public static ClassifyRuleValidateResult? TryReconstructValidateResult(
        ClassificationValidationRunRow run,
        ClassificationValidationReportRow report)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(run.ValidationRunId, report.ValidationRunId, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(run.SnapshotId)
            || string.IsNullOrWhiteSpace(run.SnapshotExpiresAt)
            || string.IsNullOrWhiteSpace(run.StoreGenerationFingerprint)
            || string.IsNullOrWhiteSpace(report.OutcomesCanonicalHash)
            || report.ActivationEligible is null)
        {
            return null;
        }

        return new ClassifyRuleValidateResult(
            "1.0",
            run.ValidationRunId,
            run.CandidateFingerprint,
            run.CorpusFingerprint,
            run.ExpectedOutcomeFingerprint,
            run.ProjectionContractVersion,
            run.SnapshotId!,
            run.SnapshotExpiresAt!,
            run.StoreGenerationFingerprint!,
            run.CategoryLifecycleFingerprint,
            run.NormalizationVersion,
            report.ReportFingerprint,
            report.OutcomesCanonicalHash!,
            report.TotalRows,
            report.AccountedRows,
            report.SuggestionCount,
            report.NoSuggestionCount,
            report.ConflictCount,
            report.StaleCount,
            report.CoverageBasisPoints,
            report.DriftCanaryCount,
            report.IncorrectApplicationCanaryCount,
            report.UnexplainedConflictCount,
            report.ActivationEligible.Value);
    }

    public async Task<long> CountActiveRuleSetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM active_rule_set;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static void RequireHex64(string value, string name)
    {
        if (value.Length != 64)
        {
            throw new ArgumentException($"{name} must be a 64-character hex SHA-256 digest.", name);
        }
    }
}

public sealed record ClassificationValidationRunRow(
    string ValidationRunId,
    string CandidateFingerprint,
    string RuleOrigin,
    string CorpusFingerprint,
    string ExpectedOutcomeFingerprint,
    string ProjectionContractVersion,
    string CategoryLifecycleFingerprint,
    string NormalizationVersion,
    string StartedAt,
    string? CompletedAt,
    string LifecycleState,
    string Actor,
    string? SnapshotId = null,
    string? SnapshotExpiresAt = null,
    string? StoreGenerationFingerprint = null);

public sealed record ClassificationValidationReportRow(
    string ValidationRunId,
    int TotalRows,
    int AccountedRows,
    int SuggestionCount,
    int NoSuggestionCount,
    int ConflictCount,
    int StaleCount,
    int CoverageBasisPoints,
    int DriftCanaryCount,
    int IncorrectApplicationCanaryCount,
    int UnexplainedConflictCount,
    int OwnerDecisionCountBefore,
    int OwnerDecisionCountAfter,
    double? OwnerMinutesBefore,
    double? OwnerMinutesAfter,
    string ReportFingerprint,
    string? OutcomesCanonicalHash = null,
    bool? ActivationEligible = null);
