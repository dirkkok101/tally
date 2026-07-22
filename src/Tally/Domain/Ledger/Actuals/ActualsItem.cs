using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Actuals;

public enum ActualsRelationshipState
{
    None,
    TransferOutflow,
    TransferInflow,
    RefundOriginal,
    RefundCredit
}

public sealed record ActualsItem(
    string TransactionId,
    string AccountId,
    Money SignedAccountAmount,
    EffectiveDate EffectiveDate,
    string OriginalDescription,
    TransactionLifecycleStatus LifecycleStatus,
    TransactionCategoryState CategoryState,
    string? CategoryId,
    IReadOnlyList<string> CurrentAncestryIds,
    TransactionPoolState PoolState,
    string? PoolId,
    TransactionKnowledgeState InstrumentState,
    string? InstrumentId,
    TransactionKnowledgeState CardholderState,
    string? CardholderId,
    IReadOnlyList<EvidenceKind> EvidenceKinds,
    TransactionReconciliationState ReconciliationState,
    ActualsRelationshipState RelationshipState);
