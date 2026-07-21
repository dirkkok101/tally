using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Infrastructure.Storage;

public sealed record StoredIdempotencyRecord(string RequestIdentity, string OperationId, string CanonicalRequestHash, string Actor, string StableResult);

public sealed class IdempotencyStore
{
    public async Task<StoredIdempotencyRecord?> FindRequestAsync(SqliteConnection connection, SqliteTransaction transaction, string requestIdentity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT idempotency_key, operation_id, canonical_request_hash, actor, stable_result FROM idempotency_record WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", requestIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    public async Task<(StoredIdempotencyRecord? Record, string? EffectType)> FindLogicalEffectAsync(SqliteConnection connection, SqliteTransaction transaction, string logicalIdentity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record.idempotency_key, record.operation_id, record.canonical_request_hash, record.actor, record.stable_result, effect.effect_type FROM logical_effect effect JOIN idempotency_record record ON record.idempotency_key = effect.idempotency_key WHERE effect.logical_identity = $identity;";
        command.Parameters.AddWithValue("$identity", logicalIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.GetString(5))
            : (null, null);
    }

    public async Task CommitAsync(SqliteConnection connection, SqliteTransaction transaction, string requestIdentity, IdempotencyRequest request, string canonicalHash, CommandResult<JsonElement> result, CancellationToken cancellationToken)
    {
        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO idempotency_record (idempotency_key, operation_id, canonical_request_hash, actor, state, stable_result, committed_at) VALUES ($key, $operation, $hash, $actor, 'committed', $result, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));";
        record.Parameters.AddWithValue("$key", requestIdentity);
        record.Parameters.AddWithValue("$operation", request.ContractVersion + "\n" + request.OperationId);
        record.Parameters.AddWithValue("$hash", canonicalHash);
        record.Parameters.AddWithValue("$actor", request.Actor);
        record.Parameters.AddWithValue("$result", Serialize(result));
        await record.ExecuteNonQueryAsync(cancellationToken);

        if (request.LogicalEffect is not null)
        {
            await using var effect = connection.CreateCommand();
            effect.Transaction = transaction;
            effect.CommandText = "INSERT INTO logical_effect (logical_identity, effect_type, idempotency_key, committed_at) VALUES ($identity, $type, $key, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));";
            effect.Parameters.AddWithValue("$identity", request.LogicalEffect.Value);
            effect.Parameters.AddWithValue("$type", request.LogicalEffect.EffectType);
            effect.Parameters.AddWithValue("$key", requestIdentity);
            await effect.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public CommandResult<JsonElement> Deserialize(string stableResult)
    {
        using var document = JsonDocument.Parse(stableResult);
        var root = document.RootElement;
        var error = root.TryGetProperty("errorCode", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null ? errorElement.GetString() : null;
        return error is null
            ? CommandResult<JsonElement>.Success(root.GetProperty("value").Clone())
            : CommandResult<JsonElement>.Failure(error);
    }

    private static string Serialize(CommandResult<JsonElement> result) => result.IsSuccess
        ? "{\"value\":" + result.Value!.GetRawText() + ",\"errorCode\":null}"
        : "{\"value\":null,\"errorCode\":\"" + JsonEncodedText.Encode(result.ErrorCode!).ToString() + "\"}";
}
