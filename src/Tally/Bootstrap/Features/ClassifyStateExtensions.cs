using System.Runtime.Versioning;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Storage;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY state composition (no reflection / plugin scan).
/// Creates the owner-only store under the Tally data root, separate from ledger.db.
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
        var idempotency = new ClassifyOperationIdempotencyStore();
        return new ClassifyStateServices(store, idempotency, protection);
    }
}

[SupportedOSPlatform("linux")]
public sealed record ClassifyStateServices(
    ClassifyStateStore Store,
    ClassifyOperationIdempotencyStore Idempotency,
    HostArtifactProtection Protection);
