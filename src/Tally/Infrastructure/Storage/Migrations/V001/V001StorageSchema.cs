using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage.Migrations.V001;

public sealed class V001StorageSchema : ILedgerSchemaFragment
{
    public const string FragmentName = "storage";
    public int Version => 1;
    public string Name => FragmentName;

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE store_generation (
                generation_id TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('candidate', 'current', 'retained')),
                verification_fingerprint TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by_version TEXT NOT NULL
            );
            CREATE TABLE artifact_manifest (
                generation_id TEXT NOT NULL REFERENCES store_generation(generation_id),
                artifact_name TEXT NOT NULL,
                checksum TEXT NOT NULL,
                permissions TEXT NOT NULL,
                PRIMARY KEY (generation_id, artifact_name)
            );
            CREATE TABLE idempotency_record (
                idempotency_key TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL,
                canonical_request_hash TEXT NOT NULL,
                actor TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state = 'committed'),
                stable_result TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE logical_effect (
                logical_identity TEXT PRIMARY KEY,
                effect_type TEXT NOT NULL,
                idempotency_key TEXT NOT NULL REFERENCES idempotency_record(idempotency_key),
                committed_at TEXT NOT NULL
            );
            CREATE TABLE migration_metadata (
                version INTEGER NOT NULL,
                fragment_name TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                PRIMARY KEY (version, fragment_name)
            );
            INSERT INTO migration_metadata(version, fragment_name, applied_at)
            VALUES (1, 'storage', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;

        await LedgerConnectionFactory.ExecuteAsync(connection, sql, cancellationToken, transaction);
    }
}
