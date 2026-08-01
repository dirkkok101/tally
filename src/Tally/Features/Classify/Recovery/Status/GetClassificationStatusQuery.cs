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
/// Unknown subjects → CLASSIFY-NOT-FOUND; inconsistent required metadata → CLASSIFY-INTEGRITY;
/// history overflow → CLASSIFY-RESOURCE-LIMIT with no partial result.
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
        var addressed = await ruleStore.GetRuleVersionAsync(
            connection, null, ruleVersionId, cancellationToken);
        if (addressed is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(addressed.RuleId)
            || string.IsNullOrWhiteSpace(addressed.LifecycleState)
            || string.IsNullOrWhiteSpace(addressed.CreatedBy)
            || string.IsNullOrWhiteSpace(addressed.CreatedAt))
        {
            return Integrity();
        }

        var allVersions = await ruleSetStore.ListAllRuleVersionsAsync(
            connection, null, cancellationToken);
        var family = allVersions
            .Where(v => string.Equals(v.RuleId, addressed.RuleId, StringComparison.Ordinal))
            .OrderBy(v => v.CreatedAt, StringComparer.Ordinal)
            .ThenBy(v => v.RuleVersionId, StringComparer.Ordinal)
            .ToArray();

        if (family.Length == 0)
        {
            return Integrity();
        }

        if (family.Length > ClassifyContractMapper.MaxStatusRuleVersionHistory)
        {
            return ResourceLimit();
        }

        var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
        var successorsByPrior = family
            .Where(v => !string.IsNullOrWhiteSpace(v.PriorVersionId))
            .GroupBy(v => v.PriorVersionId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(v => v.RuleVersionId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        var versionDetails = new List<ClassifyRuleStatusVersion>(family.Length);
        foreach (var version in family)
        {
            var tombstoned = await recoveryStore.HasRuleVersionTombstoneAsync(
                connection, null, version.RuleVersionId, cancellationToken);
            var membership = await ListRuleSetMembershipAsync(
                connection, version.RuleVersionId, cancellationToken);
            successorsByPrior.TryGetValue(version.RuleVersionId, out var successors);
            successors ??= Array.Empty<string>();
            versionDetails.Add(ClassifyContractMapper.ToRuleStatusVersion(
                version, membership, successors, tombstoned));
        }

        var addressedTombstoned = await recoveryStore.HasRuleVersionTombstoneAsync(
            connection, null, ruleVersionId, cancellationToken);
        var refs = await recoveryStore.ProbeRuleVersionReferencesAsync(
            connection, null, ruleVersionId, cancellationToken);
        var isReferenced = refs != ClassifyRetentionPolicy.ReferenceFlags.None
            && refs != ClassifyRetentionPolicy.ReferenceFlags.NotFound;
        var decision = SafeNextActionPolicy.ForRuleVersion(
            addressed.LifecycleState, addressedTombstoned, isReferenced);

        var detail = ClassifyContractMapper.ToRuleStatusDetail(
            addressed.RuleId,
            active?.RuleSetVersionId,
            versionDetails);

        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Rule, ruleVersionId, decision, rule: detail));
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

        if (string.IsNullOrWhiteSpace(run.CandidateFingerprint)
            || string.IsNullOrWhiteSpace(run.CorpusFingerprint)
            || string.IsNullOrWhiteSpace(run.ExpectedOutcomeFingerprint)
            || string.IsNullOrWhiteSpace(run.LifecycleState)
            || string.IsNullOrWhiteSpace(run.Actor)
            || string.IsNullOrWhiteSpace(run.StartedAt))
        {
            return Integrity();
        }

        var report = await validationStore.GetReportAsync(
            connection, null, validationId, cancellationToken);
        if (string.Equals(run.LifecycleState, ClassificationValidationStore.LifecycleCompleted, StringComparison.Ordinal)
            && report is null)
        {
            // Completed validation must retain aggregate report.
            return Integrity();
        }

        var staleness = ClassifyContractMapper.DeriveDurableStalenessState(
            run.LifecycleState, run.SnapshotExpiresAt, timeProvider.GetUtcNow());
        var decision = SafeNextActionPolicy.ForValidationRun(run.LifecycleState);
        var detail = ClassifyContractMapper.ToValidationStatusDetail(run, report, staleness);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Validation, validationId, decision, validation: detail));
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

        if (string.IsNullOrWhiteSpace(run.RuleSetVersionId)
            || string.IsNullOrWhiteSpace(run.NormalizationVersion)
            || string.IsNullOrWhiteSpace(run.StoreGenerationFingerprint)
            || string.IsNullOrWhiteSpace(run.SnapshotId)
            || string.IsNullOrWhiteSpace(run.SnapshotExpiresAt)
            || string.IsNullOrWhiteSpace(run.LifecycleState)
            || string.IsNullOrWhiteSpace(run.Actor)
            || string.IsNullOrWhiteSpace(run.CreatedAt)
            || run.InputCount < 0
            || run.SuggestionCount < 0
            || run.NoSuggestionCount < 0
            || run.ConflictCount < 0
            || run.StaleCount < 0)
        {
            return Integrity();
        }

        var fingerprint = ClassifyContractMapper.ComputeEvaluationStatusFingerprint(run);
        if (fingerprint.Length != 64)
        {
            return Integrity();
        }

        var staleness = ClassifyContractMapper.DeriveDurableStalenessState(
            run.LifecycleState, run.SnapshotExpiresAt, timeProvider.GetUtcNow());
        var decision = SafeNextActionPolicy.ForEvaluationRun(run.LifecycleState, run.ConflictCount);
        var detail = ClassifyContractMapper.ToEvaluationStatusDetail(run, fingerprint, staleness);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Evaluation, evaluationId, decision, evaluation: detail));
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

        if (string.IsNullOrWhiteSpace(preview.EvaluationId)
            || string.IsNullOrWhiteSpace(preview.EvaluationFingerprint)
            || string.IsNullOrWhiteSpace(preview.SelectionHash)
            || string.IsNullOrWhiteSpace(preview.ExpiresAt)
            || string.IsNullOrWhiteSpace(preview.Actor)
            || string.IsNullOrWhiteSpace(preview.CreatedAt)
            || preview.EvaluationFingerprint.Length != 64
            || preview.SelectionHash.Length != 64)
        {
            return Integrity();
        }

        var tombstone = await recoveryStore.GetTombstoneAsync(
            connection, null, ClassifyRetentionPolicy.SubjectTypePreview, previewId, cancellationToken);
        var isTombstoned = tombstone is not null;
        var isExpired = IsExpired(preview.ExpiresAt);
        var hasApply = await HasApplyForPreviewAsync(connection, previewId, cancellationToken);
        var lifecycle = ClassifyContractMapper.DerivePreviewLifecycle(isTombstoned, isExpired);
        var decision = SafeNextActionPolicy.ForPreview(isTombstoned, isExpired, hasApply);
        var detail = ClassifyContractMapper.ToPreviewStatusDetail(preview, lifecycle);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Preview, previewId, decision, preview: detail));
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

        if (string.IsNullOrWhiteSpace(run.PreviewId)
            || string.IsNullOrWhiteSpace(run.RequestFingerprint)
            || run.RequestFingerprint.Length != 64
            || string.IsNullOrWhiteSpace(run.LifecycleState)
            || string.IsNullOrWhiteSpace(run.Actor)
            || string.IsNullOrWhiteSpace(run.StartedAt)
            || run.UnresolvedFrontier < 0)
        {
            return Integrity();
        }

        var items = await runStore.ListItemsAsync(connection, null, applyId, cancellationToken);
        if (items.Count > ClassifyContractMapper.MaxStatusRuleVersionHistory * 20)
        {
            // Hard upper bound on item expansion — fail closed without partial totals.
            return ResourceLimit();
        }

        var totals = ClassifyContractMapper.ToApplyStatusTotals(items);
        var frontier = run.UnresolvedFrontier > 0
            ? run.UnresolvedFrontier
            : ApplyReplayPolicy.ComputeUnresolvedFrontier(items.Select(i => i.ItemState));
        var (replaySafe, resumeSafe) = ClassifyContractMapper.ToApplySafetyFlags(
            run.LifecycleState, frontier);
        var decision = SafeNextActionPolicy.ForApplyRun(
            run.LifecycleState,
            frontier,
            totals.AppliedCount,
            totals.AlreadyAppliedCount,
            totals.FailedCount);
        var detail = ClassifyContractMapper.ToApplyStatusDetail(
            run, totals, frontier, replaySafe, resumeSafe);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Apply, applyId, decision, apply: detail));
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

        if (string.IsNullOrWhiteSpace(feedback.OutcomeId)
            || string.IsNullOrWhiteSpace(feedback.DecisionType)
            || string.IsNullOrWhiteSpace(feedback.Actor)
            || string.IsNullOrWhiteSpace(feedback.OccurredAt)
            || string.IsNullOrWhiteSpace(feedback.RuleSetVersionId))
        {
            return Integrity();
        }

        var proposal = await feedbackStore.GetProposalByFeedbackAsync(
            connection, null, feedbackId, cancellationToken);

        var ruleVersionIds = new List<string>();
        if (proposal is not null && !string.IsNullOrWhiteSpace(proposal.SourceRuleVersionId))
        {
            ruleVersionIds.Add(proposal.SourceRuleVersionId);
        }

        var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, feedback.RuleSetVersionId, cancellationToken);
        if (members.Count > ClassifyContractMapper.MaxStatusRuleVersionHistory)
        {
            return ResourceLimit();
        }

        foreach (var id in members.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!ruleVersionIds.Contains(id, StringComparer.Ordinal))
            {
                ruleVersionIds.Add(id);
            }
        }

        if (ruleVersionIds.Count > ClassifyContractMapper.MaxStatusRuleVersionHistory)
        {
            return ResourceLimit();
        }

        var decision = SafeNextActionPolicy.ForFeedback(feedback.DecisionType);
        var detail = ClassifyContractMapper.ToFeedbackStatusDetail(
            feedback, proposal, ruleVersionIds);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Feedback, feedbackId, decision, feedback: detail));
    }

    private async Task<CommandResult<ClassifyStatusResult>> StatusAbandonmentAsync(
        SqliteConnection connection,
        string subjectId,
        CancellationToken cancellationToken)
    {
        var tombstone = await TryLoadTombstoneByIdAsync(connection, subjectId, cancellationToken)
            ?? await TryLoadTombstoneBySubjectIdAsync(connection, subjectId, cancellationToken);
        if (tombstone is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(tombstone.TombstoneId)
            || string.IsNullOrWhiteSpace(tombstone.SubjectType)
            || string.IsNullOrWhiteSpace(tombstone.SubjectId)
            || string.IsNullOrWhiteSpace(tombstone.Actor)
            || string.IsNullOrWhiteSpace(tombstone.AbandonedAt)
            || tombstone.RemovedPayloadCount < 0)
        {
            return Integrity();
        }

        var decision = SafeNextActionPolicy.ForAbandonment(tombstone.RemovedPayloadCount);
        var detail = ClassifyContractMapper.ToAbandonmentStatusDetail(tombstone);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Abandonment, subjectId, decision, abandonment: detail));
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

        var row = await LoadCleanupEventAsync(connection, cleanupId, cancellationToken);
        if (row is null
            || string.IsNullOrWhiteSpace(row.Value.PolicyVersion)
            || string.IsNullOrWhiteSpace(row.Value.Actor)
            || string.IsNullOrWhiteSpace(row.Value.OccurredAt)
            || row.Value.RemovedArtifactCount < 0
            || row.Value.RetainedArtifactCount < 0)
        {
            return Integrity();
        }

        var decision = SafeNextActionPolicy.ForCleanup(row.Value.RemovedArtifactCount);
        var detail = ClassifyContractMapper.ToCleanupStatusDetail(
            cleanupId,
            row.Value.PolicyVersion,
            row.Value.Actor,
            row.Value.OccurredAt,
            row.Value.RemovedArtifactCount,
            row.Value.RetainedArtifactCount,
            row.Value.RecognizedRemovedCount,
            row.Value.ExpiredPreviewCount,
            row.Value.AbandonedPayloadCount);
        return Ok(ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Cleanup, cleanupId, decision, cleanup: detail));
    }

    private static CommandResult<ClassifyStatusResult> Ok(ClassifyStatusResult result) =>
        CommandResult<ClassifyStatusResult>.Success(result);

    private static CommandResult<ClassifyStatusResult> NotFound() =>
        CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.NotFound);

    private static CommandResult<ClassifyStatusResult> Integrity() =>
        CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.Integrity);

    private static CommandResult<ClassifyStatusResult> ResourceLimit() =>
        CommandResult<ClassifyStatusResult>.Failure(ClassifyErrors.ResourceLimit);

    private bool IsExpired(string expiresAtUtc)
    {
        if (!DateTimeOffset.TryParse(
                expiresAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expires))
        {
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

    private static async Task<IReadOnlyList<string>> ListRuleSetMembershipAsync(
        SqliteConnection connection,
        string ruleVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rule_set_version_id
            FROM rule_set_member
            WHERE rule_version_id = $id
            ORDER BY rule_set_version_id ASC;
            """;
        command.Parameters.AddWithValue("$id", ruleVersionId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
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

    private static async Task<(
        string PolicyVersion,
        string Actor,
        string OccurredAt,
        int RemovedArtifactCount,
        int RetainedArtifactCount,
        int RecognizedRemovedCount,
        int ExpiredPreviewCount,
        int AbandonedPayloadCount)?> LoadCleanupEventAsync(
        SqliteConnection connection,
        string cleanupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT policy_version, actor, occurred_at,
                   removed_artifact_count, retained_artifact_count,
                   recognized_removed_count, expired_preview_count, abandoned_payload_count
            FROM cleanup_event
            WHERE cleanup_id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", cleanupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }
}
