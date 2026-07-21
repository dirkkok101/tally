using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Storage;

public interface ILedgerSchemaFragment
{
    int Version { get; }
    string Name { get; }
    Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}

public sealed class LedgerSchemaFragmentRegistry(IEnumerable<ILedgerSchemaFragment> fragments, IEnumerable<string> requiredFragmentNames)
{
    private readonly ILedgerSchemaFragment[] fragments = Validate(fragments, requiredFragmentNames);

    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var highestVersion = fragments.Max(fragment => fragment.Version);
        var userVersion = Convert.ToInt32(await LedgerConnectionFactory.ScalarAsync(connection, "PRAGMA user_version;", cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (userVersion > highestVersion)
        {
            throw new InvalidOperationException("The database schema version is newer than this migration registry supports.");
        }

        await using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var fragment in fragments.Where(fragment => fragment.Version > userVersion))
            {
                await fragment.ApplyAsync(connection, transaction, cancellationToken);
            }

            await LedgerConnectionFactory.ExecuteAsync(connection, $"PRAGMA user_version = {highestVersion};", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ILedgerSchemaFragment[] Validate(IEnumerable<ILedgerSchemaFragment> fragments, IEnumerable<string> requiredFragmentNames)
    {
        var ordered = fragments.OrderBy(fragment => fragment.Version).ThenBy(fragment => fragment.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("At least one schema fragment is required.");
        }

        if (ordered.Select(fragment => fragment.Name).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new InvalidOperationException("Schema fragment names must be unique.");
        }

        var actual = ordered.Select(fragment => fragment.Name).ToHashSet(StringComparer.Ordinal);
        if (requiredFragmentNames.Any(name => !actual.Contains(name)))
        {
            throw new InvalidOperationException("A required schema fragment is missing.");
        }

        return ordered;
    }
}
