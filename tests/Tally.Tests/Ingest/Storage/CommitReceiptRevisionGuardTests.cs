using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Common;
using Tally.Infrastructure.Ingest.Storage;
using Xunit;

namespace Tally.Tests.Ingest.Storage;

// bd-zw4w: EnsureReceiptAsync selects receipts per batch; these tests pin the invariants that make
// that safe — approval is rejected outside Previewed/Approved, and a receipt persisted for another
// manifest revision is refused instead of being silently reused.
[SupportedOSPlatform("linux")]
public sealed class CommitReceiptRevisionGuardTests : IAsyncLifetime
{
    private const string Timestamp = "2026-07-28T00:00:00Z";
    private const int BatchCompleted = 4;
    private const int ReceiptCommitting = 1;
    private const int ReceiptCompleted = 3;

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-receipt-guard-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ensure_receipt_returns_the_completed_receipt_for_its_own_revision()
    {
        await SeedCompletedBatchAsync("revision-a");

        var header = await CreateCommitStore().EnsureReceiptAsync("batch-1", "revision-a", Timestamp, CancellationToken.None);

        Assert.Equal("receipt-1", header.ReceiptId);
        Assert.Equal(Contracts.Ingest.ImportReceiptStatus.Completed, header.Status);
    }

    [Fact]
    public async Task Ensure_receipt_refuses_a_receipt_persisted_for_a_different_revision()
    {
        await SeedCompletedBatchAsync("revision-a");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateCommitStore().EnsureReceiptAsync("batch-1", "revision-b", Timestamp, CancellationToken.None));

        Assert.Contains("revision-a", exception.Message, StringComparison.Ordinal);
        Assert.Contains("revision-b", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_receipt_tolerates_identity_less_summaries_from_fresh_committing_receipts()
    {
        await using (var connection = await MigratedAsync())
        {
            await SeedBatchAndRevisionAsync(connection, "revision-a", batchStatus: 2);
            await ExecuteAsync(connection, $$"""
                INSERT INTO import_receipt (receipt_id, batch_id, status, summary_json, completed_at, created_at, updated_at)
                VALUES ('receipt-1', 'batch-1', {{ReceiptCommitting}}, '{}', NULL, '{{Timestamp}}', '{{Timestamp}}');
                """);
        }

        var header = await CreateCommitStore().EnsureReceiptAsync("batch-1", "revision-a", Timestamp, CancellationToken.None);

        Assert.Equal("receipt-1", header.ReceiptId);
        Assert.Equal(Contracts.Ingest.ImportReceiptStatus.Committing, header.Status);
    }

    [Fact]
    public async Task Approve_rejects_a_completed_batch_so_no_later_revision_can_reach_commit()
    {
        await SeedCompletedBatchAsync("revision-a");

        var result = await new ReviewStateStore(CreateDatabase()).ApproveAsync(
            "batch-1",
            "revision-a",
            "digest",
            new SafeActor("owner", "owner"),
            Timestamp,
            CancellationToken.None);

        Assert.Equal("reject", result);
    }

    private async Task SeedCompletedBatchAsync(string revisionId)
    {
        await using var connection = await MigratedAsync();
        await SeedBatchAndRevisionAsync(connection, revisionId, BatchCompleted);
        var summary = $$"""{"manifestRevisionId":"{{revisionId}}"}""";
        await ExecuteAsync(connection, $"""
            INSERT INTO import_receipt (receipt_id, batch_id, status, summary_json, completed_at, created_at, updated_at)
            VALUES ('receipt-1', 'batch-1', {ReceiptCompleted}, '{summary}', '{Timestamp}', '{Timestamp}', '{Timestamp}');
            """);
    }

    private static async Task SeedBatchAndRevisionAsync(SqliteConnection connection, string revisionId, int batchStatus)
    {
        await ExecuteAsync(connection, $"""
            INSERT INTO ingest_batch (batch_id, source_fingerprint, selected_account_id, adapter_identity,
                ledger_contract_version, manifest_schema_version, period_start, period_end, status, created_at, updated_at)
            VALUES ('batch-1', 'fingerprint', 'account', 'adapter', '1.0', '1', NULL, NULL, {batchStatus}, '{Timestamp}', '{Timestamp}');
            """);
        await ExecuteAsync(connection, $"""
            INSERT INTO manifest_revision (manifest_revision_id, batch_id, revision_number, canonical_digest, committable, created_at)
            VALUES ('{revisionId}', 'batch-1', 1, 'digest', 1, '{Timestamp}');
            """);
    }

    private IngestDatabase CreateDatabase() => new(root, new IngestArtifactProtection());

    private CommitStateStore CreateCommitStore() => new(CreateDatabase(), new BatchErrorEventStore());

    private async Task<SqliteConnection> MigratedAsync()
    {
        var connection = await CreateDatabase().OpenAsync(CancellationToken.None);
        await new IngestSchemaMigrator().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
