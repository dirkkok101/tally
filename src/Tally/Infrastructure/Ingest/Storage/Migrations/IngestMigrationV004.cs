using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Ingest.Storage.Migrations;

/// <summary>
/// Durable attempt_count on candidate_receipt (bd-3gib).
/// </summary>
public sealed class IngestMigrationV004
{
    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE candidate_receipt ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0;

            -- Pre-V004 rows that were already attempted must not read back as "never attempted" (0).
            UPDATE candidate_receipt
            SET attempt_count = 1
            WHERE attempted_at IS NOT NULL;
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
