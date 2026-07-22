using Tally.Features.System.Contract;
using Tally.Features.System.Guidance;
using Tally.Application;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Relationships;
using Tally.Features.Ledger.Accounts;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Dimensions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Bootstrap;

public sealed record LedgerServices(SystemOperationModule SystemOperations, GuidanceOperationModule Guidance, AccountOperationModule? Accounts, CategoryOperationModule? Categories, PaymentIdentityOperationModule? PaymentIdentities, SpendPoolOperationModule? SpendPools, EvidenceRegistryOperationModule? EvidenceRegistry, TransactionOperationModule? Transactions, CategoryAllocationOperationModule? CategoryAllocations, PaymentAttributionOperationModule? PaymentAttributions, PoolAssignmentOperationModule? PoolAssignments, EvidenceLinkOperationModule? EvidenceLinks, TransferOperationModule? Transfers, RefundOperationModule? Refunds, RelationshipLifecycleOperationModule? RelationshipLifecycle)
{
    public static LedgerServices Create() => new(new SystemOperationModule(), CreateGuidance(), null, null, null, null, null, null, null, null, null, null, null, null, null);

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
        var spendPoolStore = new SpendPoolStore(database, factory);
        var spendPools = new SpendPoolOperationModule(
            new CreateSpendPoolHandler(executor, spendPoolStore), new GetSpendPoolHandler(spendPoolStore), new ListSpendPoolsHandler(spendPoolStore),
            new RenameSpendPoolHandler(executor, spendPoolStore), new ArchiveSpendPoolHandler(executor, spendPoolStore), new ReactivateSpendPoolHandler(executor, spendPoolStore));
        var evidenceStore = new EvidenceStore(database, factory);
        var evidence = new EvidenceRegistryOperationModule(new RegisterEvidenceHandler(executor, evidenceStore), new GetEvidenceHandler(evidenceStore));
        var transactionStore = new TransactionStore(database, factory);
        var transactions = new TransactionOperationModule(
            new RecordTransactionHandler(executor, accountStore, paymentIdentityStore, evidenceStore, transactionStore),
            new GetTransactionHandler(transactionStore));
        var categoryAllocationStore = new CategoryAllocationStore(database, factory);
        var categoryAllocations = new CategoryAllocationOperationModule(
            new AssignCategoryHandler(executor, transactionStore, categoryStore, categoryAllocationStore),
            new CorrectCategoryHandler(executor, transactionStore, categoryStore, categoryAllocationStore));
        var paymentAttributionStore = new PaymentAttributionStore();
        var paymentAttributions = new PaymentAttributionOperationModule(
            new AssignPaymentAttributionHandler(executor, transactionStore, paymentIdentityStore, paymentAttributionStore),
            new CorrectPaymentAttributionHandler(executor, transactionStore, paymentIdentityStore, paymentAttributionStore));
        var poolAssignmentStore = new PoolAssignmentStore();
        var poolAssignments = new PoolAssignmentOperationModule(
            new AssignPoolHandler(executor, transactionStore, spendPoolStore, poolAssignmentStore),
            new CorrectPoolHandler(executor, transactionStore, spendPoolStore, poolAssignmentStore));
        var evidenceLinks = new EvidenceLinkOperationModule(new LinkSupportingEvidenceHandler(executor, evidenceStore, transactionStore));
        var relationshipStore = new RelationshipStore(database, factory);
        var transfers = new TransferOperationModule(
            new ConfirmTransferHandler(executor, accountStore, transactionStore, relationshipStore),
            new GetRelationshipHandler(relationshipStore));
        var refunds = new RefundOperationModule(new ConfirmRefundHandler(executor, accountStore, transactionStore, relationshipStore));
        var lifecycle = new RelationshipLifecycleOperationModule(new RelationshipLifecycleHandler(executor, accountStore, transactionStore, relationshipStore), new GetRelationshipHandler(relationshipStore));
        return new(new SystemOperationModule(), CreateGuidance(), accounts, categories, paymentIdentities, spendPools, evidence, transactions, categoryAllocations, paymentAttributions, poolAssignments, evidenceLinks, transfers, refunds, lifecycle);
    }

    private static GuidanceOperationModule CreateGuidance() => new(new GuidanceService());
}
