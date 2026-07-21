namespace Tally.Domain.Ledger;

public readonly record struct ExactTotals(Money NetAccountMovement)
{
    public static ExactTotals Zero => new(Money.FromMinorUnits(0));

    public ExactTotals Add(Money amount) => new(Money.FromMinorUnits(checked(NetAccountMovement.MinorUnits + amount.MinorUnits)));
}
