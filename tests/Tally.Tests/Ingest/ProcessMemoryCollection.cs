using Xunit;

namespace Tally.Tests.Ingest;

/// <summary>
/// Serialises tests that sample process-wide peak RSS / WorkingSet so concurrent
/// suite work cannot inflate peak-growth measurements under xUnit parallelisation.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessMemoryCollection
{
    public const string Name = "process-memory";
}
