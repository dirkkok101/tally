using System.Collections.Immutable;

namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-FORMAT-ADAPTERS
public sealed class StatementAdapterRegistry
{
    private readonly ImmutableArray<IStatementAdapter> _adapters;

    public StatementAdapterRegistry(IEnumerable<IStatementAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var materialised = adapters.ToArray();
        if (materialised.Length != 2 ||
            materialised.Count(adapter => adapter is PdfTextLayoutAStatementAdapter) != 1 ||
            materialised.Count(adapter => adapter is PdfTextLayoutBStatementAdapter) != 1)
        {
            throw new ArgumentException(
                "The registry must contain exactly one Layout A adapter and one Layout B adapter.",
                nameof(adapters));
        }

        // Deterministic order: Layout A then Layout B by variant id.
        _adapters =
        [
            materialised.OfType<PdfTextLayoutAStatementAdapter>().Single(),
            materialised.OfType<PdfTextLayoutBStatementAdapter>().Single()
        ];
    }

    public static StatementAdapterRegistry CreateDefault() =>
        new([new PdfTextLayoutAStatementAdapter(), new PdfTextLayoutBStatementAdapter()]);

    public IReadOnlyList<IStatementAdapter> Adapters => _adapters;

    public IReadOnlyList<FormatVariantDescriptor> Descriptors =>
        _adapters.Select(adapter => adapter.Descriptor).ToArray();

    public AdapterSelectionResult Select(PdfDocumentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var exact = new List<(IStatementAdapter Adapter, VariantProbeResult Probe)>();
        foreach (var adapter in _adapters)
        {
            var probe = adapter.Probe(evidence);
            if (probe.Outcome == VariantProbeOutcome.ExactMatch)
            {
                exact.Add((adapter, probe));
            }
        }

        if (exact.Count == 1)
        {
            return new AdapterSelectionResult(
                exact[0].Adapter,
                exact[0].Probe,
                AdapterSelectionStatus.ExclusiveMatch);
        }

        return new AdapterSelectionResult(
            null,
            null,
            exact.Count == 0 ? AdapterSelectionStatus.NoMatch : AdapterSelectionStatus.Ambiguous);
    }
}

public enum AdapterSelectionStatus
{
    ExclusiveMatch,
    NoMatch,
    Ambiguous
}

public sealed record AdapterSelectionResult(
    IStatementAdapter? Adapter,
    VariantProbeResult? Probe,
    AdapterSelectionStatus Status);
