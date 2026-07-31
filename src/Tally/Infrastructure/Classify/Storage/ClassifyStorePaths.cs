namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Owner paths for the CLASSIFY-owned raw-SQLite durability boundary (DD-CLASSIFY-STATE-STORE).
/// Separate from ledger.db under the Tally data root.
/// </summary>
public sealed class ClassifyStorePaths
{
    public ClassifyStorePaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string ClassifyDirectory => Path.Combine(DataRoot, "classify");

    public string DatabasePath => Path.Combine(ClassifyDirectory, "classify.db");

    public string WalPath => DatabasePath + "-wal";

    public string ShmPath => DatabasePath + "-shm";

    public string JournalPath => DatabasePath + "-journal";

    public string LockPath => DatabasePath + ".lock";

    public string TemporaryDirectory => Path.Combine(ClassifyDirectory, "tmp");

    public string ReportsDirectory => Path.Combine(ClassifyDirectory, "reports");

    /// <summary>
    /// Database file plus recognized SQLite sidecars, locks, reports, and temporary writer artifacts.
    /// </summary>
    public IEnumerable<string> RecognizedArtifactPaths()
    {
        yield return DatabasePath;
        yield return WalPath;
        yield return ShmPath;
        yield return JournalPath;
        yield return LockPath;
        if (Directory.Exists(TemporaryDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(TemporaryDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }

        if (Directory.Exists(ReportsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(ReportsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }
}
