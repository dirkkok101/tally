using Tally.Features.System.Contract;

namespace Tally.Bootstrap;

public sealed record LedgerServices(SystemOperationModule SystemOperations)
{
    public static LedgerServices Create() => new(new SystemOperationModule());
}
