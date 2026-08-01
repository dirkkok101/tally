using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Evaluation.Evaluate;

/// <summary>
/// classify.evaluate vertical slice (FR-CLASSIFY-DETERMINISTIC-EVALUATION / TASK-CLASSIFY-RULEBOOK-EVALUATION-WORKFLOW).
/// Obtains one complete <see cref="ClassificationEvaluationInput"/> before creating an evaluation ID,
/// runs the pure <see cref="ClassificationEngine"/> against the immutable active rule set, and persists
/// outcomes + bounded match evidence in one SQLite transaction. Never mutates Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class EvaluateClassificationCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationEvaluationInputLoader inputLoader;
    private readonly RuleSetStore ruleSetStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly TimeProvider timeProvider;

    public EvaluateClassificationCommand(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationEvaluationInputLoader inputLoader,
        RuleSetStore ruleSetStore,
        ClassificationRuleStore ruleStore,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(inputLoader);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.inputLoader = inputLoader;
        this.ruleSetStore = ruleSetStore;
        this.ruleStore = ruleStore;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyEvaluateResult>> HandleAsync(
        ClassifyEvaluateRequest input,
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
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);

        var fingerprintElement = ClassifyContractMapper.ToEvaluateFingerprintElement(
            ClassifyOperationIds.ContractVersion);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.Evaluate,
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
            // Complete compatible projection BEFORE any evaluation ID is allocated.
            var loaded = await inputLoader.LoadAsync(actor, ct);
            if (!loaded.IsSuccess || loaded.Value is null)
            {
                return CommandResult<ClassifyEvaluateResult>.Failure(
                    loaded.ErrorCode ?? ClassifyErrors.LedgerUnavailable);
            }

            var projectionInput = loaded.Value;

            // Active immutable rule set (membership authority is pointer + members; never re-infer eligibility).
            ClassifyActiveRuleSetPointer? activePointer;
            IReadOnlyList<string> memberIds;
            IReadOnlyList<ActiveRuleVersion> engineRules;
            string normalizationVersion;
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                activePointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, ct);
                if (activePointer is null)
                {
                    return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Lifecycle);
                }

                memberIds = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                    connection, null, activePointer.RuleSetVersionId, ct);

                if (!ClassifyContractMapper.IsRuleCountWithinBound(
                        memberIds.Count,
                        ClassifyOperationModule.V1Limits.MaxRuleCount))
                {
                    return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.ResourceLimit);
                }

                var rules = new List<ActiveRuleVersion>(memberIds.Count);
                string? sharedNormalization = null;
                foreach (var memberId in memberIds.OrderBy(id => id, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var version = await ruleStore.GetRuleVersionAsync(connection, null, memberId, ct);
                    if (version is null)
                    {
                        return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.RuleVersionNotFound);
                    }

                    if (sharedNormalization is null)
                    {
                        sharedNormalization = version.NormalizationVersion;
                    }
                    else if (!string.Equals(sharedNormalization, version.NormalizationVersion, StringComparison.Ordinal))
                    {
                        return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity);
                    }

                    if (!string.Equals(
                            version.NormalizationVersion,
                            NormalizationDescriptor.V1.Version,
                            StringComparison.Ordinal))
                    {
                        return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.UnsupportedVersion);
                    }

                    var conditions = await ruleStore.ListConditionsAsync(connection, null, memberId, ct);
                    if (conditions.Count == 0)
                    {
                        return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity);
                    }

                    rules.Add(ClassifyContractMapper.ToActiveRuleVersion(
                        version.RuleVersionId,
                        version.CategoryId,
                        conditions));
                }

                engineRules = rules;
                normalizationVersion = sharedNormalization ?? NormalizationDescriptor.V1.Version;
            }

            if (!ClassifyContractMapper.TryMapEvaluationItems(
                    projectionInput,
                    out var evaluationItems,
                    out var mapError))
            {
                return CommandResult<ClassifyEvaluateResult>.Failure(mapError ?? ClassifyErrors.Integrity);
            }

            var activeCategoryIds = projectionInput.ActiveCategories
                .Where(c => string.Equals(c.LifecycleState, "active", StringComparison.Ordinal))
                .Select(c => c.CategoryId)
                .ToHashSet(StringComparer.Ordinal);

            var fingerprint = ClassifyContractMapper.CreateEvaluationFingerprint(
                projectionInput,
                activePointer!.RuleSetVersionId,
                normalizationVersion);

            var evaluation = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
                fingerprint,
                evaluationItems,
                engineRules,
                activeCategoryIds));

            if (evaluation.InputCount != projectionInput.TotalCount
                || evaluation.SuggestionCount
                    + evaluation.NoSuggestionCount
                    + evaluation.ConflictCount
                    + evaluation.StaleCount != evaluation.InputCount)
            {
                return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity);
            }

            if (!ClassifyContractMapper.IsEvidenceWithinBound(
                    evaluation,
                    ClassifyOperationModule.V1Limits.MaxEvidenceRowCount))
            {
                return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            if (Process.GetCurrentProcess().WorkingSet64 > ClassifyOperationModule.V1Limits.MaxMemoryBytes)
            {
                return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            var createdAt = timeProvider.GetUtcNow();
            var evaluationId = ClassifyContractMapper.NewRuleVersionId(createdAt);
            var createdAtUtc = ClassifyContractMapper.FormatUtc(createdAt);
            var publicResult = ClassifyContractMapper.ToEvaluateResult(
                evaluationId,
                activePointer.RuleSetVersionId,
                normalizationVersion,
                projectionInput.SnapshotFingerprint,
                evaluation);

            var outcomeRows = new List<PersistedEvaluationOutcome>(evaluation.Outcomes.Count);
            var tick = 0;
            foreach (var outcome in evaluation.Outcomes)
            {
                var outcomeId = ClassifyContractMapper.NewRuleVersionId(createdAt.AddTicks(++tick));
                outcomeRows.Add(new PersistedEvaluationOutcome(
                    ClassifyContractMapper.ToOutcomeRow(outcomeId, evaluationId, outcome),
                    outcome.Evidence));
            }

            var runRow = ClassifyContractMapper.ToEvaluationRunRow(
                evaluationId,
                idempotencyKey,
                activePointer.RuleSetVersionId,
                normalizationVersion,
                projectionInput,
                evaluation,
                actorText,
                createdAtUtc);

            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    var existing = await idempotencyStore.FindAsync(
                        connection, transaction, idempotencyKey, writeCt);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.Evaluate,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyEvaluateResult>.Failure(
                                ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    // Idempotency terminal must exist before evaluation_run FK can reference the key.
                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.Evaluate,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            SerializeResult(publicResult),
                            createdAtUtc),
                        writeCt);

                    await evaluationStore.PersistCompletedAsync(
                        connection,
                        transaction,
                        runRow,
                        outcomeRows,
                        writeCt);

                    // Defensive accounting after write.
                    var storedOutcomes = await evaluationStore.ListOutcomesAsync(
                        connection, transaction, evaluationId, writeCt);
                    if (storedOutcomes.Count != evaluation.InputCount)
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: partial evaluation outcomes are not permitted.");
                    }

                    return CommandResult<ClassifyEvaluateResult>.Success(publicResult);
                },
                ct);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("partial evaluation", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("input_count", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyEvaluateResult>?> TryProbeAsync(
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
                ClassifyOperationIds.Evaluate,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyEvaluateResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyEvaluateResult);
            return result is null
                ? CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyEvaluateResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyEvaluateResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyEvaluateResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyEvaluateResult);
}
