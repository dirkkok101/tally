using System.Runtime.Versioning;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Tally.Infrastructure.Storage;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY state composition (no reflection / plugin scan).
/// Creates the owner-only store under the Tally data root, separate from ledger.db.
/// Recovers committed/uncommitted quarantine before any mutation services are returned.
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyStateExtensions
{
    public static async Task<ClassifyStateServices> CreateStateAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var protection = new HostArtifactProtection();
        var store = new ClassifyStateStore(dataRoot, protection);
        await store.InitializeAsync(cancellationToken);

        // Startup recovery: restore uncommitted quarantine or delete committed quarantine
        // according to durable tombstone / cleanup-event evidence — before new CLASSIFY mutation.
        var artifacts = new ClassifyArtifactProtection(store.Paths, protection);
        var recovery = new ClassificationRecoveryStore();
        await using (var connection = await store.OpenMigratedAsync(cancellationToken))
        {
            artifacts.RecoverQuarantineAtStartup((kind, operationId) =>
            {
                // Synchronous evidence probe on already-open connection.
                return kind switch
                {
                    "cleanup" => recovery.HasCleanupEventAsync(
                            connection, null, operationId, cancellationToken)
                        .GetAwaiter().GetResult(),
                    "abandon" => recovery.HasTombstoneIdAsync(
                            connection, null, operationId, cancellationToken)
                        .GetAwaiter().GetResult(),
                    _ => false
                };
            });
        }

        var idempotency = new ClassifyOperationIdempotencyStore();
        return new ClassifyStateServices(store, idempotency, protection, artifacts);
    }
}

[SupportedOSPlatform("linux")]
public sealed record ClassifyStateServices(
    ClassifyStateStore Store,
    ClassifyOperationIdempotencyStore Idempotency,
    HostArtifactProtection Protection,
    ClassifyArtifactProtection? Artifacts = null);
