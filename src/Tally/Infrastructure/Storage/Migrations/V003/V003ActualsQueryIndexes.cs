using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V003;

public sealed class V003ActualsQueryIndexes : ILedgerSchemaFragment
{
    public const string FragmentName = "actuals_query_indexes";
    public int Version => 3;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE INDEX ix_category_allocation_event_transaction
                ON category_allocation_event(transaction_id);
            CREATE INDEX ix_pool_assignment_event_transaction
                ON pool_assignment_event(transaction_id);
            CREATE INDEX ix_transaction_attribution_event_transaction
                ON transaction_attribution_event(transaction_id);

            CREATE TABLE query_snapshot_payload (
                snapshot_id TEXT PRIMARY KEY REFERENCES query_snapshot(snapshot_id) ON DELETE CASCADE ON UPDATE RESTRICT,
                total_count INTEGER NOT NULL CHECK (typeof(total_count) = 'integer' AND total_count >= 0),
                items_json BLOB NOT NULL CHECK (
                    typeof(items_json) = 'blob'
                    AND substr(CAST(items_json AS TEXT), 1, 1) = '['
                    AND json_array_length(items_json) = total_count)
            );
            CREATE TRIGGER query_snapshot_payload_is_immutable_before_update
            BEFORE UPDATE ON query_snapshot_payload
            BEGIN SELECT RAISE(ABORT, 'query snapshot payloads are immutable'); END;

            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (3, 'actuals_query_indexes', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
