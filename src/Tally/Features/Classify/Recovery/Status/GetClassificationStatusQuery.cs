using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Recovery;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Feedback;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Recovery.Status;

/// <summary>
/// classify.status — bounded read-only durable projection across rule, validation, evaluation,
/// preview, apply, feedback, abandonment, and cleanup subjects (FR-CLASSIFY-STATUS-HISTORY).
/// Never rereads private corpus rows or LEDGER projections; never searches serialized payloads.
/// Unknown subjects return stable CLASSIFY-NOT-FOUND.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetClassificationStatusQuery
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly ClassificationValidationStore validationStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationApplyPreviewStore previewStore;
    private readonly ClassificationApplyRunStore runStore;
    private readonly ClassificationFeedbackStore feedbackStore;
    private readonly ClassificationRecoveryStore recoveryStore;
    private readonly TimeProvider timeProvider;

    public GetClassificationStatusQuery(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        RuleSetStore ruleSetStore,
        ClassificationValidationStore validationStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationApplyPreviewStore previewStore,
        ClassificationApplyRunStore runStore,
        ClassificationFeedbackStore feedbackStore,
        ClassificationRecoveryStore recoveryStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(validationStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(previewStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(recoveryStore);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.ruleSetStore = ruleSetStore;
        this.validationStore = validationStore;
        this.evaluationStore = evaluationStore;
        this.previewStore = previewStore;
        this.runStore = runStore;
        this.feedbackStore = feedbackStore;
        this.recoveryStore = recoveryStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyStatusResult>> HandleAsync(
        ClassifyStatusRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.SubjectId))
        {
            return CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var subjectId = input.SubjectId.Trim();
        var subjectType = input.SubjectType;

        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);

        return subjectType switch
        {
            ClassifyStatusSubjectType.Rule =>
                await StatusRuleAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Validation =>
                await StatusValidationAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Evaluation =>
                await StatusEvaluationAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Preview =>
                await StatusPreviewAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Apply =>
                await StatusApplyAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Feedback =>
                await StatusFeedbackAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Abandonment =>
                await StatusAbandonmentAsync(connection, subjectId, cancellationToken),
            ClassifyStatusSubjectType.Cleanup =>
                await StatusCleanupAsync(connection, subjectId, cancellationToken),
            _ => CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.InvalidInput)
        };
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusRuleAsync(
        SqliteConnection connection,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        var version = await ruleStore.GetRuleVersionAsync(
            connection, null, ruleVersionId, cancellationToken);
        if (version is null)
        {
            // Tombstoned subjects still report abandoned when the version row is gone? RESTRICT keeps rows.
            // Prefer stable not-found when no durable version exists.
            return NotFound();
        }

        var tombstoned = await recoveryStore.HasRuleVersionTombstoneAsync(
            connection, null, ruleVersionId, cancellationToken);
        var refs = await recoveryStore.ProbeRuleVersionReferencesAsync(
            connection, null, ruleVersionId, cancellationToken);
        var isReferenced = refs != ClassifyRetentionPolicy.ReferenceFlags.None
            && refs != ClassifyRetentionPolicy.ReferenceFlags.NotFound;

        var decision = SafeNextActionPolicy.ForRuleVersion(
            version.LifecycleState,
            tombstoned,
            isReferenced);
        // Active pointer is consulted only as durable metadata for lifecycle confirmation.
        _ = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);

        return Ok(ClassifyStatusSubjectType.Rule, ruleVersionId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusValidationAsync(
        SqliteConnection connection,
        string validationId,
        CancellationToken cancellationToken)
    {
        var run = await validationStore.GetRunAsync(connection, null, validationId, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        // Aggregate report is durable metadata only (no corpus reread).
        _ = await validationStore.GetReportAsync(connection, null, validationId, cancellationToken);

        var decision = SafeNextActionPolicy.ForValidationRun(run.LifecycleState);
        return Ok(ClassifyStatusSubjectType.Validation, validationId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusEvaluationAsync(
        SqliteConnection connection,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        var run = await evaluationStore.GetRunAsync(connection, null, evaluationId, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var decision = SafeNextActionPolicy.ForEvaluationRun(
            run.LifecycleState,
            run.ConflictCount);
        return Ok(ClassifyStatusSubjectType.Evaluation, evaluationId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusPreviewAsync(
        SqliteConnection connection,
        string previewId,
        CancellationToken cancellationToken)
    {
        var preview = await previewStore.GetPreviewAsync(connection, null, previewId, cancellationToken);
        if (preview is null)
        {
            return NotFound();
        }

        var tombstone = await recoveryStore.GetTombstoneAsync(
            connection,
            null,
            ClassifyRetentionPolicy.SubjectTypePreview,
            previewId,
            cancellationToken);
        var isTombstoned = tombstone is not null;
        var isExpired = IsExpired(preview.ExpiresAt);
        // Presence of any apply_run referencing this preview (durable only).
        var hasApply = await HasApplyForPreviewAsync(connection, previewId, cancellationToken);

        var decision = SafeNextActionPolicy.ForPreview(isTombstoned, isExpired, hasApply);
        return Ok(ClassifyStatusSubjectType.Preview, previewId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusApplyAsync(
        SqliteConnection connection,
        string applyId,
        CancellationToken cancellationToken)
    {
        var run = await runStore.GetRunAsync(connection, null, applyId, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var items = await runStore.ListItemsAsync(connection, null, applyId, cancellationToken);
        var totals = ClassifyContractMapper.ToApplyStatusTotals(items);
        var frontier = run.UnresolvedFrontier > 0
            ? run.UnresolvedFrontier
            : ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.ItemState));

        var decision = SafeNextActionPolicy.ForApplyRun(
            run.LifecycleState,
            frontier,
            totals.AppliedCount,
            totals.AlreadyAppliedCount,
            totals.FailedCount);
        return Ok(ClassifyStatusSubjectType.Apply, applyId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusFeedbackAsync(
        SqliteConnection connection,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        var feedback = await feedbackStore.GetFeedbackAsync(
            connection, null, feedbackId, cancellationToken);
        if (feedback is null)
        {
            return NotFound();
        }

        // Proposal state is durable metadata only — never reconstruct evidence.
        _ = await feedbackStore.GetProposalByFeedbackAsync(
            connection, null, feedbackId, cancellationToken);

        var decision = SafeNextActionPolicy.ForFeedback(feedback.DecisionType);
        return Ok(ClassifyStatusSubjectType.Feedback, feedbackId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusAbandonmentAsync(
        SqliteConnection connection,
        string subjectId,
        CancellationToken cancellationToken)
    {
        // SubjectId is tombstone_id (primary) or abandoned subject_id (fallback).
        var tombstone = await TryLoadTombstoneByIdAsync(connection, subjectId, cancellationToken);
        if (tombstone is null)
        {
            tombstone = await TryLoadTombstoneBySubjectIdAsync(connection, subjectId, cancellationToken);
        }

        if (tombstone is null)
        {
            return NotFound();
        }

        var decision = SafeNextActionPolicy.ForAbandonment(tombstone.RemovedPayloadCount);
        return Ok(ClassifyStatusSubjectType.Abandonment, subjectId, decision);
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusCleanupAsync(
        SqliteConnection connection,
        string cleanupId,
        CancellationToken cancellationToken)
    {
        if (!await recoveryStore.HasCleanupEventAsync(connection, null, cleanupId, cancellationToken))
        {
            return NotFound();
        }

        var removed = await LoadCleanupRemovedCountAsync(connection, cleanupId, cancellationToken);
        var decision = SafeNextActionPolicy.ForCleanup(removed);
        return Ok(ClassifyStatusSubjectType.Cleanup, cleanupId, decision);
    }

    private static CommandResult<ClassifyStatusResult> Ok(
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        SafeNextActionPolicy.Decision decision) =>
        CommandResult<ClassifyStatusResult>.Success(
            ClassifyContractMapper.ToStatusResult(subjectType, subjectId, decision));

    private static CommandResult<ClassifyStatusResult> NotFound() =>
        CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.NotFound);

    private bool IsExpired(string expiresAtUtc)
    {
        if (!DateTimeOffset.TryParse(
                expiresAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expires))
        {
            // Malformed expiry → treat as expired fail-closed for next-action (abandon).
            return true;
        }

        return expires <= timeProvider.GetUtcNow();
    }

    private static async Task<bool> HasApplyForPreviewAsync(
        SqliteConnection connection,
        string previewId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM apply_run WHERE preview_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", previewId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is not null and not DBNull;
    }

    private static async Task<ClassifyAbandonmentTombstoneRow?> TryLoadTombstoneByIdAsync(
        SqliteConnection connection,
        string tombstoneId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tombstone_id, subject_type, subject_id, reason, actor, abandoned_at, removed_payload_count
            FROM abandonment_tombstone
            WHERE tombstone_id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", tombstoneId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ClassifyRowMapper.MapAbandonment(reader)
            : null;
    }

    private static async Task<ClassifyAbandonmentTombstoneRow?> TryLoadTombstoneBySubjectIdAsync(
        SqliteConnection connection,
        string subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tombstone_id, subject_type, subject_id, reason, actor, abandoned_at, removed_payload_count
            FROM abandonment_tombstone
            WHERE subject_id = $id
            ORDER BY abandoned_at ASC, tombstone_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ClassifyRowMapper.MapAbandonment(reader)
            : null;
    }

    private static async Task<int> LoadCleanupRemovedCountAsync(
        SqliteConnection connection,
        string cleanupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT removed_artifact_count
            FROM cleanup_event
            WHERE cleanup_id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", cleanupId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull
            ? 0
            : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }
}
