using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Infrastructure.Classify.Storage.Evaluation;

/// <summary>
/// Bounded identity-only reads of retained no_suggestion outcomes for classify.unresolved.report
/// (FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT / bd-3ciw).
/// Never loads descriptions, amounts, accounts, tokens, or private paths into storage rows.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationUnresolvedStore
{
    public const string OutcomeTypeNoSuggestion = "no_suggestion";

    /// <summary>
    /// One retained no_suggestion identity: transaction binding for exact once-only join.
    /// No description or financial payload.
    /// </summary>
    public sealed record NoSuggestionIdentity(
        string OutcomeId,
        string EvaluationId,
        int Ordinal,
        string TransactionId,
        string ItemLifecycleFingerprint);

    /// <summary>
    /// List retained no_suggestion identities for a completed evaluation, ordered by ordinal then
    /// transaction id. Returns empty when none exist.
    /// </summary>
    public async Task<IReadOnlyList<NoSuggestionIdentity>> ListNoSuggestionIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outcome_id, evaluation_id, ordinal, transaction_id, item_lifecycle_fingerprint
            FROM classification_outcome
            WHERE evaluation_id = $id
              AND outcome_type = $type
            ORDER BY ordinal ASC, transaction_id ASC;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        command.Parameters.AddWithValue("$type", OutcomeTypeNoSuggestion);

        var rows = new List<NoSuggestionIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NoSuggestionIdentity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>Count retained no_suggestion outcomes for accounting checks.</summary>
    public async Task<long> CountNoSuggestionIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM classification_outcome
            WHERE evaluation_id = $id
              AND outcome_type = $type;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        command.Parameters.AddWithValue("$type", OutcomeTypeNoSuggestion);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}
