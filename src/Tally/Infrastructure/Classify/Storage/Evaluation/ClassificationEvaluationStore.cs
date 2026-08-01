using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Domain.Classify.Evaluation;

namespace Tally.Infrastructure.Classify.Storage.Evaluation;

/// <summary>
/// Atomic evaluation_run / classification_outcome / match_evidence persistence
/// (DM-CLASSIFY-EVALUATION-OUTCOME / TASK-CLASSIFY-RULEBOOK-EVALUATION-WORKFLOW).
/// One transaction publishes a complete evaluation or nothing — never partial outcomes.
/// Never stores raw source descriptions, amounts, or private paths.
/// Never mutates Ledger or active_rule_set.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationEvaluationStore
{
    /// <summary>
    /// Persist a completed evaluation with ordered outcomes and bounded match evidence atomically.
    /// Caller owns the SQLite transaction and must commit only when this method returns successfully.
    /// </summary>
    public async Task PersistCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyEvaluationRunRow run,
        IReadOnlyList<PersistedEvaluationOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(outcomes);
        cancellationToken.ThrowIfCancellationRequested();

        if (outcomes.Count != run.InputCount)
        {
            throw new InvalidOperationException(
                "Persisted outcome count must equal evaluation input_count (no partial evaluation).");
        }

        if (run.SuggestionCount + run.NoSuggestionCount + run.ConflictCount + run.StaleCount != run.InputCount)
        {
            throw new InvalidOperationException(
                "Outcome partition totals must equal input_count before persistence.");
        }

        await InsertEvaluationRunAsync(connection, transaction, run, cancellationToken);

        var ordered = outcomes
            .OrderBy(o => o.Outcome.Ordinal)
            .ThenBy(o => o.Outcome.TransactionId, StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = ordered[i];
            if (row.Outcome.Ordinal != i)
            {
                throw new InvalidOperationException(
                    "Outcome ordinals must be contiguous from zero before persistence.");
            }

            await InsertOutcomeAsync(connection, transaction, row.Outcome, cancellationToken);
            await InsertEvidenceAsync(connection, transaction, row.Outcome.OutcomeId, row.Evidence, cancellationToken);
        }
    }

    public async Task InsertEvaluationRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyEvaluationRunRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                $evaluation_id, $operation_idempotency_key, $rule_set_version_id, $normalization_version,
                $ledger_contract_version, $projection_version, $store_generation_fingerprint, $snapshot_id,
                $snapshot_expires_at, $category_lifecycle_fingerprint, $ordered_items_fingerprint,
                $input_count, $suggestion_count, $no_suggestion_count, $conflict_count, $stale_count,
                $lifecycle_state, $actor, $created_at
            );
            """;
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$operation_idempotency_key", (object?)row.OperationIdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$rule_set_version_id", row.RuleSetVersionId);
        command.Parameters.AddWithValue("$normalization_version", row.NormalizationVersion);
        command.Parameters.AddWithValue("$ledger_contract_version", row.LedgerContractVersion);
        command.Parameters.AddWithValue("$projection_version", row.ProjectionVersion);
        command.Parameters.AddWithValue("$store_generation_fingerprint", row.StoreGenerationFingerprint);
        command.Parameters.AddWithValue("$snapshot_id", row.SnapshotId);
        command.Parameters.AddWithValue("$snapshot_expires_at", row.SnapshotExpiresAt);
        command.Parameters.AddWithValue("$category_lifecycle_fingerprint", row.CategoryLifecycleFingerprint);
        command.Parameters.AddWithValue("$ordered_items_fingerprint", row.OrderedItemsFingerprint);
        command.Parameters.AddWithValue("$input_count", row.InputCount);
        command.Parameters.AddWithValue("$suggestion_count", row.SuggestionCount);
        command.Parameters.AddWithValue("$no_suggestion_count", row.NoSuggestionCount);
        command.Parameters.AddWithValue("$conflict_count", row.ConflictCount);
        command.Parameters.AddWithValue("$stale_count", row.StaleCount);
        command.Parameters.AddWithValue("$lifecycle_state", row.LifecycleState);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyOutcomeRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                $outcome_id, $evaluation_id, $ordinal, $transaction_id, $outcome_type,
                $category_id, $item_lifecycle_fingerprint, $safe_reason
            );
            """;
        command.Parameters.AddWithValue("$outcome_id", row.OutcomeId);
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$ordinal", row.Ordinal);
        command.Parameters.AddWithValue("$transaction_id", row.TransactionId);
        command.Parameters.AddWithValue("$outcome_type", row.OutcomeType);
        command.Parameters.AddWithValue("$category_id", (object?)row.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$item_lifecycle_fingerprint", row.ItemLifecycleFingerprint);
        command.Parameters.AddWithValue("$safe_reason", row.SafeReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string outcomeId,
        IReadOnlyList<MatchEvidence> evidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeId);
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var item in MatchEvidenceOrdering.Order(evidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO match_evidence (
                    outcome_id, rule_version_id, condition_id, field_key, predicate_kind, normalized_value_hash
                ) VALUES (
                    $outcome_id, $rule_version_id, $condition_id, $field_key, $predicate_kind, $normalized_value_hash
                );
                """;
            command.Parameters.AddWithValue("$outcome_id", outcomeId);
            command.Parameters.AddWithValue("$rule_version_id", item.RuleVersionId);
            command.Parameters.AddWithValue("$condition_id", item.ConditionId);
            command.Parameters.AddWithValue("$field_key", item.FieldKey);
            command.Parameters.AddWithValue("$predicate_kind", item.PredicateKind);
            command.Parameters.AddWithValue("$normalized_value_hash", item.NormalizedValueHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<ClassifyEvaluationRunRow?> GetRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                   ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                   snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                   input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                   lifecycle_state, actor, created_at
            FROM evaluation_run
            WHERE evaluation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapEvaluationRun(reader) : null;
    }

    public async Task<IReadOnlyList<ClassifyOutcomeRow>> ListOutcomesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                   category_id, item_lifecycle_fingerprint, safe_reason
            FROM classification_outcome
            WHERE evaluation_id = $id
            ORDER BY ordinal ASC, transaction_id ASC;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        var rows = new List<ClassifyOutcomeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ClassifyRowMapper.MapOutcome(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ClassifyMatchEvidenceRow>> ListEvidenceForOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string outcomeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outcome_id, rule_version_id, condition_id, field_key, predicate_kind, normalized_value_hash
            FROM match_evidence
            WHERE outcome_id = $id
            ORDER BY rule_version_id ASC, condition_id ASC, field_key ASC, predicate_kind ASC;
            """;
        command.Parameters.AddWithValue("$id", outcomeId);
        var rows = new List<ClassifyMatchEvidenceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClassifyMatchEvidenceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    public async Task<long> CountEvaluationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM evaluation_run;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountOutcomesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM classification_outcome;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM match_evidence;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}

/// <summary>One durable outcome plus its bounded match evidence for atomic persistence.</summary>
public sealed record PersistedEvaluationOutcome(
    ClassifyOutcomeRow Outcome,
    IReadOnlyList<MatchEvidence> Evidence);

/// <summary>Durable match_evidence row (no raw descriptions or financial payloads).</summary>
public sealed record ClassifyMatchEvidenceRow(
    string OutcomeId,
    string RuleVersionId,
    string ConditionId,
    string FieldKey,
    string PredicateKind,
    string NormalizedValueHash);
