using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Review;

public static class InspectErrors
{
    public const string InvalidInput = "INGEST-INSPECT-INPUT-INVALID";
    public const string NotFound = "INGEST-INSPECT-NOT-FOUND";
}

[SupportedOSPlatform("linux")]
public sealed class InspectHandler(ReviewStateStore store)
{
    public async Task<CommandResult<InspectManifestResult>> HandleAsync(
        InspectQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.BatchId) || string.IsNullOrWhiteSpace(query.ManifestRevisionId))
        {
            return CommandResult<InspectManifestResult>.Failure(InspectErrors.InvalidInput);
        }

        var stored = await store.LoadAsync(query.BatchId, query.ManifestRevisionId, cancellationToken);
        if (stored is null)
        {
            return CommandResult<InspectManifestResult>.Failure(InspectErrors.NotFound);
        }

        var exclusions = stored.Outcomes
            .Where(outcome => outcome.Disposition == SourceRecordDisposition.ExcludedNonTransaction)
            .ToArray();
        var duplicates = stored.Outcomes
            .Where(outcome => outcome.Disposition == SourceRecordDisposition.ExactDuplicate)
            .ToArray();
        var conflicts = stored.Outcomes
            .Where(outcome => outcome.Disposition == SourceRecordDisposition.Blocked)
            .ToArray();

        return CommandResult<InspectManifestResult>.Success(new InspectManifestResult(
            stored.BatchId,
            stored.ManifestRevisionId,
            stored.CanonicalDigest,
            new IngestVersions(stored.LedgerContractVersion, stored.ManifestSchemaVersion),
            stored.SelectedAccountId,
            stored.Outcomes,
            stored.Candidates,
            exclusions,
            duplicates,
            conflicts,
            stored.Controls,
            stored.Approval));
    }
}
