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

            -- Best-effort backfill from completed_at or a stable placeholder so columns are readable.
            UPDATE import_receipt
            SET created_at = COALESCE(completed_at, '1970-01-01T00:00:00Z'),
                updated_at = COALESCE(completed_at, '1970-01-01T00:00:00Z')
            WHERE created_at IS NULL OR updated_at IS NULL;
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
