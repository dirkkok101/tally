namespace Tally.Infrastructure.Storage;

public sealed class LedgerDb(string dataRoot, string generationId)
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);
    public string GenerationId { get; } = ValidateGenerationId(generationId);
    public string GenerationDirectory => Path.Combine(DataRoot, "generations", GenerationId);
    public string DatabasePath => Path.Combine(GenerationDirectory, "ledger.db");
    public string ManifestPath => Path.Combine(GenerationDirectory, "manifest");

    private static string ValidateGenerationId(string value) => Guid.TryParseExact(value, "N", out _)
        ? value
        : throw new ArgumentException("Generation identifiers must be GUIDs in N format.", nameof(generationId));
}
