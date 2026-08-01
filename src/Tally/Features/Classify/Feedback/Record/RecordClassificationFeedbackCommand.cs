using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Feedback;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Feedback;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Feedback.Record;

/// <summary>
/// classify.feedback.record vertical slice
/// (FR-CLASSIFY-CORRECTION-FEEDBACK / TASK-CLASSIFY-RULEBOOK-FEEDBACK-PROPOSALS).
/// Appends exact provenance feedback and at most one non-active smallest-scope proposal.
/// Never rewrites history, broadens, activates, or reconstructs missing MatchEvidence.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RecordClassificationFeedbackCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationFeedbackStore feedbackStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly TimeProvider timeProvider;

    public RecordClassificationFeedbackCommand(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationFeedbackStore feedbackStore,
        ClassificationRuleStore ruleStore,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.feedbackStore = feedbackStore;
        this.ruleStore = ruleStore;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyFeedbackRecordResult>> HandleAsync(
        ClassifyFeedbackRecordRequest input,
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
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.OutcomeId))
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (!ClassifyContractMapper.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var outcomeId = input.OutcomeId.Trim();

        var fingerprintElement = ClassifyContractMapper.ToFeedbackFingerprintElement(
            ClassifyOperationIds.ContractVersion,
            outcomeId,
            input.Decision,
            reason,
            input.LedgerAllocationRefs);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.FeedbackRecord,
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
            ClassifyOutcomeRow outcome;
            ClassifyEvaluationRunRow run;
            IReadOnlyList<ClassifyMatchEvidenceRow> evidence;
            Dictionary<string, ClassifyRuleVersionRow> sourceRules;
            string? priorAlloc;
            string? resultingAlloc;
            string? resultingCategoryId;
            bool correctionAllocationsComplete;
            (string? PriorAllocationId, string? ResultingAllocationId, string? CategoryId)? authoritativeCorrection;

            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                var loadedOutcome = await feedbackStore.GetOutcomeAsync(connection, null, outcomeId, ct);
                if (loadedOutcome is null)
                {
                    return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.OutcomeNotFound);
                }

                outcome = loadedOutcome;
                var loadedRun = await evaluationStore.GetRunAsync(
                    connection, null, outcome.EvaluationId, ct);
                if (loadedRun is null)
                {
                    return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.EvaluationNotFound);
                }

                if (!string.Equals(
                        loadedRun.LifecycleState,
                        ClassifyContractMapper.EvaluationLifecycleCompleted,
                        StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Lifecycle);
                }

                run = loadedRun;
                evidence = await evaluationStore.ListEvidenceForOutcomeAsync(
                    connection, null, outcome.OutcomeId, ct);

                sourceRules = new Dictionary<string, ClassifyRuleVersionRow>(StringComparer.Ordinal);
                foreach (var ruleId in evidence
                             .Select(e => e.RuleVersionId)
                             .Where(id => !string.IsNullOrWhiteSpace(id))
                             .Distinct(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var version = await ruleStore.GetRuleVersionAsync(connection, null, ruleId, ct);
                    if (version is not null)
                    {
                        sourceRules[ruleId] = version;
                    }
                }

                // Resolve correction allocations from owner refs or durable apply_item — never invent.
                priorAlloc = null;
                resultingAlloc = null;
                resultingCategoryId = null;
                correctionAllocationsComplete = false;
                authoritativeCorrection = null;
                if (input.Decision == ClassifyFeedbackDecision.Corrected)
                {
                    var applied = await feedbackStore.FindAppliedCorrectionForOutcomeAsync(
                        connection, null, outcome.OutcomeId, outcome.TransactionId, ct);
                    if (!ClassifyContractMapper.TryResolveCorrectionAllocations(
                            input.LedgerAllocationRefs,
                            applied?.PriorAllocationId,
                            applied?.ResultingAllocationId,
                            out priorAlloc,
                            out resultingAlloc,
                            out var allocError))
                    {
                        return CommandResult<ClassifyFeedbackRecordResult>.Failure(
                            allocError ?? ClassifyErrors.InvalidInput);
                    }

                    correctionAllocationsComplete = applied is not null
                        && !string.IsNullOrWhiteSpace(applied.Value.PriorAllocationId)
                        && !string.IsNullOrWhiteSpace(applied.Value.ResultingAllocationId)
                        && string.Equals(priorAlloc, applied.Value.PriorAllocationId, StringComparison.Ordinal)
                        && string.Equals(resultingAlloc, applied.Value.ResultingAllocationId, StringComparison.Ordinal);
                    authoritativeCorrection = correctionAllocationsComplete ? applied : null;
                    resultingCategoryId = correctionAllocationsComplete ? applied?.CategoryId : null;
                }
                else if (input.LedgerAllocationRefs is { Count: > 0 })
                {
                    // Allocation provenance belongs to completed corrections only.
                    return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.InvalidInput);
                }
            }

            ClassificationOutcomeKind outcomeKind;
            try
            {
                outcomeKind = ClassifyContractMapper.ParseStoredOutcomeType(outcome.OutcomeType);
            }
            catch (ArgumentOutOfRangeException)
            {
                return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Integrity);
            }

            var evidenceAvailable = FeedbackProposalBuilder.IsEvidenceAvailable(outcomeKind, evidence);

            var proposal = FeedbackProposalBuilder.Build(new FeedbackProposalBuilder.Input(
                input.Decision,
                outcomeKind,
                evidenceAvailable,
                evidence,
                sourceRules,
                resultingCategoryId,
                correctionAllocationsComplete));

            var now = timeProvider.GetUtcNow();
            var occurredAt = ClassifyContractMapper.FormatUtc(now);
            var feedbackId = ClassifyContractMapper.NewRuleVersionId(now);
            var proposalId = FeedbackProposalBuilder.IsActiveProposal(proposal.Kind)
                ? ClassifyContractMapper.NewRuleVersionId(now.AddTicks(1))
                : null;

            var feedbackRow = ClassifyContractMapper.ToFeedbackRow(
                feedbackId,
                outcome.OutcomeId,
                outcome.TransactionId,
                run.EvaluationId,
                run.NormalizationVersion,
                run.RuleSetVersionId,
                input.Decision,
                priorAlloc,
                resultingAlloc,
                reason,
                actorText,
                occurredAt);

            var proposalRow = proposalId is null
                ? null
                : ClassifyContractMapper.ToProposalRow(proposalId, feedbackId, proposal, occurredAt);

            // Public result never exposes descriptions, tokens, or full proposal bodies.
            var publicResult = ClassifyContractMapper.ToFeedbackResult(
                feedbackId, outcome.OutcomeId, proposalId);

            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    var existing = await idempotencyStore.FindAsync(
                        connection, transaction, idempotencyKey, writeCt);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.FeedbackRecord,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyFeedbackRecordResult>.Failure(
                                ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    // Rebind outcome-scoped correction authority under the mutation lock.
                    // A later correction on the same outcome must not race this proposal.
                    if (correctionAllocationsComplete && authoritativeCorrection is not null)
                    {
                        var rebound = await feedbackStore.FindAppliedCorrectionForOutcomeAsync(
                            connection, transaction, outcome.OutcomeId, outcome.TransactionId, writeCt);
                        if (rebound is null
                            || !string.Equals(rebound.Value.PriorAllocationId, authoritativeCorrection.Value.PriorAllocationId, StringComparison.Ordinal)
                            || !string.Equals(rebound.Value.ResultingAllocationId, authoritativeCorrection.Value.ResultingAllocationId, StringComparison.Ordinal)
                            || !string.Equals(rebound.Value.CategoryId, authoritativeCorrection.Value.CategoryId, StringComparison.Ordinal))
                        {
                            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Stale);
                        }
                    }

                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.FeedbackRecord,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            SerializeResult(publicResult),
                            occurredAt),
                        writeCt);

                    // Defensive: proposal must remain draft/feedback_derived and never active.
                    if (proposalRow is not null
                        && (!string.Equals(proposalRow.LifecycleState, FeedbackProposalBuilder.LifecycleDraft, StringComparison.Ordinal)
                            || !string.Equals(proposalRow.RuleOrigin, FeedbackProposalBuilder.RuleOriginFeedbackDerived, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: feedback proposal must stay non-active draft.");
                    }

                    await feedbackStore.PersistAsync(
                        connection, transaction, feedbackRow, proposalRow, writeCt);

                    return CommandResult<ClassifyFeedbackRecordResult>.Success(publicResult);
                },
                ct);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("feedback", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("proposal", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyFeedbackRecordResult>?> TryProbeAsync(
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
                ClassifyOperationIds.FeedbackRecord,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyFeedbackRecordResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyFeedbackRecordResult);
            return result is null
                ? CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyFeedbackRecordResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyFeedbackRecordResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyFeedbackRecordResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyFeedbackRecordResult);
}
