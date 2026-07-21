using System.Text;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Application.Ports;

namespace Tally.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class StoreGenerationManager(HostArtifactProtection artifactProtection) : IAuthoritativeStoreActivator
{
    public Task<LedgerDb> CreateCandidateAsync(string dataRoot, string generationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateGenerationId(generationId);
        artifactProtection.EnsureDataRoot(dataRoot);
        var candidate = new LedgerDb(Path.Combine(dataRoot, "candidates"), generationId);
        artifactProtection.EnsureDataRoot(candidate.DataRoot);
        artifactProtection.EnsureDataRoot(Path.GetDirectoryName(candidate.GenerationDirectory)!);
        artifactProtection.EnsureDataRoot(candidate.GenerationDirectory);
        return Task.FromResult(candidate);
    }

    public async Task ActivateAsync(string generationId, string verificationFingerprint, CancellationToken cancellationToken)
    {
        ValidateGenerationId(generationId);
        var root = CurrentDataRoot ?? throw new InvalidOperationException("A data root must be configured before activation.");
        var database = new LedgerDb(root, generationId);
        artifactProtection.RequireOwnerOnlyDirectory(database.GenerationDirectory);
        artifactProtection.RequireOwnerOnlyArtifact(database.DatabasePath);
        artifactProtection.RequireOwnerOnlyArtifact(database.ManifestPath);

        var manifestFingerprint = (await File.ReadAllTextAsync(database.ManifestPath, cancellationToken)).Trim();
        if (!string.Equals(manifestFingerprint, verificationFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The generation manifest does not match the verified fingerprint.");
        }

        await VerifySqliteIntegrityAsync(database.DatabasePath, cancellationToken);
        var currentPath = Path.Combine(root, "CURRENT");
        if (File.Exists(currentPath))
        {
            artifactProtection.RequireOwnerOnlyArtifact(currentPath);
            var priorGenerationId = (await File.ReadAllTextAsync(currentPath, cancellationToken)).Trim();
            ValidateGenerationId(priorGenerationId);
            artifactProtection.RequireOwnerOnlyDirectory(new LedgerDb(root, priorGenerationId).GenerationDirectory);
        }

        var temporaryPath = Path.Combine(root, $".CURRENT.{Guid.NewGuid():N}");
        try
        {
            await using (var temporaryPointer = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await temporaryPointer.WriteAsync(Encoding.UTF8.GetBytes(generationId), cancellationToken);
                await temporaryPointer.FlushAsync(cancellationToken);
                temporaryPointer.Flush(flushToDisk: true);
            }
            artifactProtection.ProtectArtifact(temporaryPath);
            File.Move(temporaryPath, currentPath, true);
            artifactProtection.RequireOwnerOnlyArtifact(currentPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public string? CurrentDataRoot { get; private set; }

    public void ConfigureDataRoot(string dataRoot)
    {
        artifactProtection.EnsureDataRoot(dataRoot);
        CurrentDataRoot = Path.GetFullPath(dataRoot);
    }

    private static async Task VerifySqliteIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        var integrity = Convert.ToString(await LedgerConnectionFactory.ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The candidate generation failed SQLite integrity verification.");
        }
    }

    private static void ValidateGenerationId(string generationId)
    {
        if (!Guid.TryParseExact(generationId, "N", out _))
        {
            throw new ArgumentException("Generation identifiers must be GUIDs in N format.", nameof(generationId));
        }
    }
}
