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
/// classify.cleanup vertical slice
/// (FR-CLASSIFY-STATE-RETENTION-CLEANUP / TASK-CLASSIFY-RULEBOOK-ABANDON-CLEANUP).
/// Fixed-policy cleanup: accepts policy version only (no path). Removes only recognized
/// unlocked temporaries, tombstones expired unreferenced previews, and clears abandoned
/// subject-scoped temporary residue. Records metadata-only cleanup_event counts.
/// Never follows symlinks, globs outside CLASSIFY root, or hard-deletes referenced history.
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

        var probed = await TryProbeAsync(idempotencyKey, requestFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs));
        var ct = timeout.Token;

        try
        {
            artifactProtection.EnsureClassifyLayout();

            // 1) Startup-equivalent: recognized unlocked temporary crash residue only.
            var removedTemporary = artifactProtection.RecoverRecognizedTemporaryResidue();

            // 2) Tombstone expired unreferenced previews (RESTRICT: no hard-delete of preview rows).
            var now = timeProvider.GetUtcNow();
            var expiredPreviewCount = 0;
            IReadOnlyList<(string PreviewId, string ExpiresAt)> expired;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                expired = await recoveryStore.ListExpiredUnreferencedPreviewsAsync(
                    connection, null, now, ct);
            }

            foreach (var (previewId, _) in expired)
            {
                ct.ThrowIfCancellationRequested();
                var tombstoned = await stateStore.ExecuteWriteAsync(
                    async (connection, transaction, writeCt) =>
                    {
                        var existing = await recoveryStore.GetTombstoneAsync(
                            connection, transaction, ClassifyRetentionPolicy.SubjectTypePreview, previewId, writeCt);
                        if (existing is not null)
                        {
                            return false;
                        }

                        // Re-check unreferenced under writer.
                        var refs = await recoveryStore.ProbePreviewReferencesAsync(
                            connection, transaction, previewId, writeCt);
                        var decision = ClassifyRetentionPolicy.EvaluateAbandon(
                            ClassifyStatusSubjectType.Preview, refs);
                        if (!decision.Allowed)
                        {
                            return false;
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
                        return true;
                    },
                    ct);
                if (tombstoned)
                {
                    expiredPreviewCount++;
                }
            }

            // 3) Abandoned subjects: remove any remaining subject-scoped recognized temps.
            var abandonedPayload = 0;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                // Count-driven cleanup of temps whose names match abandoned subject ids.
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT subject_id FROM abandonment_tombstone
                    ORDER BY subject_id ASC;
                    """;
                var subjectIds = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        subjectIds.Add(reader.GetString(0));
                    }
                }

                foreach (var subjectId in subjectIds)
                {
                    foreach (var name in artifactProtection.ListRecognizedTemporaryFileNames())
                    {
                        if (name.Contains(subjectId, StringComparison.Ordinal)
                            && artifactProtection.TryDeleteRecognizedTemporary(name))
                        {
                            abandonedPayload++;
                        }
                    }
                }
            }

            var occurredAt = ClassifyContractMapper.FormatUtc(timeProvider.GetUtcNow());
            var cleanupId = ClassifyContractMapper.NewRuleVersionId(timeProvider.GetUtcNow());
            var eventRow = ClassifyContractMapper.ToCleanupEventRow(
                cleanupId,
                policyVersion,
                removedTemporary,
                expiredPreviewCount,
                abandonedPayload,
                actorText,
                occurredAt);

            var publicResult = ClassifyContractMapper.ToCleanupResult(
                policyVersion,
                removedTemporary,
                expiredPreviewCount,
                abandonedPayload);

            return await stateStore.ExecuteWriteAsync(
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
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyCleanupResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException)
        {
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
