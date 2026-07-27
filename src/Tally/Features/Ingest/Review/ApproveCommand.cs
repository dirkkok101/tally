using Tally.Contracts.Common;

namespace Tally.Features.Ingest.Review;

public sealed record ApproveCommand(
    string BatchId,
    string ManifestRevisionId,
    string ManifestDigest,
    SafeActor Actor);
