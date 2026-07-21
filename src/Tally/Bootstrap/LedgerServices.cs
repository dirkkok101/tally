using Tally.Features.System.Contract;
using Tally.Application;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Accounts;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Evidence;

namespace Tally.Bootstrap;

public sealed record LedgerServices(SystemOperationModule SystemOperations, AccountOperationModule? Accounts, EvidenceRegistryOperationModule? EvidenceRegistry)
{
    public static LedgerServices Create() => new(new SystemOperationModule(), null, null);

    public static LedgerServices Create(LedgerDb database)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var accountStore = new AccountStore(database, factory);
        var accounts = new AccountOperationModule(
            new CreateAccountHandler(executor, accountStore),
            new GetAccountHandler(accountStore),
            new ListAccountsHandler(accountStore),
            new RenameAccountHandler(executor, accountStore),
            new ArchiveAccountHandler(executor, accountStore));
        var evidenceStore = new EvidenceStore(database, factory);
        var evidence = new EvidenceRegistryOperationModule(new RegisterEvidenceHandler(executor, evidenceStore), new GetEvidenceHandler(evidenceStore));
        return new(new SystemOperationModule(), accounts, evidence);
    }
}
