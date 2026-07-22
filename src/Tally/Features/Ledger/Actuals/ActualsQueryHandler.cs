using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;
using Tally.Infrastructure.Storage.Actuals;

namespace Tally.Features.Ledger.Actuals;

public sealed class ActualsQueryHandler(QuerySnapshotStore store)
{
    private const int CursorVersion = 1;
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 500;

    public async Task<CommandResult<JsonElement>> HandleAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Cursor is null
            ? await FirstPageAsync(input, cancellationToken)
            : await LaterPageAsync(input, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> FirstPageAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        var pageSize = input.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize || !TryFilter(input.Filter ?? new(), out var filter))
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        var filterHash = FilterHash(filter);
        try
        {
            var page = await store.CreateAsync(filter, filterHash, pageSize, DateTimeOffset.UtcNow, cancellationToken);
            return Success(WithCursor(page));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }
    }

    private async Task<CommandResult<JsonElement>> LaterPageAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        if (input.Filter is not null || input.PageSize is not null)
        {
            return Failure(ActualsErrors.CursorFilterMismatch);
        }
        if (!TryDecode(input.Cursor!, out var cursor, out var error)) return Failure(error!);

        SnapshotReadResult read;
        try
        {
            read = await store.ReadAsync(cursor!, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }

        return read.IsSuccess ? Success(WithCursor(read.Page!)) : Failure(read.ErrorCode!);
    }

    private static ActualsQueryResult WithCursor(SnapshotPage page)
    {
        if (page.NextOrdinal is null) return page.Result;
        var cursor = new ActualsCursorPayload(
            CursorVersion,
            QuerySnapshotStore.ContractVersion,
            page.Result.SnapshotId,
            page.NextOrdinal.Value,
            page.PageSize,
            page.FilterHash,
            page.GenerationFingerprint,
            page.HierarchyFingerprint,
            page.Result.ExpiresAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, ActualsJsonContext.Default.ActualsCursorPayload);
        return page.Result with { Cursor = Encode(bytes) };
    }

    private static bool TryDecode(string value, out ActualsCursorPayload? cursor, out string? error)
    {
        cursor = null;
        error = ActualsErrors.CursorInvalid;
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            cursor = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), ActualsJsonContext.Default.ActualsCursorPayload);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (cursor is null
            || cursor.CursorVersion != CursorVersion
            || string.IsNullOrWhiteSpace(cursor.ContractVersion)
            || !LedgerId.TryParse(cursor.SnapshotId, out _, out _)
            || cursor.NextOrdinal < 1
            || cursor.PageSize is < 1 or > MaximumPageSize
            || string.IsNullOrWhiteSpace(cursor.FilterHash)
            || string.IsNullOrWhiteSpace(cursor.GenerationFingerprint)
            || string.IsNullOrWhiteSpace(cursor.CategoryHierarchyFingerprint)
            || string.IsNullOrWhiteSpace(cursor.ExpiresAt))
        {
            return false;
        }

        if (!string.Equals(cursor.ContractVersion, QuerySnapshotStore.ContractVersion, StringComparison.Ordinal))
        {
            error = ActualsErrors.ContractMismatch;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryFilter(ActualsFilterInput input, out ActualsFilter filter)
    {
        filter = null!;
        if (!Enum.IsDefined(input.CategoryScope)
            || !Enum.IsDefined(input.GroupBy)
            || !TryDate(input.EffectiveFrom, out var effectiveFrom)
            || !TryDate(input.EffectiveTo, out var effectiveTo))
        {
            return false;
        }

        filter = new(
            input.AccountIds,
            effectiveFrom,
            effectiveTo,
            input.CategoryIds,
            (ActualsCategoryScope)(int)input.CategoryScope,
            input.CategorizationStates,
            input.PoolIds,
            input.PoolStates,
            input.InstrumentIds,
            input.InstrumentStates,
            input.CardholderIds,
            input.CardholderStates,
            input.EvidenceKinds,
            input.ReconciliationStates,
            input.RelationshipStates?.Select(value => (ActualsRelationshipState)(int)value).ToArray(),
            input.LifecycleStates,
            (ActualsGroupKind)(int)input.GroupBy);
        return filter.IsValid();
    }

    private static bool TryDate(string? value, out EffectiveDate? result)
    {
        result = null;
        if (value is null) return true;
        if (!EffectiveDate.TryParse(value, out var parsed, out _)) return false;
        result = parsed;
        return true;
    }

    private static string FilterHash(ActualsFilter filter)
    {
        var canonical = new StringBuilder();
        Add(canonical, "accounts", filter.AccountIds);
        Add(canonical, "from", filter.EffectiveFrom?.ToString());
        Add(canonical, "to", filter.EffectiveTo?.ToString());
        Add(canonical, "categories", filter.CategoryIds);
        Add(canonical, "categoryScope", (int)filter.CategoryScope);
        Add(canonical, "categorizationStates", filter.CategorizationStates?.Select(value => (int)value));
        Add(canonical, "pools", filter.PoolIds);
        Add(canonical, "poolStates", filter.PoolStates?.Select(value => (int)value));
        Add(canonical, "instruments", filter.InstrumentIds);
        Add(canonical, "instrumentStates", filter.InstrumentStates?.Select(value => (int)value));
        Add(canonical, "cardholders", filter.CardholderIds);
        Add(canonical, "cardholderStates", filter.CardholderStates?.Select(value => (int)value));
        Add(canonical, "evidenceKinds", filter.EvidenceKinds?.Select(value => (int)value));
        Add(canonical, "reconciliationStates", filter.ReconciliationStates?.Select(value => (int)value));
        Add(canonical, "relationshipStates", filter.RelationshipStates?.Select(value => (int)value));
        Add(canonical, "lifecycleStates", filter.LifecycleStates?.Select(value => (int)value));
        Add(canonical, "groupBy", (int)filter.GroupBy);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Add(StringBuilder target, string name, string? value)
    {
        target.Append(name).Append('=');
        if (value is null) target.Append("null");
        else target.Append(value.Length).Append(':').Append(value);
        target.Append('|');
    }

    private static void Add(StringBuilder target, string name, int value) => Add(target, name, value.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

    private static void Add(StringBuilder target, string name, IEnumerable<string>? values) =>
        Add(target, name, values is null ? null : string.Join(',', values.Order(StringComparer.Ordinal)));

    private static void Add(StringBuilder target, string name, IEnumerable<int>? values) =>
        Add(target, name, values is null ? null : string.Join(',', values.Order().Select(value => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture))));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CommandResult<JsonElement> Success(ActualsQueryResult result) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(result, ActualsJsonContext.Default.ActualsQueryResult));

    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
