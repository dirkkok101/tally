using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Recovery;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;

namespace Tally.Features.Classify.Recovery.Cleanup;

/// <summary>
/// classify.cleanup — fixed-policy cleanup with reversible same-filesystem quarantine staging.
/// Durable cleanup_event + terminal idempotency commit before final staged deletion.
/// Replay performs no filesystem mutation. Receipt is metadata-only.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CleanupClassificationStateCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRecoveryStore recoveryStore;
    private readonly ClassifyArtifactProtection artifactProtection;
    private readonly TimeProvider timeProvider;

    public CleanupClassificationStateCommand(
        ClassifyStateStore stateStore,
        ClassificationRecoveryStore recoveryStore,
        ClassifyArtifactProtection artifactProtection,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(recoveryStore);
        ArgumentNullException.ThrowIfNull(artifactProtection);
        this.stateStore = stateStore;
        this.recoveryStore = recoveryStore;
        this.artifactProtection = artifactProtection;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyCleanupResult>> HandleAsync(
        ClassifyCleanupRequest input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (!ClassifyRetentionPolicy.IsSupportedCleanupPolicyVersion(input.PolicyVersion))
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var policyVersion = ClassifyRetentionPolicy.PolicyVersion;

        var fingerprintElement = ClassifyContractMapper.ToCleanupFingerprintElement(
            ClassifyOperationIds.ContractVersion, policyVersion);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.Cleanup,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        // Replay: no filesystem mutation.
        var probed = await TryProbeAsync(idempotencyKey, requestFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs));
        var ct = timeout.Token;

        ClassifyArtifactQuarantine? quarantine = null;
        try
        {
            artifactProtection.EnsureClassifyLayout();

            var now = timeProvider.GetUtcNow();
            var cleanupId = ClassifyContractMapper.NewRuleVersionId(now);
            var occurredAt = ClassifyContractMapper.FormatUtc(now);

            // Inventory recognized temps; partition removable (unlocked) vs retained (locked/etc.).
            var allTemps = artifactProtection.ListRecognizedTemporaryFileNames().ToList();
            var abandonedSubjectIds = new List<string>();
            IReadOnlyList<(string PreviewId, string ExpiresAt)> expired;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                expired = await recoveryStore.ListExpiredUnreferencedPreviewsAsync(
                    connection, null, now, ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT subject_id FROM abandonment_tombstone ORDER BY subject_id ASC;";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    abandonedSubjectIds.Add(reader.GetString(0));
                }
            }

            var temporaryCandidates = allTemps
                .Where(n => !abandonedSubjectIds.Any(s => n.Contains(s, StringComparison.Ordinal)))
                .ToArray();
            var abandonedTempCandidates = allTemps
                .Where(n => abandonedSubjectIds.Any(s => n.Contains(s, StringComparison.Ordinal)))
                .ToArray();

            var tempPartition = artifactProtection.PartitionRecognizedTemporaries(temporaryCandidates);
            var abandonedPartition = artifactProtection.PartitionRecognizedTemporaries(abandonedTempCandidates);

            // Stage only unlocked removable files; locked recognized files stay and count as retained.
            var stageNames = tempPartition.Removable
                .Concat(abandonedPartition.Removable)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            if (stageNames.Length > 0)
            {
                quarantine = artifactProtection.TryStageRecognizedTemporaries(
                    cleanupId, "cleanup", stageNames);
                if (quarantine is null)
                {
                    return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Integrity);
                }
            }
            else
            {
                // Empty stage is success (nothing removable).
                quarantine = artifactProtection.TryStageRecognizedTemporaries(
                    cleanupId, "cleanup", Array.Empty<string>());
            }

            var removedTemporary = tempPartition.Removable.Count;
            var abandonedPayload = abandonedPartition.Removable.Count;
            var stagedCount = quarantine?.StagedCount ?? 0;
            // Retained recognized locked/non-removable files (still under tmp).
            var retainedLockedCount = tempPartition.Retained.Count + abandonedPartition.Retained.Count;

            // Expired preview tombstones (RESTRICT: no hard-delete of preview rows) under writer.
            var expiredPreviewCount = 0;
            var expiredIds = expired.Select(e => e.PreviewId).ToArray();

            var retainedAfter = 0;
            CommandResult<ClassifyCleanupResult> writeResult;
            try
            {
                writeResult = await stateStore.ExecuteWriteAsync(
                    async (connection, transaction, writeCt) =>
                    {
                        var existing = await idempotencyStore.FindAsync(
                            connection, transaction, idempotencyKey, writeCt);
                        var lookup = idempotencyStore.Resolve(
                            existing,
                            ClassifyOperationIds.Cleanup,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint);
                        switch (lookup.Disposition)
                        {
                            case ClassifyIdempotencyDisposition.Replay:
                                return ReplayOrIntegrity(lookup.Record!);
                            case ClassifyIdempotencyDisposition.Conflict:
                                return CommandResult<ClassifyCleanupResult>.Failure(
                                    ClassifyErrors.IdempotencyConflict);
                            case ClassifyIdempotencyDisposition.Miss:
                                break;
                            default:
                                return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Unexpected);
                        }

                        foreach (var previewId in expiredIds)
                        {
                            writeCt.ThrowIfCancellationRequested();
                            var existingTomb = await recoveryStore.GetTombstoneAsync(
                                connection, transaction, ClassifyRetentionPolicy.SubjectTypePreview, previewId, writeCt);
                            if (existingTomb is not null)
                            {
                                continue;
                            }

                            var refs = await recoveryStore.ProbePreviewReferencesAsync(
                                connection, transaction, previewId, writeCt);
                            var decision = ClassifyRetentionPolicy.EvaluateAbandon(
                                ClassifyStatusSubjectType.Preview, refs);
                            if (!decision.Allowed)
                            {
                                continue;
                            }

                            var tombstone = ClassifyContractMapper.ToTombstoneRow(
                                ClassifyContractMapper.NewRuleVersionId(timeProvider.GetUtcNow()),
                                ClassifyStatusSubjectType.Preview,
                                previewId,
                                "cleanup expired unreferenced preview",
                                actorText,
                                ClassifyContractMapper.FormatUtc(timeProvider.GetUtcNow()),
                                removedPayloadCount: 0);
                            await recoveryStore.InsertTombstoneAsync(connection, transaction, tombstone, writeCt);
                            expiredPreviewCount++;
                        }

                        // Retained = locked/non-removable recognized still present + any other recognized temps.
                        retainedAfter = artifactProtection.CountRecognizedTemporaryArtifacts();
                        // Stable floor: at least the locked partition we observed.
                        if (retainedAfter < retainedLockedCount)
                        {
                            retainedAfter = retainedLockedCount;
                        }

                        var removedArtifactCount = stagedCount + expiredPreviewCount;
                        var eventRow = ClassifyContractMapper.ToCleanupEventRow(
                            cleanupId,
                            policyVersion,
                            recognizedRemovedCount: stagedCount,
                            expiredPreviewCount: expiredPreviewCount,
                            abandonedPayloadCount: abandonedPayload,
                            actorText,
                            occurredAt,
                            removedArtifactCount,
                            retainedAfter);

                        var publicResult = ClassifyContractMapper.ToCleanupResult(
                            cleanupId,
                            policyVersion,
                            removedArtifactCount,
                            retainedAfter,
                            removedTemporary,
                            expiredPreviewCount,
                            abandonedPayload);

                        await recoveryStore.InsertCleanupEventAsync(connection, transaction, eventRow, writeCt);
                        await idempotencyStore.CommitAsync(
                            connection,
                            transaction,
                            new ClassifyOperationIdempotencyRow(
                                idempotencyKey,
                                ClassifyOperationIds.Cleanup,
                                ClassifyOperationIds.ContractVersion,
                                requestFingerprint,
                                SerializeResult(publicResult),
                                occurredAt),
                            writeCt);

                        return CommandResult<ClassifyCleanupResult>.Success(publicResult);
                    },
                    ct);
            }
            catch
            {
                quarantine?.RestoreAndDiscard();
                quarantine = null;
                throw;
            }

            if (writeResult.IsSuccess)
            {
                // The transaction and terminal idempotency outcome are already durable. Detach
                // the quarantine before best-effort finalization so a cancelled/failed authority
                // re-probe cannot restore files behind the committed cleanup receipt. Startup
                // recovery will rebind the manifest operation ID to durable DB authority.
                var committedQuarantine = quarantine;
                quarantine = null;
                try
                {
                    await using var connection = await stateStore.OpenMigratedAsync(ct);
                    var durable = await recoveryStore.HasCleanupEventAsync(
                        connection, null, cleanupId, ct);
                    committedQuarantine?.FinalizeWithDurableAuthority(durable);
                }
                catch
                {
                    // Leave the protected manifest and staged files for startup reconciliation.
                }
            }
            else
            {
                quarantine?.RestoreAndDiscard();
                quarantine = null;
            }

            return writeResult;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            quarantine?.RestoreAndDiscard();
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            quarantine?.RestoreAndDiscard();
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException)
        {
            quarantine?.RestoreAndDiscard();
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyCleanupResult>?> TryProbeAsync(
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        await using var transaction = stateStore.BeginImmediate(connection);
        try
        {
            var existing = await idempotencyStore.FindAsync(
                connection, transaction, idempotencyKey, cancellationToken);
            var lookup = idempotencyStore.Resolve(
                existing,
                ClassifyOperationIds.Cleanup,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyCleanupResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyCleanupResult);
            return result is null
                ? CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyCleanupResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyCleanupResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyCleanupResult);
}
