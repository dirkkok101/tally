using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Ingest.Storage.Migrations;

namespace Tally.Infrastructure.Ingest.Storage;

public sealed class IngestSchemaMigrator
{
    private const int CurrentVersion = 1;

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userVersion = Convert.ToInt32(
            await IngestDatabase.ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        if (userVersion > CurrentVersion)
        {
            throw new InvalidOperationException("The ingest database schema version is newer than this runtime supports.");
        }

        if (userVersion == CurrentVersion)
        {
            return;
        }

        if (userVersion != 0)
        {
            throw new InvalidOperationException("The ingest database schema version is not supported by this runtime.");
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await new IngestMigrationV001().ApplyAsync(connection, transaction, cancellationToken);
            await IngestDatabase.ExecuteAsync(connection, "PRAGMA user_version = 1;", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
