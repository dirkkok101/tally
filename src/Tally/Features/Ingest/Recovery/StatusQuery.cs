namespace Tally.Features.Ingest.Recovery;

public sealed record StatusQuery(string? BatchId = null, int Limit = 50, string? Cursor = null);
