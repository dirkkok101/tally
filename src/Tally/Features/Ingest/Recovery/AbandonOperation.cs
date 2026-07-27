using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Ingest;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Recovery;

public static class AbandonErrors
{
    public const string InvalidInput = "INGEST-ABANDON-INPUT-INVALID";
    public const string NotFound = "INGEST-ABANDON-NOT-FOUND";
    public const string NotAbandonable = "INGEST-ABANDON-NOT-ABANDONABLE";
    public const string LockHeld = "INGEST-ABANDON-LOCK-HELD";
}

public sealed record AbandonCommand(string BatchId, string Reason);

[SupportedOSPlatform("linux")]
public sealed class AbandonHandler(
    RecoveryStateStore store,
    BatchCommitLock batchLock,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<AbandonBatchResult>> HandleAsync(
        AbandonCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.BatchId) || string.IsNullOrWhiteSpace(command.Reason))
        {
            return CommandResult<AbandonBatchResult>.Failure(AbandonErrors.InvalidInput);
        }

        var snapshot = await store.LoadBatchAsync(command.BatchId, cancellationToken);
        if (snapshot is null)
        {
            return CommandResult<AbandonBatchResult>.Failure(AbandonErrors.NotFound);
        }

        if (snapshot.Status is BatchStatus.Completed or BatchStatus.Abandoned or BatchStatus.Cleaned)
        {
            return CommandResult<AbandonBatchResult>.Failure(AbandonErrors.NotAbandonable);
        }

        await using var held = await batchLock.TryAcquireAsync(command.BatchId, cancellationToken);
        if (held is null)
        {
            return CommandResult<AbandonBatchResult>.Failure(AbandonErrors.LockHeld);
        }

        var now = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var abandoned = await store.AbandonAsync(command.BatchId, command.Reason, now, cancellationToken);
        if (!abandoned)
        {
            return CommandResult<AbandonBatchResult>.Failure(AbandonErrors.NotAbandonable);
        }

        return CommandResult<AbandonBatchResult>.Success(new AbandonBatchResult(
            command.BatchId,
            BatchStatus.Abandoned,
            RetainedMetadata: true,
            snapshot.PriorLedgerEffectCount));
    }
}
