using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Canonical request fingerprint and replay contract for every CLASSIFY mutation
/// (DM-CLASSIFY-STATE-STORE / TASK-CLASSIFY-RULEBOOK-STATE-FOUNDATION).
/// </summary>
public sealed class ClassifyOperationIdempotencyStore
{
    /// <summary>
    /// SHA-256 over canonical UTF-8 JSON of operation_id, contract_version, actor kind/label/run ID,
    /// and normalized request input. Object keys are sorted; nulls are explicit; the idempotency key
    /// and volatile timestamps are excluded from the compared tuple.
    /// </summary>
    public static string ComputeRequestFingerprint(
        string operationId,
        string contractVersion,
        string actorKind,
        string actorLabel,
        string? actorRunId,
        JsonElement requestInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorLabel);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            // Sorted key order: actorKind, actorLabel, actorRunId, contractVersion, operationId, request
            writer.WriteString("actorKind", actorKind);
            writer.WriteString("actorLabel", actorLabel);
            if (actorRunId is null)
            {
                writer.WriteNull("actorRunId");
            }
            else
            {
                writer.WriteString("actorRunId", actorRunId);
            }

            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("operationId", operationId);
            writer.WritePropertyName("request");
            WriteCanonicalElement(writer, requestInput);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public async Task<ClassifyOperationIdempotencyRow?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT idempotency_key, operation_id, contract_version, request_fingerprint, terminal_result, created_at
            FROM operation_idempotency
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapIdempotency(reader) : null;
    }

    /// <summary>
    /// Classifies an existing record as replay (exact fingerprint) or conflict (key reused with different tuple).
    /// </summary>
    public ClassifyIdempotencyLookup Resolve(
        ClassifyOperationIdempotencyRow? existing,
        string requestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        if (existing is null)
        {
            return ClassifyIdempotencyLookup.Miss;
        }

        return string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
            ? ClassifyIdempotencyLookup.Replay(existing)
            : ClassifyIdempotencyLookup.Conflict(existing);
    }

    public async Task CommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyOperationIdempotencyRow record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RequestFingerprint.Length != 64)
        {
            throw new InvalidOperationException("Request fingerprint must be a 64-character hex SHA-256 digest.");
        }

        if (string.IsNullOrWhiteSpace(record.TerminalResult))
        {
            throw new InvalidOperationException("Terminal result must be a non-empty serialized process result.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_idempotency (
                idempotency_key, operation_id, contract_version, request_fingerprint, terminal_result, created_at
            ) VALUES (
                $idempotency_key, $operation_id, $contract_version, $request_fingerprint, $terminal_result, $created_at
            );
            """;
        command.Parameters.AddWithValue("$idempotency_key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$operation_id", record.OperationId);
        command.Parameters.AddWithValue("$contract_version", record.ContractVersion);
        command.Parameters.AddWithValue("$request_fingerprint", record.RequestFingerprint);
        command.Parameters.AddWithValue("$terminal_result", record.TerminalResult);
        command.Parameters.AddWithValue("$created_at", record.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue))
                {
                    writer.WriteNumberValue(longValue);
                }
                else if (element.TryGetDouble(out var doubleValue))
                {
                    writer.WriteNumberValue(doubleValue);
                }
                else
                {
                    writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new NotSupportedException("Unsupported JSON value kind for canonical fingerprinting.");
        }
    }
}

public enum ClassifyIdempotencyDisposition
{
    Miss,
    Replay,
    Conflict
}

public sealed record ClassifyIdempotencyLookup(
    ClassifyIdempotencyDisposition Disposition,
    ClassifyOperationIdempotencyRow? Record)
{
    public static ClassifyIdempotencyLookup Miss { get; } = new(ClassifyIdempotencyDisposition.Miss, null);

    public static ClassifyIdempotencyLookup Replay(ClassifyOperationIdempotencyRow record) =>
        new(ClassifyIdempotencyDisposition.Replay, record);

    public static ClassifyIdempotencyLookup Conflict(ClassifyOperationIdempotencyRow record) =>
        new(ClassifyIdempotencyDisposition.Conflict, record);
}
