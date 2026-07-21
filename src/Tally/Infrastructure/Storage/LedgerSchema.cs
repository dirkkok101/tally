using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage.Migrations.V001;
using Tally.Infrastructure.Storage.Migrations.V002;

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

public static class CompleteLedgerSchema
{
    public const int CurrentVersion = 2;
    public static IReadOnlyList<string> V1FragmentNames { get; } =
    [
        V001StorageSchema.FragmentName,
        V001CatalogueSchema.FragmentName,
        V001RelationshipActualsSchema.FragmentName,
        V001TransactionSchema.FragmentName,
        V001EvidenceReconciliationSchema.FragmentName
    ];
    public static IReadOnlyList<string> CurrentFragmentNames { get; } =
    [.. V1FragmentNames, V002StatementAuthoritySchema.FragmentName];

    public static LedgerSchemaFragmentRegistry CreateV1() => new(V1Fragments(), V1FragmentNames);

    public static LedgerSchemaFragmentRegistry CreateCurrent() => new(
        [.. V1Fragments(), new V002StatementAuthoritySchema()],
        CurrentFragmentNames);

    private static ILedgerSchemaFragment[] V1Fragments() =>
    [
        new V001StorageSchema(),
        new V001CatalogueSchema(),
        new V001RelationshipActualsSchema(),
        new V001TransactionSchema(),
        new V001EvidenceReconciliationSchema()
    ];
}

[SupportedOSPlatform("linux")]
public static class LedgerRuntimeBootstrap
{
    public static async Task<LedgerDb> InitializeCurrentAsync(string dataRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var protection = new HostArtifactProtection();
        protection.EnsureDataRoot(dataRoot);
        var currentPath = Path.Combine(Path.GetFullPath(dataRoot), "CURRENT");
        if (File.Exists(currentPath))
        {
            protection.RequireOwnerOnlyArtifact(currentPath);
            var currentGeneration = (await File.ReadAllTextAsync(currentPath, cancellationToken)).Trim();
            var current = new LedgerDb(dataRoot, currentGeneration);
            await ApplyCurrentAsync(current, protection, cancellationToken);
            return current;
        }

        var generationId = Guid.NewGuid().ToString("N");
        var database = new LedgerDb(dataRoot, generationId);
        await ApplyCurrentAsync(database, protection, cancellationToken);
        const string fingerprint = "ledger-schema-v2";
        await File.WriteAllTextAsync(database.ManifestPath, fingerprint, cancellationToken);
        protection.ProtectArtifact(database.ManifestPath);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(dataRoot);
        await manager.ActivateAsync(generationId, fingerprint, cancellationToken);
        return database;
    }

    private static async Task ApplyCurrentAsync(LedgerDb database, HostArtifactProtection protection, CancellationToken cancellationToken)
    {
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, cancellationToken);
    }
}
