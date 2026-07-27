using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Commit;

public static class ResumeErrors
{
    public const string InvalidInput = "INGEST-RESUME-INPUT-INVALID";
    public const string NotFound = "INGEST-RESUME-NOT-FOUND";
    public const string NotResumable = "INGEST-RESUME-NOT-RESUMABLE";
}

[SupportedOSPlatform("linux")]
public sealed class ResumeHandler(CommitStateStore commitStore, CandidateCommitSaga saga)
{
    public async Task<CommandResult<ImportReceipt>> HandleAsync(ResumeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.BatchId))
        {
            return CommandResult<ImportReceipt>.Failure(ResumeErrors.InvalidInput);
        }

        var target = await commitStore.ResolveResumeTargetAsync(command.BatchId, cancellationToken);
        if (target is null)
        {
            return CommandResult<ImportReceipt>.Failure(ResumeErrors.NotFound);
        }

        if (!target.Approved || string.IsNullOrWhiteSpace(target.ManifestRevisionId) || string.IsNullOrWhiteSpace(target.ManifestDigest))
        {
            return CommandResult<ImportReceipt>.Failure(ResumeErrors.NotResumable);
        }

        // Resume reuses the exact frozen approved revision — never a latest mutable choice.
        return await saga.ExecuteAsync(
            new CommitCommand(
                command.BatchId,
                target.ManifestRevisionId,
                target.ManifestDigest,
                ResumeMode: true),
            cancellationToken);
    }
}
