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

namespace Tally.Features.Classify.Recovery.Abandon;

/// <summary>
/// classify.abandon — tombstone unreferenced subjects after reversible quarantine staging.
/// Never deletes recognized artifacts before durable tombstone + terminal idempotency commit.
/// Replay performs no filesystem mutation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AbandonClassificationStateCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRecoveryStore recoveryStore;
    private readonly ClassifyArtifactProtection artifactProtection;
    private readonly TimeProvider timeProvider;

    public AbandonClassificationStateCommand(
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

    public async Task<CommandResult<ClassifyAbandonResult>> HandleAsync(
        ClassifyAbandonRequest input,
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
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.SubjectId)
            || !ClassifyContractMapper.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (ClassifyRetentionPolicy.IsAlwaysRestrictedSubjectType(input.SubjectType)
            || !ClassifyRetentionPolicy.IsAbandonableSubjectType(input.SubjectType))
        {
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Lifecycle);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var subjectId = input.SubjectId.Trim();
        var subjectTypeWire = ClassifyRetentionPolicy.FormatSubjectType(input.SubjectType);

        var fingerprintElement = ClassifyContractMapper.ToAbandonFingerprintElement(
            ClassifyOperationIds.ContractVersion, input.SubjectType, subjectId, reason);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.Abandon,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        // Replay: return prior terminal result with no filesystem mutation.
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

            ClassifyRetentionPolicy.ReferenceFlags references;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                var existingTombstone = await recoveryStore.GetTombstoneAsync(
                    connection, null, subjectTypeWire, subjectId, ct);
                if (existingTombstone is not null)
                {
                    return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Lifecycle);
                }

                references = await ProbeAsync(connection, null, input.SubjectType, subjectId, ct);
            }

            var decision = ClassifyRetentionPolicy.EvaluateAbandon(input.SubjectType, references);
            if (!decision.Allowed)
            {
                return CommandResult<ClassifyAbandonResult>.Failure(
                    decision.ErrorCode ?? ClassifyErrors.Lifecycle);
            }

            var now = timeProvider.GetUtcNow();
            var abandonedAt = ClassifyContractMapper.FormatUtc(now);
            var tombstoneId = ClassifyContractMapper.NewRuleVersionId(now);

            // Subject-scoped temps: stage only unlocked removable; locked remain retained.
            var subjectTemps = artifactProtection.ListRecognizedTemporaryFileNames()
                .Where(n => n.Contains(subjectId, StringComparison.Ordinal))
                .ToArray();
            var partition = artifactProtection.PartitionRecognizedTemporaries(subjectTemps);
            if (partition.Removable.Count > 0)
            {
                quarantine = artifactProtection.TryStageRecognizedTemporaries(
                    tombstoneId, "abandon", partition.Removable);
                if (quarantine is null)
                {
                    return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Integrity);
                }
            }
            else
            {
                quarantine = artifactProtection.TryStageRecognizedTemporaries(
                    tombstoneId, "abandon", Array.Empty<string>());
            }

            var removedPayload = quarantine?.StagedCount ?? 0;
            var tombstone = ClassifyContractMapper.ToTombstoneRow(
                tombstoneId,
                input.SubjectType,
                subjectId,
                reason,
                actorText,
                abandonedAt,
                removedPayload);
            var publicResult = ClassifyContractMapper.ToAbandonResult(
                input.SubjectType, subjectId, abandoned: true);

            CommandResult<ClassifyAbandonResult> writeResult;
            try
            {
                writeResult = await stateStore.ExecuteWriteAsync(
                    async (connection, transaction, writeCt) =>
                    {
                        var existing = await idempotencyStore.FindAsync(
                            connection, transaction, idempotencyKey, writeCt);
                        var lookup = idempotencyStore.Resolve(
                            existing,
                            ClassifyOperationIds.Abandon,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint);
                        switch (lookup.Disposition)
                        {
                            case ClassifyIdempotencyDisposition.Replay:
                                return ReplayOrIntegrity(lookup.Record!);
                            case ClassifyIdempotencyDisposition.Conflict:
                                return CommandResult<ClassifyAbandonResult>.Failure(
                                    ClassifyErrors.IdempotencyConflict);
                            case ClassifyIdempotencyDisposition.Miss:
                                break;
                            default:
                                return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Unexpected);
                        }

                        var live = await ProbeAsync(connection, transaction, input.SubjectType, subjectId, writeCt);
                        var liveDecision = ClassifyRetentionPolicy.EvaluateAbandon(input.SubjectType, live);
                        if (!liveDecision.Allowed)
                        {
                            return CommandResult<ClassifyAbandonResult>.Failure(
                                liveDecision.ErrorCode ?? ClassifyErrors.Lifecycle);
                        }

                        if (await recoveryStore.GetTombstoneAsync(
                                connection, transaction, subjectTypeWire, subjectId, writeCt) is not null)
                        {
                            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Lifecycle);
                        }

                        await recoveryStore.InsertTombstoneAsync(connection, transaction, tombstone, writeCt);

                        if (input.SubjectType == ClassifyStatusSubjectType.Evaluation)
                        {
                            _ = await recoveryStore.TryAbandonEvaluationLifecycleAsync(
                                connection, transaction, subjectId, writeCt);
                        }
                        else if (input.SubjectType == ClassifyStatusSubjectType.Validation)
                        {
                            _ = await recoveryStore.TryAbandonValidationLifecycleAsync(
                                connection, transaction, subjectId, abandonedAt, writeCt);
                        }

                        await idempotencyStore.CommitAsync(
                            connection,
                            transaction,
                            new ClassifyOperationIdempotencyRow(
                                idempotencyKey,
                                ClassifyOperationIds.Abandon,
                                ClassifyOperationIds.ContractVersion,
                                requestFingerprint,
                                SerializeResult(publicResult),
                                abandonedAt),
                            writeCt);

                        return CommandResult<ClassifyAbandonResult>.Success(publicResult);
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
                // Final deletion only with durable tombstone authority (not manifest.Committed alone).
                var durable = false;
                await using (var connection = await stateStore.OpenMigratedAsync(ct))
                {
                    durable = await recoveryStore.HasTombstoneIdAsync(
                        connection, null, tombstoneId, ct);
                }

                quarantine?.FinalizeWithDurableAuthority(durable);
                quarantine = null;
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
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            quarantine?.RestoreAndDiscard();
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException)
        {
            quarantine?.RestoreAndDiscard();
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<ClassifyRetentionPolicy.ReferenceFlags> ProbeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        CancellationToken cancellationToken) =>
        subjectType switch
        {
            ClassifyStatusSubjectType.Rule =>
                await recoveryStore.ProbeRuleVersionReferencesAsync(connection, transaction, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Validation =>
                await recoveryStore.ProbeValidationReferencesAsync(connection, transaction, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Evaluation =>
                await recoveryStore.ProbeEvaluationReferencesAsync(connection, transaction, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Preview =>
                await recoveryStore.ProbePreviewReferencesAsync(connection, transaction, subjectId, cancellationToken),
            _ => ClassifyRetentionPolicy.ReferenceFlags.NotFound
        };

    private async Task<CommandResult<ClassifyAbandonResult>?> TryProbeAsync(
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
                ClassifyOperationIds.Abandon,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyAbandonResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyAbandonResult);
            return result is null
                ? CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyAbandonResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyAbandonResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyAbandonResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyAbandonResult);
}
