using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Ledger.Actuals;

public readonly record struct ActualsTotals(Money NetAccountMovement, Money ExternalSpend, Money BudgetActual)
{
    public static ActualsTotals Zero => new(Money.FromMinorUnits(0), Money.FromMinorUnits(0), Money.FromMinorUnits(0));

    public ActualsTotals Add(ActualsTotals value) => new(
        Money.FromMinorUnits(checked(NetAccountMovement.MinorUnits + value.NetAccountMovement.MinorUnits)),
        Money.FromMinorUnits(checked(ExternalSpend.MinorUnits + value.ExternalSpend.MinorUnits)),
        Money.FromMinorUnits(checked(BudgetActual.MinorUnits + value.BudgetActual.MinorUnits)));
}

public sealed record CalculatedActualsItem(ActualsItem Item, ActualsTotals Contribution);

public sealed record ActualsGroup(
    ActualsGroupKind Kind,
    TransactionPoolState? PoolState,
    string? PoolId,
    TransactionCategoryState? CategoryState,
    string? CategoryId,
    ActualsTotals Totals,
    IReadOnlyList<string> TransactionIds);

public sealed record ActualsCalculation(
    IReadOnlyList<CalculatedActualsItem> Items,
    ActualsTotals Totals,
    IReadOnlyList<ActualsGroup> Groups);

public static class ActualsCalculator
{
    public const string InvariantError = "LEDGER-ACTUALS-INVARIANT";

    public static ActualsCalculation Calculate(IEnumerable<ActualsItem> membership, ActualsGroupKind groupKind)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (!Enum.IsDefined(groupKind)) throw Invariant("Unknown actuals group kind.");

        var items = membership
            .OrderByDescending(item => item.EffectiveDate.Value)
            .ThenByDescending(item => item.TransactionId, StringComparer.Ordinal)
            .ToArray();
        if (items.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw Invariant("A transaction can occur only once in actuals membership.");
        }

        var calculated = new CalculatedActualsItem[items.Length];
        var totals = ActualsTotals.Zero;
        for (var index = 0; index < items.Length; index++)
        {
            Validate(items[index]);
            var contribution = Contribution(items[index]);
            calculated[index] = new(items[index], contribution);
            totals = totals.Add(contribution);
        }

        return new(calculated, totals, Group(calculated, groupKind));
    }

    private static ActualsTotals Contribution(ActualsItem item)
    {
        if (item.LifecycleStatus != TransactionLifecycleStatus.Active) return ActualsTotals.Zero;

        var contribution = ActiveContributionMinor(item.SignedAccountAmount.MinorUnits, item.RelationshipState);
        return new(
            Money.FromMinorUnits(contribution.NetAccountMovement),
            Money.FromMinorUnits(contribution.ExternalSpend),
            Money.FromMinorUnits(contribution.BudgetActual));
    }

    internal static (long NetAccountMovement, long ExternalSpend, long BudgetActual) ActiveContributionMinor(
        long signedAmountMinor,
        ActualsRelationshipState relationshipState)
    {
        var spend = relationshipState switch
        {
            ActualsRelationshipState.TransferOutflow or ActualsRelationshipState.TransferInflow => 0,
            ActualsRelationshipState.RefundCredit => checked(-signedAmountMinor),
            _ when signedAmountMinor < 0 => checked(-signedAmountMinor),
            _ => 0
        };
        return (signedAmountMinor, spend, spend);
    }

    private static IReadOnlyList<ActualsGroup> Group(IReadOnlyList<CalculatedActualsItem> items, ActualsGroupKind groupKind)
    {
        if (items.Count == 0) return [];
        var memberships = groupKind switch
        {
            ActualsGroupKind.None => items.Select(item => new GroupMember(GroupKey.None, item)),
            ActualsGroupKind.Pool => items.Select(item => new GroupMember(
                new(groupKind, item.Item.PoolState, item.Item.PoolId, null, null), item)),
            ActualsGroupKind.CategoryDirect => items.Select(item => new GroupMember(
                new(groupKind, null, null, item.Item.CategoryState, item.Item.CategoryId), item)),
            ActualsGroupKind.CategorySubtree => items.SelectMany(SubtreeMembership),
            ActualsGroupKind.PoolCategory => items.Select(item => new GroupMember(
                new(groupKind, item.Item.PoolState, item.Item.PoolId, item.Item.CategoryState, item.Item.CategoryId), item)),
            _ => throw Invariant("Unknown actuals group kind.")
        };

        return memberships
            .GroupBy(member => member.Key)
            .Select(group =>
            {
                var totals = group.Aggregate(ActualsTotals.Zero, (sum, member) => sum.Add(member.Item.Contribution));
                return new ActualsGroup(
                    group.Key.Kind,
                    group.Key.PoolState,
                    group.Key.PoolId,
                    group.Key.CategoryState,
                    group.Key.CategoryId,
                    totals,
                    group.Select(member => member.Item.Item.TransactionId).Order(StringComparer.Ordinal).ToArray());
            })
            .OrderBy(group => group.PoolState)
            .ThenBy(group => group.PoolId, StringComparer.Ordinal)
            .ThenBy(group => group.CategoryState)
            .ThenBy(group => group.CategoryId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<GroupMember> SubtreeMembership(CalculatedActualsItem item)
    {
        if (item.Item.CategoryState == TransactionCategoryState.Uncategorized)
        {
            yield return new(new(ActualsGroupKind.CategorySubtree, null, null, TransactionCategoryState.Uncategorized, null), item);
            yield break;
        }

        foreach (var categoryId in item.Item.CurrentAncestryIds)
        {
            yield return new(new(ActualsGroupKind.CategorySubtree, null, null, TransactionCategoryState.Categorized, categoryId), item);
        }
    }

    private static void Validate(ActualsItem item)
    {
        var categoryValid = item.CategoryState switch
        {
            TransactionCategoryState.Categorized => LedgerId.TryParse(item.CategoryId, out _, out _)
                && item.CurrentAncestryIds.Count > 0
                && item.CurrentAncestryIds[^1] == item.CategoryId
                && item.CurrentAncestryIds.Count == item.CurrentAncestryIds.Distinct(StringComparer.Ordinal).Count()
                && item.CurrentAncestryIds.All(id => LedgerId.TryParse(id, out _, out _)),
            TransactionCategoryState.Uncategorized => item.CategoryId is null && item.CurrentAncestryIds.Count == 0,
            _ => false
        };
        var poolValid = item.PoolState switch
        {
            TransactionPoolState.Assigned => LedgerId.TryParse(item.PoolId, out _, out _),
            TransactionPoolState.Unassigned => item.PoolId is null,
            _ => false
        };
        var instrumentValid = KnowledgeIsValid(item.InstrumentState, item.InstrumentId);
        var cardholderValid = KnowledgeIsValid(item.CardholderState, item.CardholderId);
        var relationshipDirectionValid = item.RelationshipState switch
        {
            ActualsRelationshipState.None => true,
            ActualsRelationshipState.TransferOutflow or ActualsRelationshipState.RefundOriginal => item.SignedAccountAmount.MinorUnits < 0,
            ActualsRelationshipState.TransferInflow or ActualsRelationshipState.RefundCredit => item.SignedAccountAmount.MinorUnits > 0,
            _ => false
        };
        if (!LedgerId.TryParse(item.TransactionId, out _, out _)
            || !LedgerId.TryParse(item.AccountId, out _, out _)
            || !Enum.IsDefined(item.LifecycleStatus)
            || !Enum.IsDefined(item.ReconciliationState)
            || !categoryValid
            || !poolValid
            || !instrumentValid
            || !cardholderValid
            || item.EvidenceKinds is null
            || item.EvidenceKinds.Count != item.EvidenceKinds.Distinct().Count()
            || item.EvidenceKinds.Any(kind => !Enum.IsDefined(kind))
            || !relationshipDirectionValid)
        {
            throw Invariant("Actuals membership violates a financial or dimensional invariant.");
        }
    }

    private static bool KnowledgeIsValid(TransactionKnowledgeState state, string? id) => state switch
    {
        TransactionKnowledgeState.Known => LedgerId.TryParse(id, out _, out _),
        TransactionKnowledgeState.Unknown => id is null,
        _ => false
    };

    private static InvalidOperationException Invariant(string detail) => new($"{InvariantError}: {detail}");

    private sealed record GroupKey(
        ActualsGroupKind Kind,
        TransactionPoolState? PoolState,
        string? PoolId,
        TransactionCategoryState? CategoryState,
        string? CategoryId)
    {
        public static GroupKey None { get; } = new(ActualsGroupKind.None, null, null, null, null);
    }

    private sealed record GroupMember(GroupKey Key, CalculatedActualsItem Item);
}
