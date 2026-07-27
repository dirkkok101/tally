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
            """;

        await IngestDatabase.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
