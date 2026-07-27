namespace Tally.Features.Ingest.Commit;

public sealed record CommitCommand(
    string BatchId,
    string ManifestRevisionId,
    string ManifestDigest,
    bool ResumeMode = false);
