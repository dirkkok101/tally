using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Recovery;

public static class StatusErrors
{
    public const string InvalidInput = "INGEST-STATUS-INPUT-INVALID";
    public const string BatchNotFound = "INGEST-STATUS-BATCH-NOT-FOUND";
    public const string CursorInvalid = "INGEST-STATUS-CURSOR-INVALID";
    public const string SnapshotNotFound = "INGEST-STATUS-SNAPSHOT-NOT-FOUND";
    public const string SnapshotExpired = "INGEST-STATUS-SNAPSHOT-EXPIRED";
    public const string ContractMismatch = "INGEST-STATUS-CONTRACT-MISMATCH";
    public const string GenerationMismatch = "INGEST-STATUS-GENERATION-MISMATCH";
    public const string SnapshotBusy = "INGEST-STATUS-SNAPSHOT-BUSY";
}

[SupportedOSPlatform("linux")]
public sealed class StatusHandler(StatusStateStore store, TimeProvider? timeProvider = null)
{
    private const int CursorVersion = 1;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<IngestStatusResult>> HandleAsync(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 100
            || (query.BatchId is not null && query.Cursor is not null)
            || string.IsNullOrWhiteSpace(query.BatchId) && query.BatchId is not null)
        {
            return Failure(StatusErrors.InvalidInput);
        }

        try
        {
            if (query.BatchId is not null)
            {
                var detail = await store.DetailAsync(query.BatchId, cancellationToken);
                return detail is null
                    ? Failure(StatusErrors.BatchNotFound)
                    : Success(new(detail, null, null));
            }

            return query.Cursor is null
                ? await FirstPageAsync(query.Limit, cancellationToken)
                : await LaterPageAsync(query.Limit, query.Cursor, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(StatusErrors.SnapshotBusy);
        }
    }

    private async Task<CommandResult<IngestStatusResult>> FirstPageAsync(int pageSize, CancellationToken cancellationToken)
    {
        var page = await store.CreateSnapshotAsync(pageSize, clock.GetUtcNow(), cancellationToken);
        return Success(new(null, page.Items, Cursor(page)));
    }

    private async Task<CommandResult<IngestStatusResult>> LaterPageAsync(
        int pageSize,
        string cursorValue,
        CancellationToken cancellationToken)
    {
        if (!TryDecode(cursorValue, out var cursor)
            || cursor!.PageSize != pageSize)
        {
            return Failure(StatusErrors.CursorInvalid);
        }

        if (!string.Equals(cursor.ContractVersion, StatusStateStore.ContractVersion, StringComparison.Ordinal))
        {
            return Failure(StatusErrors.ContractMismatch);
        }

        var read = await store.ReadSnapshotAsync(cursor, clock.GetUtcNow(), cancellationToken);
        return read.ErrorCode is null
            ? Success(new(null, read.Page!.Items, Cursor(read.Page)))
            : Failure(read.ErrorCode);
    }

    private static string? Cursor(StatusSnapshotPage page)
    {
        if (page.NextOrdinal is null) return null;
        var payload = new IngestStatusCursorPayload(
            CursorVersion,
            StatusStateStore.ContractVersion,
            page.SnapshotId,
            page.NextOrdinal.Value,
            page.PageSize,
            page.StoreGeneration,
            page.ExpiresAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, IngestJsonContext.Default.IngestStatusCursorPayload);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string value, out IngestStatusCursorPayload? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return false;
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            cursor = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), IngestJsonContext.Default.IngestStatusCursorPayload);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        return cursor is not null
            && cursor.CursorVersion == CursorVersion
            && !string.IsNullOrWhiteSpace(cursor.ContractVersion)
            && cursor.SnapshotId is { Length: > 0 and <= 128 }
            && cursor.NextOrdinal >= 1
            && cursor.PageSize is >= 1 and <= 100
            && cursor.StoreGeneration is { Length: > 0 and <= 128 }
            && DateTimeOffset.TryParse(cursor.ExpiresAt, global::System.Globalization.CultureInfo.InvariantCulture, global::System.Globalization.DateTimeStyles.RoundtripKind, out _);
    }

    private static CommandResult<IngestStatusResult> Success(IngestStatusResult result) => CommandResult<IngestStatusResult>.Success(result);
    private static CommandResult<IngestStatusResult> Failure(string errorCode) => CommandResult<IngestStatusResult>.Failure(errorCode);
}
