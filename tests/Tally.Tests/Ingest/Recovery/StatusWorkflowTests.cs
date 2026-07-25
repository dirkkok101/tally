using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Recovery;
using Tally.Infrastructure.Ingest.Storage;
using Xunit;

namespace Tally.Tests.Ingest.Recovery;

[SupportedOSPlatform("linux")]
public sealed class StatusWorkflowTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-status-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Empty_store_returns_an_empty_snapshot_page()
    {
        var result = await HandlerAsync(new IngestStatusInput());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items!);
        Assert.Null(result.Value.NextCursor);
        Assert.Null(result.Value.Detail);
    }

    [Fact]
    public async Task Unknown_batch_returns_a_stable_not_found_error()
    {
        var result = await HandlerAsync(new IngestStatusInput("missing"));

        Assert.Equal(StatusErrors.BatchNotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Limit_outside_one_through_one_hundred_fails_closed(int limit)
    {
        var result = await HandlerAsync(new IngestStatusInput(Limit: limit));

        Assert.Equal(StatusErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Batch_id_and_cursor_are_mutually_exclusive()
    {
        var result = await HandlerAsync(new IngestStatusInput("batch-1", Cursor: "opaque"));

        Assert.Equal(StatusErrors.InvalidInput, result.ErrorCode);
    }

    [Theory]
    [InlineData(BatchStatus.Previewed, "ingest.inspect,ingest.approve,ingest.abandon")]
    [InlineData(BatchStatus.Approved, "ingest.inspect,ingest.commit,ingest.abandon")]
    [InlineData(BatchStatus.Committing, "")]
    [InlineData(BatchStatus.Interrupted, "ingest.resume,ingest.abandon")]
    [InlineData(BatchStatus.Completed, "ingest.cleanup")]
    [InlineData(BatchStatus.Abandoned, "ingest.cleanup")]
    [InlineData(BatchStatus.Cleaned, "")]
    public async Task Detail_derives_only_lifecycle_permitted_next_operations(BatchStatus status, string expected)
    {
        await InsertBatchAsync("batch-1", status, "2026-07-25T10:00:00Z");
        if (status == BatchStatus.Previewed)
        {
            await InsertManifestAsync("batch-1", committable: true);
        }

        var result = await HandlerAsync(new IngestStatusInput("batch-1"));

        Assert.Equal(expected, string.Join(',', result.Value!.Detail!.Summary.NextAllowedOperations));
    }

    [Fact]
    public async Task Noncommittable_preview_cannot_be_approved()
    {
        await InsertBatchAsync("batch-1", BatchStatus.Previewed, "2026-07-25T10:00:00Z");
        await InsertManifestAsync("batch-1", committable: false);

        var result = await HandlerAsync(new IngestStatusInput("batch-1"));

        Assert.Equal([IngestOperationIds.Inspect, IngestOperationIds.Abandon], result.Value!.Detail!.Summary.NextAllowedOperations);
    }

    [Fact]
    public async Task Detail_projects_latest_manifest_approval_counts_receipt_frontier_and_artifacts()
    {
        await InsertBatchAsync("batch-1", BatchStatus.Interrupted, "2026-07-25T10:00:00Z");
        await InsertManifestAsync("batch-1", committable: true);
        await ExecuteAsync("INSERT INTO manifest_approval VALUES ('approval-1', 'revision-batch-1', 'digest', 'owner', 'owner', '2026-07-25T10:01:00Z', 1);");
        await ExecuteAsync("INSERT INTO source_record_outcome VALUES ('revision-batch-1', 'record-1', 0, 0, 'accepted', 'candidate-1', NULL), ('revision-batch-1', 'record-2', 1, 1, 'duplicate', NULL, 'prior-1'), ('revision-batch-1', 'record-3', 2, 2, 'excluded', NULL, NULL), ('revision-batch-1', 'record-4', 3, 3, 'blocked', NULL, NULL);");
        await InsertCandidatesAsync();
        await InsertReceiptAsync();

        var result = await HandlerAsync(new IngestStatusInput("batch-1"));
        var detail = result.Value!.Detail!;

        Assert.Equal("revision-batch-1", detail.ManifestRevisionId);
        Assert.True(detail.Approved);
        Assert.Equal(ImportReceiptStatus.Interrupted, detail.ReceiptStatus);
        Assert.Equal(new IngestOutcomeCounts(1, 1, 1, 1), detail.Summary.OutcomeCounts);
        Assert.Equal(new IngestOutcomeCounts(1, 1, 0, 2), detail.TerminalCounts);
        Assert.Equal(["candidate-1", "candidate-6", "candidate-7"], detail.UnresolvedFrontier);
        Assert.Equal([ArtifactKind.Manifest, ArtifactKind.Candidates, ArtifactKind.Receipt, ArtifactKind.Metadata], detail.RetainedArtifactKinds);
    }

    [Fact]
    public async Task Detail_returns_the_latest_complete_durable_error_without_inference()
    {
        await InsertBatchAsync("batch-1", BatchStatus.Interrupted, "2026-07-25T10:00:00Z");
        var first = new IngestError("INGEST-001", IngestErrorCategory.Validation, "Correct the input.", "batch-1", null, MutationPossibility.None, "preview_blocked", IngestRetryAction.CorrectSource, "accountId");
        var latest = new IngestError("INGEST-002", IngestErrorCategory.Ledger, "Resume the batch.", "batch-1", "candidate-safe", MutationPossibility.Possible, "commit_interrupted", IngestRetryAction.Resume, "candidate");
        await AppendErrorAsync("event-1", first, "2026-07-25T10:00:01Z");
        await AppendErrorAsync("event-2", latest, "2026-07-25T10:00:02Z");

        var result = await HandlerAsync(new IngestStatusInput("batch-1"));

        Assert.Equal(latest, result.Value!.Detail!.LastStableError);
    }

    [Fact]
    public async Task Candidate_receipt_error_code_is_never_promoted_to_last_stable_error()
    {
        await InsertBatchAsync("batch-1", BatchStatus.Interrupted, "2026-07-25T10:00:00Z");
        await InsertManifestAsync("batch-1", true);
        await ExecuteAsync("INSERT INTO import_candidate VALUES ('candidate-1', 'revision-batch-1', 'record-1', '{}', '{}', 'key-1', 0);");
        await ExecuteAsync("INSERT INTO import_receipt VALUES ('receipt-1', 'batch-1', 2, '{}', NULL);");
        await ExecuteAsync("INSERT INTO candidate_receipt VALUES ('receipt-1', 'candidate-1', 6, NULL, 'PARTIAL-CODE', NULL, NULL);");

        var result = await HandlerAsync(new IngestStatusInput("batch-1"));

        Assert.Null(result.Value!.Detail!.LastStableError);
    }

    [Fact]
    public async Task First_page_materializes_all_batches_in_stable_order()
    {
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T10:00:00Z");
        await InsertBatchAsync("batch-c", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T11:00:00Z");

        var result = await HandlerAsync(new IngestStatusInput(Limit: 2));

        Assert.Equal(["batch-a", "batch-c"], result.Value!.Items!.Select(item => item.BatchId));
        Assert.NotNull(result.Value.NextCursor);
        Assert.Equal(3L, await ScalarLongAsync("SELECT total_count FROM status_snapshot;"));
        Assert.Equal(3L, await ScalarLongAsync("SELECT COUNT(*) FROM status_snapshot_item;"));
    }

    [Fact]
    public async Task Later_pages_return_each_frozen_member_once()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        await InsertBatchAsync("batch-c", BatchStatus.Previewed, "2026-07-25T10:00:00Z");

        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));
        var second = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: first.Value!.NextCursor));
        var third = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: second.Value!.NextCursor));

        Assert.Equal(["batch-a", "batch-b", "batch-c"], first.Value.Items!.Concat(second.Value!.Items!).Concat(third.Value!.Items!).Select(item => item.BatchId));
        Assert.Null(third.Value.NextCursor);
    }

    [Fact]
    public async Task Concurrent_updates_do_not_enter_or_reorder_a_frozen_snapshot()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));
        await ExecuteAsync("UPDATE ingest_batch SET updated_at = '2026-07-25T13:00:00Z' WHERE batch_id = 'batch-b';");
        await InsertBatchAsync("batch-new", BatchStatus.Previewed, "2026-07-25T14:00:00Z");

        var second = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: first.Value!.NextCursor));

        Assert.Equal("batch-b", Assert.Single(second.Value!.Items!).BatchId);
        Assert.Equal("2026-07-25T11:00:00Z", second.Value.Items![0].UpdatedAt);
        Assert.Null(second.Value.NextCursor);
    }

    [Fact]
    public async Task Malformed_cursor_fails_closed()
    {
        var result = await HandlerAsync(new IngestStatusInput(Cursor: "not-base64url!"));

        Assert.Equal(StatusErrors.CursorInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_snapshot_cursor_fails_closed()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));
        var cursor = Cursor(Decode(first.Value!.NextCursor!) with { SnapshotId = "unknown" });

        var result = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: cursor));

        Assert.Equal(StatusErrors.SnapshotNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Expired_cursor_fails_closed()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));
        time.Advance(TimeSpan.FromMinutes(16));

        var result = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: first.Value!.NextCursor));

        Assert.Equal(StatusErrors.SnapshotExpired, result.ErrorCode);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("generation")]
    [InlineData("expiry")]
    [InlineData("ordinal")]
    public async Task Cursor_header_or_range_tampering_fails_closed(string field)
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));
        var payload = Decode(first.Value!.NextCursor!);
        payload = field switch
        {
            "contract" => payload with { ContractVersion = "2.0" },
            "generation" => payload with { StoreGeneration = "changed" },
            "expiry" => payload with { ExpiresAt = "2026-07-25T12:14:59Z" },
            "ordinal" => payload with { NextOrdinal = 99 },
            _ => payload
        };

        var result = await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: Cursor(payload)));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Cursor_page_size_must_match_the_request_and_snapshot_contract()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));

        var result = await HandlerAsync(new IngestStatusInput(Limit: 2, Cursor: first.Value!.NextCursor));

        Assert.Equal(StatusErrors.CursorInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Cursor_is_base64url_metadata_only()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));

        Assert.Matches("^[A-Za-z0-9_-]+$", first.Value!.NextCursor!);
        var json = Encoding.UTF8.GetString(Base64UrlDecode(first.Value.NextCursor!));
        Assert.DoesNotContain("sourcePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_mutates_only_ephemeral_snapshot_state()
    {
        await InsertBatchAsync("batch-1", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        var before = await DurableCountsAsync();

        await HandlerAsync(new IngestStatusInput());

        Assert.Equal(before, await DurableCountsAsync());
        Assert.Equal(1L, await ScalarLongAsync("SELECT COUNT(*) FROM status_snapshot;"));
    }

    [Fact]
    public async Task Later_page_does_not_create_another_snapshot()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        var first = await HandlerAsync(new IngestStatusInput(Limit: 1));

        await HandlerAsync(new IngestStatusInput(Limit: 1, Cursor: first.Value!.NextCursor));

        Assert.Equal(1L, await ScalarLongAsync("SELECT COUNT(*) FROM status_snapshot;"));
    }

    [Fact]
    public async Task Expired_snapshots_are_removed_only_during_first_page_creation()
    {
        await InsertBatchAsync("batch-a", BatchStatus.Previewed, "2026-07-25T12:00:00Z");
        await InsertBatchAsync("batch-b", BatchStatus.Previewed, "2026-07-25T11:00:00Z");
        await HandlerAsync(new IngestStatusInput(Limit: 1));
        time.Advance(TimeSpan.FromMinutes(16));

        await HandlerAsync(new IngestStatusInput(Limit: 1));

        Assert.Equal(1L, await ScalarLongAsync("SELECT COUNT(*) FROM status_snapshot;"));
    }

    [Fact]
    public void Status_operation_module_binds_only_ingest_status_without_global_registration()
    {
        var module = new StatusOperationModule(null!);

        var descriptor = Assert.Single(module.Descriptors);
        Assert.Equal(IngestOperationIds.Status, descriptor.OperationId);
        Assert.Equal("query", descriptor.Kind);
        Assert.False(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(IngestStatusInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(IngestStatusResult), descriptor.ResultTypeInfo.Type);
    }

    [Fact]
    public async Task Status_operation_module_rejects_invalid_json_without_throwing()
    {
        var module = new StatusOperationModule(await CreateHandlerAsync());
        using var document = JsonDocument.Parse("{\"limit\":\"invalid\"}");

        var result = await module.HandleAsync(IngestOperationIds.Status, new OperationRequest(document.RootElement.Clone(), null, null), CancellationToken.None);

        Assert.Equal(StatusErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public void Status_dependencies_expose_no_source_or_ledger_storage_boundary()
    {
        var types = new[] { typeof(StatusQuery), typeof(StatusHandler), typeof(StatusStateStore), typeof(StatusOperationModule) };
        var dependencies = types.SelectMany(type => type.GetConstructors()).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType.FullName).ToArray();

        Assert.DoesNotContain(dependencies, name => name?.Contains("Pdf", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencies, name => name?.StartsWith("Tally.Infrastructure.Storage", StringComparison.Ordinal) == true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<CommandResult<IngestStatusResult>> HandlerAsync(IngestStatusInput input) =>
        await (await CreateHandlerAsync()).HandleAsync(new StatusQuery(input.BatchId, input.Limit, input.Cursor), CancellationToken.None);

    private async Task<StatusHandler> CreateHandlerAsync()
    {
        var database = new IngestDatabase(root, new IngestArtifactProtection());
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        return new StatusHandler(new StatusStateStore(database, new BatchErrorEventStore()), time);
    }

    private async Task InsertBatchAsync(string batchId, BatchStatus status, string updatedAt)
    {
        await ExecuteAsync("""
            INSERT INTO ingest_batch (
                batch_id, source_fingerprint, selected_account_id, adapter_identity,
                ledger_contract_version, manifest_schema_version, period_start, period_end,
                status, created_at, updated_at)
            VALUES ($batchId, 'fingerprint', 'account-safe', 'layout-a@1', '1.0', '1.0', NULL, NULL, $status, '2026-07-25T09:00:00Z', $updatedAt);
            """, ("$batchId", batchId), ("$status", (int)status), ("$updatedAt", updatedAt));
    }

    private Task InsertManifestAsync(string batchId, bool committable) => ExecuteAsync(
        "INSERT INTO manifest_revision VALUES ($revisionId, $batchId, 1, 'digest', $committable, '2026-07-25T10:00:00Z');",
        ("$revisionId", $"revision-{batchId}"), ("$batchId", batchId), ("$committable", committable ? 1 : 0));

    private Task InsertCandidatesAsync() => ExecuteAsync("""
        INSERT INTO import_candidate VALUES
            ('candidate-1', 'revision-batch-1', 'record-1', '{}', '{}', 'key-1', 0),
            ('candidate-2', 'revision-batch-1', 'record-2', '{}', '{}', 'key-2', 0),
            ('candidate-3', 'revision-batch-1', 'record-3', '{}', '{}', 'key-3', 0),
            ('candidate-4', 'revision-batch-1', 'record-4', '{}', '{}', 'key-4', 0),
            ('candidate-5', 'revision-batch-1', 'record-5', '{}', '{}', 'key-5', 0),
            ('candidate-6', 'revision-batch-1', 'record-6', '{}', '{}', 'key-6', 0),
            ('candidate-7', 'revision-batch-1', 'record-7', '{}', '{}', 'key-7', 0);
        """);

    private Task InsertReceiptAsync() => ExecuteAsync("""
        INSERT INTO import_receipt VALUES ('receipt-1', 'batch-1', 2, '{}', NULL);
        INSERT INTO candidate_receipt VALUES
            ('receipt-1', 'candidate-1', 0, NULL, NULL, NULL, NULL),
            ('receipt-1', 'candidate-2', 2, 'ledger-safe-2', NULL, '2026-07-25T10:02:00Z', '2026-07-25T10:02:01Z'),
            ('receipt-1', 'candidate-3', 3, 'ledger-safe-3', NULL, '2026-07-25T10:03:00Z', '2026-07-25T10:03:01Z'),
            ('receipt-1', 'candidate-4', 4, NULL, 'conflict', '2026-07-25T10:04:00Z', '2026-07-25T10:04:01Z'),
            ('receipt-1', 'candidate-5', 5, NULL, 'rejected', '2026-07-25T10:05:00Z', '2026-07-25T10:05:01Z'),
            ('receipt-1', 'candidate-6', 1, NULL, NULL, '2026-07-25T10:06:00Z', NULL),
            ('receipt-1', 'candidate-7', 6, NULL, NULL, '2026-07-25T10:07:00Z', NULL);
        """);

    private async Task AppendErrorAsync(string eventId, IngestError error, string recordedAt)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await new BatchErrorEventStore().AppendAsync(connection, transaction, eventId, error, recordedAt, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await new IngestDatabase(root, new IngestArtifactProtection()).OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private async Task<long> ScalarLongAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long[]> DurableCountsAsync()
    {
        string[] tables = ["ingest_batch", "manifest_revision", "source_record_outcome", "import_candidate", "reconciliation_control", "manifest_approval", "import_receipt", "candidate_receipt", "batch_error_event", "ingest_store_metadata"];
        var counts = new List<long>();
        foreach (var table in tables) counts.Add(await ScalarLongAsync($"SELECT COUNT(*) FROM {table};"));
        return counts.ToArray();
    }

    private static string Cursor(IngestStatusCursorPayload payload) =>
        Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, IngestJsonContext.Default.IngestStatusCursorPayload));

    private static IngestStatusCursorPayload Decode(string cursor) =>
        JsonSerializer.Deserialize(Base64UrlDecode(cursor), IngestJsonContext.Default.IngestStatusCursorPayload)!;

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return Convert.FromBase64String(encoded);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
