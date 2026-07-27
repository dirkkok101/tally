using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Ingest.Storage.Migrations;

/// <summary>
/// Adds durable created_at/updated_at provenance on import_receipt (bd-2vft).
/// </summary>
public sealed class IngestMigrationV003
{
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE import_receipt ADD COLUMN created_at TEXT;
            ALTER TABLE import_receipt ADD COLUMN updated_at TEXT;

            -- Backfill from real provenance: the owning batch's timestamps (FK guarantees the row),
            -- preferring completed_at as the last known transition for updated_at.
            UPDATE import_receipt
            SET created_at = COALESCE(
                    (SELECT b.created_at FROM ingest_batch b WHERE b.batch_id = import_receipt.batch_id),
                    completed_at,
                    '1970-01-01T00:00:00Z'),
                updated_at = COALESCE(
                    completed_at,
                    (SELECT b.updated_at FROM ingest_batch b WHERE b.batch_id = import_receipt.batch_id),
                    '1970-01-01T00:00:00Z');
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
