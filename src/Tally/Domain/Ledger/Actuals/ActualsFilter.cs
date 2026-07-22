using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Actuals;

public enum ActualsCategoryScope
{
    Exact,
    Subtree
}

public enum ActualsGroupKind
{
    None,
    Pool,
    CategoryDirect,
    CategorySubtree,
    PoolCategory
}

public sealed record ActualsFilter(
    IReadOnlyCollection<string>? AccountIds = null,
    EffectiveDate? EffectiveFrom = null,
    EffectiveDate? EffectiveTo = null,
    IReadOnlyCollection<string>? CategoryIds = null,
    ActualsCategoryScope CategoryScope = ActualsCategoryScope.Exact,
    IReadOnlyCollection<TransactionCategoryState>? CategorizationStates = null,
    IReadOnlyCollection<string>? PoolIds = null,
    IReadOnlyCollection<TransactionPoolState>? PoolStates = null,
    IReadOnlyCollection<string>? InstrumentIds = null,
    IReadOnlyCollection<TransactionKnowledgeState>? InstrumentStates = null,
    IReadOnlyCollection<string>? CardholderIds = null,
    IReadOnlyCollection<TransactionKnowledgeState>? CardholderStates = null,
    IReadOnlyCollection<EvidenceKind>? EvidenceKinds = null,
    IReadOnlyCollection<TransactionReconciliationState>? ReconciliationStates = null,
    IReadOnlyCollection<ActualsRelationshipState>? RelationshipStates = null,
    IReadOnlyCollection<TransactionLifecycleStatus>? LifecycleStates = null,
    ActualsGroupKind GroupBy = ActualsGroupKind.None)
{
    public const string InvalidError = "LEDGER-ACTUALS-FILTER-INVALID";

    public bool IsValid() =>
        (EffectiveFrom is null || EffectiveTo is null || EffectiveFrom.Value.Value <= EffectiveTo.Value.Value)
        && Enum.IsDefined(CategoryScope)
        && Enum.IsDefined(GroupBy)
        && ValidIds(AccountIds)
        && ValidIds(CategoryIds)
        && ValidIds(PoolIds)
        && ValidIds(InstrumentIds)
        && ValidIds(CardholderIds)
        && ValidEnums(CategorizationStates)
        && ValidEnums(PoolStates)
        && ValidEnums(InstrumentStates)
        && ValidEnums(CardholderStates)
        && ValidEnums(EvidenceKinds)
        && ValidEnums(ReconciliationStates)
        && ValidEnums(RelationshipStates)
        && ValidEnums(LifecycleStates);

    internal bool Matches(ActualsItem item)
    {
        var lifecycleStates = LifecycleStates ?? [TransactionLifecycleStatus.Active];
        return Contains(AccountIds, item.AccountId)
            && (EffectiveFrom is null || item.EffectiveDate.Value >= EffectiveFrom.Value.Value)
            && (EffectiveTo is null || item.EffectiveDate.Value <= EffectiveTo.Value.Value)
            && MatchesCategory(item)
            && Contains(CategorizationStates, item.CategoryState)
            && Contains(PoolIds, item.PoolId)
            && Contains(PoolStates, item.PoolState)
            && Contains(InstrumentIds, item.InstrumentId)
            && Contains(InstrumentStates, item.InstrumentState)
            && Contains(CardholderIds, item.CardholderId)
            && Contains(CardholderStates, item.CardholderState)
            && (EvidenceKinds is null || item.EvidenceKinds.Any(EvidenceKinds.Contains))
            && Contains(ReconciliationStates, item.ReconciliationState)
            && Contains(RelationshipStates, item.RelationshipState)
            && lifecycleStates.Contains(item.LifecycleStatus);
    }

    private bool MatchesCategory(ActualsItem item) => CategoryIds is null
        || CategoryScope switch
        {
            ActualsCategoryScope.Exact => item.CategoryId is not null && CategoryIds.Contains(item.CategoryId),
            ActualsCategoryScope.Subtree => item.CurrentAncestryIds.Any(CategoryIds.Contains),
            _ => false
        };

    private static bool Contains<T>(IReadOnlyCollection<T>? values, T? value) => values is null || value is not null && values.Contains(value);

    private static bool ValidIds(IReadOnlyCollection<string>? values) => values is null
        || values.Count > 0
        && values.Count == values.Distinct(StringComparer.Ordinal).Count()
        && values.All(value => LedgerId.TryParse(value, out _, out _));

    private static bool ValidEnums<T>(IReadOnlyCollection<T>? values) where T : struct, Enum => values is null
        || values.Count > 0
        && values.Count == values.Distinct().Count()
        && values.All(Enum.IsDefined);
}
