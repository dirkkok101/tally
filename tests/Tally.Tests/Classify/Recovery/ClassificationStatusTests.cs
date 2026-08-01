using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap.Features;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Recovery;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Recovery.Status;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Xunit;

namespace Tally.Tests.Classify.Recovery;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-STATUS-WORKFLOW / bd-3tpm — bounded status state matrix.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationStatusTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-status-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "status", "run-01");
    private ClassifyRecoveryServices services = null!;
    private GetClassificationStatusQuery status = null!;
    private ClassificationRecoveryStore recovery = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        services = await ClassifyRecoveryExtensions.CreateServicesAsync(root);
        status = services.Status;
        recovery = services.RecoveryStore;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Pure policy ─────────────────────────────────────────────────────────

    [Fact]
    public void Policy_known_next_actions_closed_set()
    {
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.Retry));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.Resume));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.ReEvaluate));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.Correct));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.Abandon));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.Cleanup));
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(SafeNextActionPolicy.None));
        Assert.False(SafeNextActionPolicy.IsKnownNextAction("classify.evaluate"));
        Assert.False(SafeNextActionPolicy.IsKnownNextAction(null));
    }

    [Fact]
    public void Policy_rule_draft_unreferenced_suggests_abandon()
    {
        var d = SafeNextActionPolicy.ForRuleVersion("draft", isTombstoned: false, isReferenced: false);
        Assert.Equal(SafeNextActionPolicy.Abandon, d.NextSafeOperationId);
        Assert.False(d.MutationMayHaveOccurred);
    }

    [Fact]
    public void Policy_rule_tombstoned_suggests_cleanup()
    {
        var d = SafeNextActionPolicy.ForRuleVersion("draft", isTombstoned: true, isReferenced: false);
        Assert.Equal(SafeNextActionPolicy.LifecycleAbandoned, d.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Cleanup, d.NextSafeOperationId);
    }

    [Fact]
    public void Policy_validation_running_is_retry()
    {
        var d = SafeNextActionPolicy.ForValidationRun("running");
        Assert.Equal(SafeNextActionPolicy.Retry, d.NextSafeOperationId);
    }

    [Fact]
    public void Policy_evaluation_conflict_completed_is_correct()
    {
        var d = SafeNextActionPolicy.ForEvaluationRun("completed", conflictCount: 2);
        Assert.Equal(SafeNextActionPolicy.Correct, d.NextSafeOperationId);
    }

    [Fact]
    public void Policy_evaluation_failed_is_re_evaluate()
    {
        var d = SafeNextActionPolicy.ForEvaluationRun("failed", conflictCount: 0);
        Assert.Equal(SafeNextActionPolicy.ReEvaluate, d.NextSafeOperationId);
    }

    [Fact]
    public void Policy_preview_expired_is_abandon()
    {
        var d = SafeNextActionPolicy.ForPreview(isTombstoned: false, isExpired: true, hasApplyRun: false);
        Assert.Equal(SafeNextActionPolicy.LifecycleExpired, d.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Abandon, d.NextSafeOperationId);
    }

    [Fact]
    public void Policy_apply_running_with_frontier_is_resume()
    {
        var d = SafeNextActionPolicy.ForApplyRun(
            "running", unresolvedFrontier: 3, appliedCount: 1, alreadyAppliedCount: 0, failedCount: 0);
        Assert.Equal(SafeNextActionPolicy.Resume, d.NextSafeOperationId);
        Assert.True(d.MutationMayHaveOccurred);
    }

    [Fact]
    public void Policy_apply_completed_no_mutation_items()
    {
        var d = SafeNextActionPolicy.ForApplyRun(
            "completed", unresolvedFrontier: 0, appliedCount: 0, alreadyAppliedCount: 0, failedCount: 0);
        Assert.Equal(SafeNextActionPolicy.None, d.NextSafeOperationId);
        Assert.False(d.MutationMayHaveOccurred);
    }

    [Fact]
    public void Policy_feedback_recorded_is_none_with_mutation_possibility()
    {
        var d = SafeNextActionPolicy.ForFeedback("accepted");
        Assert.Equal(SafeNextActionPolicy.LifecycleRecorded, d.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, d.NextSafeOperationId);
        Assert.True(d.MutationMayHaveOccurred);
    }

    [Fact]
    public void Policy_cleanup_with_removals_marks_mutation()
    {
        var d = SafeNextActionPolicy.ForCleanup(removedArtifactCount: 4);
        Assert.Equal(SafeNextActionPolicy.LifecycleCompleted, d.LifecycleState);
        Assert.True(d.MutationMayHaveOccurred);
        Assert.Equal(SafeNextActionPolicy.None, d.NextSafeOperationId);
    }

    [Fact]
    public void Mapper_status_result_is_bounded()
    {
        var result = ClassifyContractMapper.ToStatusResult(
            ClassifyStatusSubjectType.Evaluation,
            "eval-1",
            new SafeNextActionPolicy.Decision("completed", false, SafeNextActionPolicy.None));
        Assert.Equal(ClassifyOperationIds.ContractVersion, result.ContractVersion);
        Assert.Equal(ClassifyStatusSubjectType.Evaluation, result.SubjectType);
        Assert.Equal("eval-1", result.SubjectId);
        Assert.Equal("completed", result.LifecycleState);
        Assert.False(result.MutationMayHaveOccurred);
        Assert.Equal(SafeNextActionPolicy.None, result.NextSafeOperationId);
    }

    // ── Query integration ───────────────────────────────────────────────────

    [Fact]
    public async Task Status_requires_actor()
    {
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "x"),
            actor: null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Status_rejects_unsupported_version()
    {
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("0.9", ClassifyStatusSubjectType.Evaluation, "x"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Status_rejects_empty_subject_id()
    {
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "  "),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_subject_is_stable_not_found()
    {
        foreach (var type in Enum.GetValues<ClassifyStatusSubjectType>())
        {
            var result = await status.HandleAsync(
                new ClassifyStatusRequest("1.0", type, "missing-" + type),
                actor,
                CancellationToken.None);
            Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public async Task Rule_draft_status_suggests_abandon_when_unreferenced()
    {
        await SeedRuleVersionAsync("rv-draft-1", lifecycle: "draft");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Rule, "rv-draft-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("draft", result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Abandon, result.Value.NextSafeOperationId);
        Assert.False(result.Value.MutationMayHaveOccurred);
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(result.Value.NextSafeOperationId));
    }

    [Fact]
    public async Task Rule_active_status_is_none()
    {
        await SeedRuleVersionAsync("rv-active-1", lifecycle: "active");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Rule, "rv-active-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("active", result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Rule_tombstoned_status_is_abandoned_cleanup()
    {
        await SeedRuleVersionAsync("rv-tomb-1", lifecycle: "draft");
        await InsertTombstoneAsync("tomb-rv-1", "rule", "rv-tomb-1");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Rule, "rv-tomb-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleAbandoned, result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Cleanup, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Validation_completed_status()
    {
        await SeedValidationAsync("val-1", lifecycle: "completed");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Validation, "val-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("completed", result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Validation_running_status_is_retry()
    {
        await SeedValidationAsync("val-run", lifecycle: "running");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Validation, "val-run"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Retry, result.Value!.NextSafeOperationId);
        Assert.False(result.Value.MutationMayHaveOccurred);
    }

    [Fact]
    public async Task Validation_failed_status_is_retry()
    {
        await SeedValidationAsync("val-fail", lifecycle: "failed");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Validation, "val-fail"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Retry, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public async Task Evaluation_completed_status()
    {
        await SeedEvaluationAsync("eval-ok", lifecycle: "completed", conflictCount: 0);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-ok"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal("completed", result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Evaluation_conflict_completed_suggests_correct()
    {
        await SeedEvaluationAsync("eval-conflict", lifecycle: "completed", conflictCount: 3);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-conflict"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Correct, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public async Task Evaluation_failed_suggests_re_evaluate()
    {
        await SeedEvaluationAsync("eval-fail", lifecycle: "failed", conflictCount: 0);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-fail"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.ReEvaluate, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public async Task Evaluation_abandoned_suggests_cleanup()
    {
        await SeedEvaluationAsync("eval-ab", lifecycle: "abandoned", conflictCount: 0);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-ab"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Cleanup, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public async Task Preview_retained_status()
    {
        await SeedPreviewGraphAsync("prev-ok", expiresAt: "2099-01-01T00:00:00Z");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Preview, "prev-ok"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleRetained, result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Preview_expired_status_is_abandon()
    {
        await SeedPreviewGraphAsync("prev-exp", expiresAt: "2020-01-01T00:00:00Z");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Preview, "prev-exp"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleExpired, result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Abandon, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Preview_tombstoned_status_is_cleanup()
    {
        await SeedPreviewGraphAsync("prev-tomb", expiresAt: "2099-01-01T00:00:00Z");
        await InsertTombstoneAsync("tomb-prev", "preview", "prev-tomb");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Preview, "prev-tomb"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleAbandoned, result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.Cleanup, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Apply_running_with_frontier_is_resume_and_mutation()
    {
        await SeedApplyRunAsync(
            "apply-run-1",
            lifecycle: "running",
            unresolvedFrontier: 2,
            itemStates: ["applied", "planned", "planned"]);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Apply, "apply-run-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Resume, result.Value!.NextSafeOperationId);
        Assert.True(result.Value.MutationMayHaveOccurred);
    }

    [Fact]
    public async Task Apply_completed_with_applied_marks_mutation()
    {
        await SeedApplyRunAsync(
            "apply-done",
            lifecycle: "completed",
            unresolvedFrontier: 0,
            itemStates: ["applied", "already_applied"]);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Apply, "apply-done"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.None, result.Value!.NextSafeOperationId);
        Assert.True(result.Value.MutationMayHaveOccurred);
    }

    [Fact]
    public async Task Apply_failed_is_retry()
    {
        await SeedApplyRunAsync(
            "apply-fail",
            lifecycle: "failed",
            unresolvedFrontier: 0,
            itemStates: ["failed"]);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Apply, "apply-fail"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.Retry, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public async Task Feedback_status_is_recorded_none()
    {
        await SeedFeedbackAsync("fb-1");
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Feedback, "fb-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleRecorded, result.Value!.LifecycleState);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
        Assert.True(result.Value.MutationMayHaveOccurred);
    }

    [Fact]
    public async Task Abandonment_status_by_tombstone_id()
    {
        await SeedRuleVersionAsync("rv-ab-subj", lifecycle: "draft");
        await InsertTombstoneAsync("tomb-ab-1", "rule", "rv-ab-subj", removedPayload: 1);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Abandonment, "tomb-ab-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleAbandoned, result.Value!.LifecycleState);
        Assert.True(result.Value.MutationMayHaveOccurred);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Cleanup_status_by_cleanup_id()
    {
        await InsertCleanupEventAsync("clean-1", removed: 3);
        var result = await status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Cleanup, "clean-1"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(SafeNextActionPolicy.LifecycleCompleted, result.Value!.LifecycleState);
        Assert.True(result.Value.MutationMayHaveOccurred);
        Assert.Equal(SafeNextActionPolicy.None, result.Value.NextSafeOperationId);
    }

    [Fact]
    public async Task Every_success_returns_exactly_one_known_next_action()
    {
        await SeedRuleVersionAsync("rv-matrix", lifecycle: "retired");
        await SeedValidationAsync("val-matrix", lifecycle: "completed");
        await SeedEvaluationAsync("eval-matrix", lifecycle: "completed", conflictCount: 0);
        await SeedPreviewGraphAsync("prev-matrix", expiresAt: "2099-01-01T00:00:00Z");
        await SeedApplyRunAsync("apply-matrix", "completed", 0, ["rejected"]);
        await SeedFeedbackAsync("fb-matrix");
        await InsertTombstoneAsync("tomb-matrix", "rule", "rv-matrix");
        await InsertCleanupEventAsync("clean-matrix", removed: 0);

        var subjects = new (ClassifyStatusSubjectType Type, string Id)[]
        {
            (ClassifyStatusSubjectType.Rule, "rv-matrix"),
            (ClassifyStatusSubjectType.Validation, "val-matrix"),
            (ClassifyStatusSubjectType.Evaluation, "eval-matrix"),
            (ClassifyStatusSubjectType.Preview, "prev-matrix"),
            (ClassifyStatusSubjectType.Apply, "apply-matrix"),
            (ClassifyStatusSubjectType.Feedback, "fb-matrix"),
            (ClassifyStatusSubjectType.Abandonment, "tomb-matrix"),
            (ClassifyStatusSubjectType.Cleanup, "clean-matrix")
        };

        foreach (var (type, id) in subjects)
        {
            var result = await status.HandleAsync(
                new ClassifyStatusRequest("1.0", type, id),
                actor,
                CancellationToken.None);
            Assert.True(result.IsSuccess, $"{type}:{id} -> {result.ErrorCode}");
            Assert.True(
                SafeNextActionPolicy.IsKnownNextAction(result.Value!.NextSafeOperationId),
                result.Value.NextSafeOperationId);
            Assert.False(string.IsNullOrWhiteSpace(result.Value.LifecycleState));
            Assert.Equal(type, result.Value.SubjectType);
            Assert.Equal(id, result.Value.SubjectId);
        }
    }

    // ── Seed helpers ────────────────────────────────────────────────────────

    private async Task SeedRuleVersionAsync(string ruleVersionId, string lifecycle)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO classification_rule (rule_id, created_at, created_by)
            VALUES ('rule-status', '2026-08-01T00:00:00Z', 'human:owner');
            INSERT INTO rule_version (
                rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                broad_apply_allowed, validation_run_id, created_at, created_by
            ) VALUES (
                '{ruleVersionId}', 'rule-status', NULL, 'normalization_v1', 'cat-1',
                '{new string('a', 64)}', 'owner_authored', NULL, 'seed', '{lifecycle}',
                0, NULL, '2026-08-01T00:00:00Z', 'human:owner');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedValidationAsync(string validationId, string lifecycle)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        var completed = lifecycle is "completed" or "failed" or "abandoned"
            ? "'2026-08-01T00:00:01Z'"
            : "NULL";
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO validation_run (
                validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, completed_at, lifecycle_state, actor
            ) VALUES (
                '{validationId}', '{new string('a', 64)}', 'owner_authored', '{new string('b', 64)}',
                '{new string('c', 64)}', 'classification_v1', '{new string('d', 64)}',
                'normalization_v1', '2026-08-01T00:00:00Z', {completed}, '{lifecycle}', 'human:owner');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedEvaluationAsync(string evaluationId, string lifecycle, int conflictCount)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await EnsureValidationAndRuleSetAsync(connection, transaction, "val-eval-seed", "rsv-eval-seed");
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                '{evaluationId}', NULL, 'rsv-eval-seed', 'normalization_v1',
                '1.0', 'classification_v1', '{new string('b', 64)}', 'snap-1',
                '2099-01-01T00:00:00Z', '{new string('c', 64)}', '{new string('d', 64)}',
                1, 0, 0, {conflictCount}, 0, '{lifecycle}', 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedPreviewGraphAsync(string previewId, string expiresAt)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await EnsureValidationAndRuleSetAsync(connection, transaction, "val-prev-seed", "rsv-prev-seed");
        var evaluationId = "eval-" + previewId;
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                '{evaluationId}', NULL, 'rsv-prev-seed', 'normalization_v1',
                '1.0', 'classification_v1', '{new string('b', 64)}', 'snap-1',
                '2099-01-01T00:00:00Z', '{new string('c', 64)}', '{new string('d', 64)}',
                1, 1, 0, 0, 0, 'completed', 'human:owner', '2026-08-01T00:00:00Z');
            INSERT OR IGNORE INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                'out-{previewId}', '{evaluationId}', 0, 'tx-{previewId}', 'suggestion',
                'cat-1', '{new string('e', 64)}', 'suggestion');
            INSERT INTO apply_preview (
                preview_id, operation_idempotency_key, evaluation_id, evaluation_fingerprint, selection_mode,
                selection_hash, ledger_contract_version, projection_version, store_generation_fingerprint,
                preflight_snapshot_id, preflight_expires_at, category_lifecycle_fingerprint,
                target_category_fingerprint, rule_authority_fingerprint, expires_at,
                selected_count, exclusion_count, no_suggestion_count, conflict_count, actor, created_at
            ) VALUES (
                '{previewId}', NULL, '{evaluationId}', '{new string('1', 64)}', 'selected_outcomes',
                '{new string('2', 64)}', '1.0', 'classification_v1', '{new string('b', 64)}',
                'snap-p', '2099-01-01T00:00:00Z', '{new string('c', 64)}',
                '{new string('3', 64)}', '{new string('4', 64)}', '{expiresAt}',
                1, 0, 0, 0, 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedApplyRunAsync(
        string applyId,
        string lifecycle,
        int unresolvedFrontier,
        string[] itemStates)
    {
        var previewId = "prev-" + applyId;
        await SeedPreviewGraphAsync(previewId, expiresAt: "2099-01-01T00:00:00Z");
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        var completed = lifecycle is "completed" or "failed" or "abandoned"
            ? "'2026-08-01T00:01:00Z'"
            : "NULL";
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO apply_run (
                apply_id, preview_id, request_fingerprint, lifecycle_state, unresolved_frontier,
                actor, started_at, completed_at
            ) VALUES (
                '{applyId}', '{previewId}', '{new string('5', 64)}', '{lifecycle}', {unresolvedFrontier},
                'human:owner', '2026-08-01T00:00:00Z', {completed});
            """);
        for (var i = 0; i < itemStates.Length; i++)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO apply_item (
                    apply_id, ordinal, transaction_id, ledger_operation_id, category_id,
                    expected_active_allocation_id, expected_transaction_revision,
                    expected_relationship_revision, expected_allocation_revision, correction_reason,
                    ledger_request_fingerprint, ledger_idempotency_key, item_state,
                    ledger_result_fingerprint, ledger_allocation_id, prior_ledger_allocation_id, safe_error_code
                ) VALUES (
                    '{applyId}', {i}, 'tx-{applyId}-{i}', 'ledger.transaction.category.assign', 'cat-1',
                    NULL, 'genesis:tx', 'none', 'none', NULL,
                    '{new string('6', 64)}', 'idem-{applyId}-{i}', '{itemStates[i]}',
                    NULL, NULL, NULL, NULL);
                """);
        }

        await transaction.CommitAsync();
    }

    private async Task SeedFeedbackAsync(string feedbackId)
    {
        await SeedPreviewGraphAsync("prev-" + feedbackId, expiresAt: "2099-01-01T00:00:00Z");
        var evaluationId = "eval-prev-" + feedbackId;
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO classification_feedback (
                feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                rule_set_version_id, decision_type, reason, actor, occurred_at
            ) VALUES (
                '{feedbackId}', 'out-prev-{feedbackId}', 'tx-prev-{feedbackId}', '{evaluationId}',
                'normalization_v1', 'rsv-prev-seed', 'accept', 'ok', 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task InsertTombstoneAsync(
        string tombstoneId,
        string subjectType,
        string subjectId,
        int removedPayload = 0)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await recovery.InsertTombstoneAsync(
            connection,
            transaction,
            new ClassifyAbandonmentTombstoneRow(
                tombstoneId,
                subjectType,
                subjectId,
                "status-seed",
                "human:owner",
                "2026-08-01T00:00:00Z",
                removedPayload),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task InsertCleanupEventAsync(string cleanupId, int removed)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await recovery.InsertCleanupEventAsync(
            connection,
            transaction,
            ClassifyContractMapper.ToCleanupEventRow(
                cleanupId,
                ClassifyRetentionPolicy.PolicyVersion,
                recognizedRemovedCount: removed,
                expiredPreviewCount: 0,
                abandonedPayloadCount: 0,
                actor: "human:owner",
                occurredAtUtc: "2026-08-01T00:00:00Z",
                removedArtifactCount: removed,
                retainedArtifactCount: 0),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task EnsureValidationAndRuleSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string validationId,
        string ruleSetVersionId)
    {
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO validation_run (
                validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, completed_at, lifecycle_state, actor
            ) VALUES (
                '{validationId}', '{new string('a', 64)}', 'owner_authored', '{new string('a', 64)}',
                '{new string('a', 64)}', 'classification_v1', '{new string('a', 64)}',
                'normalization_v1', '2026-08-01T00:00:00Z', '2026-08-01T00:00:01Z', 'completed', 'human:owner');
            INSERT OR IGNORE INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                '{ruleSetVersionId}', NULL, 'normalization_v1', '{validationId}', 'seed',
                '2026-08-01T00:00:00Z', 'human:owner');
            """);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
