namespace Tally.Infrastructure.Budget.Storage;

/// <summary>
/// Owner paths for the BUDGET-owned raw-SQLite durability boundary (DD-BUDGET-STATE-STORE).
/// </summary>
public sealed class BudgetStorePaths
{
    public BudgetStorePaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string BudgetDirectory => Path.Combine(DataRoot, "budget");

    public string DatabasePath => Path.Combine(BudgetDirectory, "budget.db");

    public string WalPath => DatabasePath + "-wal";

    public string ShmPath => DatabasePath + "-shm";

    public string JournalPath => DatabasePath + "-journal";

    public string LockPath => DatabasePath + ".lock";

    public string AtomicPath => DatabasePath + ".atomic";

    /// <summary>
    /// Database file plus recognized SQLite sidecars and temporary writer artifacts.
    /// </summary>
    public IEnumerable<string> RecognizedArtifactPaths()
    {
        yield return DatabasePath;
        yield return WalPath;
        yield return ShmPath;
        yield return JournalPath;
        yield return LockPath;
        yield return AtomicPath;
    }
}
