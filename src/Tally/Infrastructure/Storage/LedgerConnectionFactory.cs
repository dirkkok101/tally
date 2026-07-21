using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage;

public sealed class LedgerConnectionFactory(HostArtifactProtection artifactProtection)
{
    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenAsync(LedgerDb database, int maximumSchemaVersion, CancellationToken cancellationToken)
    {
        artifactProtection.EnsureDataRoot(database.DataRoot);
        artifactProtection.EnsureDataRoot(Path.GetDirectoryName(database.GenerationDirectory)!);
        artifactProtection.EnsureDataRoot(database.GenerationDirectory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
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

            var userVersion = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (userVersion > maximumSchemaVersion)
            {
                throw new InvalidOperationException("The database schema version is newer than this Ledger runtime supports.");
            }

            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
            artifactProtection.ProtectArtifact(database.DatabasePath);
            ProtectSidecars(database.DatabasePath);
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
    private void ProtectSidecars(string databasePath)
    {
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" }.Where(File.Exists))
        {
            artifactProtection.ProtectArtifact(sidecar);
        }
    }
}
