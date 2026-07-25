using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Ingest.Storage;

public sealed class IngestDatabase(string dataRoot, IngestArtifactProtection artifactProtection)
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);
    public string IngestDirectory => Path.Combine(DataRoot, "ingest");
    public string DatabasePath => Path.Combine(IngestDirectory, "ingest.db");

    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        artifactProtection.EnsureOwnerOnlyDirectory(DataRoot);
        artifactProtection.EnsureOwnerOnlyDirectory(IngestDirectory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
            ProtectPersistedArtifacts();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    [SupportedOSPlatform("linux")]
    private void ProtectPersistedArtifacts()
    {
        foreach (var artifact in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal", DatabasePath + ".lock", DatabasePath + ".atomic" }.Where(File.Exists))
        {
            artifactProtection.EnsureOwnerOnly(artifact);
        }
    }
}
