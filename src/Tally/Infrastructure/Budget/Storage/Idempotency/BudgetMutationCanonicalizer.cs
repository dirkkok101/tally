using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tally.Infrastructure.Budget.Storage.Idempotency;

/// <summary>
/// Canonical logical-request and outcome hashing for BUDGET mutations
/// (DD-BUDGET-IDEMPOTENT-MUTATIONS). Produces SHA-256 hex digests only —
/// never retains raw keys or financial response bodies.
/// </summary>
public static class BudgetMutationCanonicalizer
{
    public const string HashSchemaVersion = "budget-mutation-canonical-v1";

    /// <summary>SHA-256 hex digest of the caller-supplied idempotency key (UTF-8).</summary>
    public static string DigestKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return Sha256Hex(Encoding.UTF8.GetBytes(idempotencyKey));
    }

    /// <summary>
    /// Canonical draft-create request hash: fixed field order, category-ID-sorted entries,
    /// actor/reason/selectors included.
    /// </summary>
    public static string HashDraftRequest(BudgetDraftLogicalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Entries);

        var ordered = request.Entries
            .OrderBy(entry => entry.CategoryId, StringComparer.Ordinal)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", HashSchemaVersion);
            writer.WriteString("operationId", request.OperationId);
            writer.WriteString("contractVersion", request.ContractVersion);
            writer.WriteString("actorKind", request.ActorKind);
            writer.WriteString("actorLabel", request.ActorLabel);
            writer.WriteString("actorRunId", request.ActorRunId ?? string.Empty);
            writer.WriteString("reason", request.Reason);
            writer.WritePropertyName("period");
            writer.WriteStartObject();
            writer.WriteNumber("year", request.PeriodYear);
            writer.WriteNumber("month", request.PeriodMonth);
            writer.WriteString("currencyCode", request.CurrencyCode);
            writer.WriteEndObject();
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var entry in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("categoryId", entry.CategoryId);
                writer.WriteNumber("plannedMinorUnits", entry.PlannedMinorUnits);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Sha256Hex(buffer.ToArray());
    }

    /// <summary>
    /// Canonical activate-revision request hash: fixed field order with actor/reason/selector.
    /// </summary>
    public static string HashActivateRequest(BudgetActivateLogicalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", HashSchemaVersion);
            writer.WriteString("operationId", request.OperationId);
            writer.WriteString("contractVersion", request.ContractVersion);
            writer.WriteString("actorKind", request.ActorKind);
            writer.WriteString("actorLabel", request.ActorLabel);
            writer.WriteString("actorRunId", request.ActorRunId ?? string.Empty);
            writer.WriteString("reason", request.Reason);
            writer.WriteString("revisionId", request.RevisionId);
            writer.WriteEndObject();
        }

        return Sha256Hex(buffer.ToArray());
    }

    /// <summary>
    /// Outcome result hash over stable references only (no amounts, names, or response JSON).
    /// </summary>
    public static string HashResult(
        string planId,
        string resultRevisionId,
        string? priorActiveRevisionId,
        IReadOnlyList<string> lifecycleEventIds,
        string revisionPayloadHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRevisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionPayloadHash);
        ArgumentNullException.ThrowIfNull(lifecycleEventIds);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", HashSchemaVersion);
            writer.WriteString("planId", planId);
            writer.WriteString("resultRevisionId", resultRevisionId);
            writer.WriteString("priorActiveRevisionId", priorActiveRevisionId ?? string.Empty);
            writer.WriteString("revisionPayloadHash", revisionPayloadHash);
            writer.WritePropertyName("lifecycleEventIds");
            writer.WriteStartArray();
            foreach (var eventId in lifecycleEventIds)
            {
                writer.WriteStringValue(eventId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Sha256Hex(buffer.ToArray());
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string utf8Text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(utf8Text));

    public static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Logical draft-create input fields that participate in the request hash.</summary>
public sealed record BudgetDraftLogicalRequest(
    string ContractVersion,
    string OperationId,
    string ActorKind,
    string ActorLabel,
    string? ActorRunId,
    string Reason,
    int PeriodYear,
    int PeriodMonth,
    string CurrencyCode,
    IReadOnlyList<BudgetCanonicalEntry> Entries);

/// <summary>Logical activate input fields that participate in the request hash.</summary>
public sealed record BudgetActivateLogicalRequest(
    string ContractVersion,
    string OperationId,
    string ActorKind,
    string ActorLabel,
    string? ActorRunId,
    string Reason,
    string RevisionId);

public sealed record BudgetCanonicalEntry(string CategoryId, long PlannedMinorUnits);
