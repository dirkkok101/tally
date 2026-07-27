using System.Runtime.Versioning;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit BUDGET state composition (no reflection / plugin scan).
/// Creates the owner-only store under the Tally data root.
/// </summary>
[SupportedOSPlatform("linux")]
public static class BudgetStateExtensions
{
    public static async Task<BudgetStateServices> CreateStateAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var protection = new HostArtifactProtection();
        var store = new BudgetStateStore(dataRoot, protection);
        await store.InitializeAsync(cancellationToken);
        var idempotency = new BudgetIdempotencyStore();
        return new BudgetStateServices(store, idempotency, protection);
    }
}

[SupportedOSPlatform("linux")]
public sealed record BudgetStateServices(
    BudgetStateStore Store,
    BudgetIdempotencyStore Idempotency,
    HostArtifactProtection Protection);
