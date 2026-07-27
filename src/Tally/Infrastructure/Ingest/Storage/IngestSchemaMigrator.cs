using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Ingest.Storage.Migrations;

namespace Tally.Infrastructure.Ingest.Storage;

public sealed class IngestSchemaMigrator
{
    private const int CurrentVersion = 4;

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

        while (userVersion < CurrentVersion)
        {
            var targetVersion = userVersion + 1;
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                switch (targetVersion)
                {
                    case 1:
                        await new IngestMigrationV001().ApplyAsync(connection, transaction, cancellationToken);
                        break;
                    case 2:
                        await new IngestMigrationV002().ApplyAsync(connection, transaction, cancellationToken);
                        break;
                    case 3:
                        await new IngestMigrationV003().ApplyAsync(connection, transaction, cancellationToken);
                        break;
                    case 4:
                        await new IngestMigrationV004().ApplyAsync(connection, transaction, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("The ingest database schema version is not supported by this runtime.");
                }

                await IngestDatabase.ExecuteAsync(connection, $"PRAGMA user_version = {targetVersion};", cancellationToken, transaction);
                await transaction.CommitAsync(cancellationToken);
                userVersion = targetVersion;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }
}
