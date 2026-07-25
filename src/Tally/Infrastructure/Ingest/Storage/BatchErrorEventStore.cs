using Microsoft.Data.Sqlite;
using Tally.Contracts.Ingest;

namespace Tally.Infrastructure.Ingest.Storage;

public sealed class BatchErrorEventStore
{
    public async Task AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string errorEventId,
        IngestError error,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error.BatchId);

        const string sql = """
            INSERT INTO batch_error_event (
                error_event_id, batch_id, sequence, code, category, safe_message, candidate_id,
                mutation_possibility, durable_state, retry_action, field, recorded_at)
            VALUES (
                $errorEventId,
                $batchId,
                (SELECT COALESCE(MAX(sequence), 0) + 1 FROM batch_error_event WHERE batch_id = $batchId),
                $code,
                $category,
                $safeMessage,
                $candidateId,
                $mutationPossibility,
                $durableState,
                $retryAction,
                $field,
                $recordedAt);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$errorEventId", errorEventId);
        command.Parameters.AddWithValue("$batchId", error.BatchId);
        command.Parameters.AddWithValue("$code", error.Code);
        command.Parameters.AddWithValue("$category", (int)error.Category);
        command.Parameters.AddWithValue("$safeMessage", error.SafeMessage);
        command.Parameters.AddWithValue("$candidateId", (object?)error.CandidateId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mutationPossibility", (int)error.MutationPossibility);
        command.Parameters.AddWithValue("$durableState", (object?)error.DurableState ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryAction", (int)error.RetryAction);
        command.Parameters.AddWithValue("$field", (object?)error.Field ?? DBNull.Value);
        command.Parameters.AddWithValue("$recordedAt", recordedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IngestError?> LatestAsync(SqliteConnection connection, string batchId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT code, category, safe_message, batch_id, candidate_id, mutation_possibility,
                   durable_state, retry_action, field
            FROM batch_error_event
            WHERE batch_id = $batchId
            ORDER BY sequence DESC
            LIMIT 1;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IngestError(
            reader.GetString(0),
            (IngestErrorCategory)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (MutationPossibility)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            (IngestRetryAction)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}
