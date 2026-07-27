using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Review;

public static class ApproveErrors
{
    public const string InvalidInput = "INGEST-APPROVE-INPUT-INVALID";
    public const string NotFound = "INGEST-APPROVE-NOT-FOUND";
    public const string DigestMismatch = "INGEST-APPROVE-DIGEST-MISMATCH";
    public const string NotCommittable = "INGEST-APPROVE-NOT-COMMITTABLE";
    public const string Blocked = "INGEST-APPROVE-BLOCKED";
}

[SupportedOSPlatform("linux")]
public sealed class ApproveHandler(ReviewStateStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<ApproveManifestResult>> HandleAsync(
        ApproveCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.BatchId) ||
            string.IsNullOrWhiteSpace(command.ManifestRevisionId) ||
            string.IsNullOrWhiteSpace(command.ManifestDigest) ||
            command.Actor is null ||
            string.IsNullOrWhiteSpace(command.Actor.Kind) ||
            string.IsNullOrWhiteSpace(command.Actor.Label))
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.InvalidInput);
        }

        var stored = await store.LoadAsync(command.BatchId, command.ManifestRevisionId, cancellationToken);
        if (stored is null)
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.NotFound);
        }

        if (!string.Equals(stored.CanonicalDigest, command.ManifestDigest, StringComparison.Ordinal))
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.DigestMismatch);
        }

        if (!stored.Committable ||
            stored.Outcomes.Any(outcome => outcome.Disposition == SourceRecordDisposition.Blocked) ||
            stored.Controls.Any(control =>
                string.Equals(control.Detail, "Mismatched", StringComparison.Ordinal)))
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.NotCommittable);
        }

        var approvedAt = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var approvalId = await store.ApproveAsync(
            command.BatchId,
            command.ManifestRevisionId,
            command.ManifestDigest,
            command.Actor,
            approvedAt,
            cancellationToken);

        if (approvalId is null)
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.NotFound);
        }

        if (approvalId == "reject")
        {
            return CommandResult<ApproveManifestResult>.Failure(ApproveErrors.Blocked);
        }

        return CommandResult<ApproveManifestResult>.Success(new ApproveManifestResult(
            approvalId,
            command.BatchId,
            command.ManifestRevisionId,
            approvedAt));
    }
}
