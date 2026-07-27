using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Recovery;

public static class CleanupErrors
{
    public const string InvalidInput = "INGEST-CLEANUP-INPUT-INVALID";
    public const string NotFound = "INGEST-CLEANUP-NOT-FOUND";
    public const string RetainedForRecovery = "INGEST-CLEANUP-RETAINED-FOR-RECOVERY";
    public const string LockHeld = "INGEST-CLEANUP-LOCK-HELD";
}

public sealed record CleanupCommand(string BatchId, BatchStatus ExpectedTerminalStatus);

[SupportedOSPlatform("linux")]
public sealed class CleanupHandler(
    RecoveryStateStore store,
    BatchCommitLock batchLock,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<CleanupBatchResult>> HandleAsync(
        CleanupCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.BatchId))
        {
            return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.InvalidInput);
        }

        if (command.ExpectedTerminalStatus is not (BatchStatus.Completed or BatchStatus.Abandoned))
        {
            return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.InvalidInput);
        }

        var snapshot = await store.LoadBatchAsync(command.BatchId, cancellationToken);
        if (snapshot is null)
        {
            return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.NotFound);
        }

        await using var held = await batchLock.TryAcquireAsync(command.BatchId, cancellationToken);
        if (held is null)
        {
            return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.LockHeld);
        }

        var now = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var (ok, error, removed) = await store.CleanupAsync(
            command.BatchId,
            command.ExpectedTerminalStatus,
            now,
            cancellationToken);

        if (!ok)
        {
            if (error == "not_found")
            {
                return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.NotFound);
            }

            await store.AppendErrorAsync(
                command.BatchId,
                CleanupErrors.RetainedForRecovery,
                "Cleanup retained artifacts required for recovery.",
                "cleanup_retained",
                now,
                cancellationToken);
            return CommandResult<CleanupBatchResult>.Failure(CleanupErrors.RetainedForRecovery);
        }

        return CommandResult<CleanupBatchResult>.Success(new CleanupBatchResult(
            command.BatchId,
            BatchStatus.Cleaned,
            removed));
    }
}
