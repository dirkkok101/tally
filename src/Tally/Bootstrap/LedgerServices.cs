using Tally.Features.System.Contract;
using Tally.Application;
using Tally.Features.Ledger.Evidence;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Evidence;

namespace Tally.Bootstrap;

public sealed record LedgerServices(SystemOperationModule SystemOperations, EvidenceRegistryOperationModule? EvidenceRegistry)
{
    public static LedgerServices Create() => new(new SystemOperationModule(), null);

    public static LedgerServices Create(LedgerDb database)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        var store = new EvidenceStore(database, factory);
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        return new(new SystemOperationModule(), new(new RegisterEvidenceHandler(executor, store), new GetEvidenceHandler(store)));
    }
}
