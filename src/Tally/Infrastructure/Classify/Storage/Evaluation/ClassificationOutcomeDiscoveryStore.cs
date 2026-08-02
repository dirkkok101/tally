using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Classify.Storage.Evaluation;

/// <summary>
/// Bounded keyset reads over retained classification outcomes for classify.outcome.list
/// (DM-CLASSIFY-EVALUATION-OUTCOME / FR-CLASSIFY-OUTCOME-DISCOVERY / bd-vg33).
/// Raw SQLite only — no Ledger I/O, no second outcome index, no description/amount storage.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationOutcomeDiscoveryStore
{
    /// <summary>
    /// List outcomes for one evaluation with optional static AND filters (kind, category, rule, transaction).
    /// Ordered by ordinal ASC, transaction_id ASC. Does not apply stale-state filters (computed in-process).
    /// </summary>
    public async Task<IReadOnlyList<ClassifyOutcomeRow>> ListFilteredOutcomesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        string? outcomeType,
        string? suggestedCategoryId,
        string? contributingRuleVersionId,
        string? transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        cancellationToken.ThrowIfCancellationRequested();

        var sql = new StringBuilder("""
            SELECT o.outcome_id, o.evaluation_id, o.ordinal, o.transaction_id, o.outcome_type,
                   o.category_id, o.item_lifecycle_fingerprint, o.safe_reason
            FROM classification_outcome o
            """);

        if (!string.IsNullOrWhiteSpace(contributingRuleVersionId))
        {
            sql.Append("""
                
                WHERE o.evaluation_id = $evaluation_id
                  AND EXISTS (
                    SELECT 1 FROM match_evidence m
                    WHERE m.outcome_id = o.outcome_id
                      AND m.rule_version_id = $rule_version_id
                  )
                """);
        }
        else
        {
            sql.Append("\nWHERE o.evaluation_id = $evaluation_id");
        }

        if (!string.IsNullOrWhiteSpace(outcomeType))
        {
            sql.Append("\n  AND o.outcome_type = $outcome_type");
        }

        if (!string.IsNullOrWhiteSpace(suggestedCategoryId))
        {
            sql.Append("\n  AND o.category_id = $category_id");
        }

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            sql.Append("\n  AND o.transaction_id = $transaction_id");
        }

        sql.Append("\nORDER BY o.ordinal ASC, o.transaction_id ASC;");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$evaluation_id", evaluationId);
        if (!string.IsNullOrWhiteSpace(contributingRuleVersionId))
        {
            command.Parameters.AddWithValue("$rule_version_id", contributingRuleVersionId);
        }

        if (!string.IsNullOrWhiteSpace(outcomeType))
        {
            command.Parameters.AddWithValue("$outcome_type", outcomeType);
        }

        if (!string.IsNullOrWhiteSpace(suggestedCategoryId))
        {
            command.Parameters.AddWithValue("$category_id", suggestedCategoryId);
        }

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            command.Parameters.AddWithValue("$transaction_id", transactionId);
        }

        var rows = new List<ClassifyOutcomeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ClassifyRowMapper.MapOutcome(reader));
        }

        return rows;
    }

    /// <summary>Overall retained outcome count for one evaluation (partition identity check).</summary>
    public async Task<int> CountOutcomesForEvaluationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM classification_outcome WHERE evaluation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>Batch load match evidence for a set of outcome ids (ordered by outcome then rule/field).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>>> ListEvidenceForOutcomesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> outcomeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcomeIds);
        var map = new Dictionary<string, List<ClassifyMatchEvidenceRow>>(outcomeIds.Count, StringComparer.Ordinal);
        if (outcomeIds.Count == 0)
        {
            return map.ToDictionary(
                static kv => kv.Key,
                static kv => (IReadOnlyList<ClassifyMatchEvidenceRow>)kv.Value,
                StringComparer.Ordinal);
        }

        // Chunk to keep parameter counts bounded.
        const int chunkSize = 200;
        for (var offset = 0; offset < outcomeIds.Count; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = outcomeIds.Skip(offset).Take(chunkSize).ToArray();
            var paramNames = new string[chunk.Length];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < chunk.Length; i++)
            {
                paramNames[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(paramNames[i], chunk[i]);
            }

            command.CommandText = $"""
                SELECT outcome_id, rule_version_id, condition_id, field_key, predicate_kind, normalized_value_hash
                FROM match_evidence
                WHERE outcome_id IN ({string.Join(',', paramNames)})
                ORDER BY outcome_id ASC, rule_version_id ASC, condition_id ASC, field_key ASC, predicate_kind ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new ClassifyMatchEvidenceRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5));
                if (!map.TryGetValue(row.OutcomeId, out var list))
                {
                    list = [];
                    map[row.OutcomeId] = list;
                }

                list.Add(row);
            }
        }

        foreach (var id in outcomeIds)
        {
            map.TryAdd(id, []);
        }

        return map.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<ClassifyMatchEvidenceRow>)kv.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Canonical result fingerprint over all retained outcomes of one evaluation
    /// (ordinal, transaction id, outcome type, outcome id) — no private payloads.
    /// </summary>
    public static string ComputeResultFingerprint(IReadOnlyList<ClassifyOutcomeRow> orderedOutcomes)
    {
        ArgumentNullException.ThrowIfNull(orderedOutcomes);
        var parts = new List<string?>(orderedOutcomes.Count * 4);
        foreach (var o in orderedOutcomes.OrderBy(x => x.Ordinal).ThenBy(x => x.TransactionId, StringComparer.Ordinal))
        {
            parts.Add(o.Ordinal.ToString(CultureInfo.InvariantCulture));
            parts.Add(o.TransactionId);
            parts.Add(o.OutcomeType);
            parts.Add(o.OutcomeId);
        }

        return Domain.Classify.Evaluation.CanonicalClassificationHasher.HashParts(parts.ToArray());
    }
}
