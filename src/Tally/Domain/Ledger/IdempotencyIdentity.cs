using System.Security.Cryptography;
using System.Text.Json;

namespace Tally.Domain.Ledger;

public sealed record LogicalEffectIdentity(string Value, string EffectType);

public sealed class CanonicalRequestHasher
{
    public string Hash(string contractVersion, string operationId, string actor, JsonElement input)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("actor", actor);
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("operationId", operationId);
            writer.WritePropertyName("input");
            WriteCanonical(writer, input);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
