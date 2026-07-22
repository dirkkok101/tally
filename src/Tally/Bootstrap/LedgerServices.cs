using Tally.Features.System.Contract;
using Tally.Application;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Accounts;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Dimensions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;

namespace Tally.Bootstrap;

public sealed record LedgerServices(SystemOperationModule SystemOperations, AccountOperationModule? Accounts, CategoryOperationModule? Categories, PaymentIdentityOperationModule? PaymentIdentities, EvidenceRegistryOperationModule? EvidenceRegistry)
{
    public static LedgerServices Create() => new(new SystemOperationModule(), null, null, null, null);

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
        var categoryStore = new CategoryStore(database, factory);
        var categories = new CategoryOperationModule(
            new CreateCategoryHandler(executor, categoryStore), new GetCategoryHandler(categoryStore), new ListCategoriesHandler(categoryStore),
            new RenameCategoryHandler(executor, categoryStore), new ReparentCategoryHandler(executor, categoryStore),
            new ArchiveCategoryHandler(executor, categoryStore), new ReactivateCategoryHandler(executor, categoryStore));
        var paymentIdentityStore = new PaymentIdentityStore(database, factory);
        var paymentIdentities = new PaymentIdentityOperationModule(
            new CreatePaymentInstrumentHandler(executor, paymentIdentityStore), new GetPaymentInstrumentHandler(paymentIdentityStore), new ListPaymentInstrumentsHandler(paymentIdentityStore),
            new RenamePaymentInstrumentHandler(executor, paymentIdentityStore), new ArchivePaymentInstrumentHandler(executor, paymentIdentityStore), new ReactivatePaymentInstrumentHandler(executor, paymentIdentityStore),
            new CreateCardholderHandler(executor, paymentIdentityStore), new GetCardholderHandler(paymentIdentityStore), new ListCardholdersHandler(paymentIdentityStore),
            new RenameCardholderHandler(executor, paymentIdentityStore), new ArchiveCardholderHandler(executor, paymentIdentityStore), new ReactivateCardholderHandler(executor, paymentIdentityStore));
        var evidenceStore = new EvidenceStore(database, factory);
        var evidence = new EvidenceRegistryOperationModule(new RegisterEvidenceHandler(executor, evidenceStore), new GetEvidenceHandler(evidenceStore));
        return new(new SystemOperationModule(), accounts, categories, paymentIdentities, evidence);
    }
}
