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
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Xunit;

namespace Tally.Tests.Classify.Recovery;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-STATUS-WORKFLOW / bd-3tpm — no-reread and disclosure boundary.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class StatusPrivacyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-status-priv-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "status-privacy", "run-01");
    private ClassifyRecoveryServices services = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        services = await ClassifyRecoveryExtensions.CreateServicesAsync(root);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Status_result_json_has_no_paths_amounts_or_descriptions()
    {
        await SeedEvaluationAsync("eval-priv", "completed", 0);
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("classify/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpus", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lifecycleState", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mutationMayHaveOccurred", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nextSafeOperationId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_does_not_embed_outcome_payload_or_safe_reason_text()
    {
        await SeedEvaluationWithOutcomeAsync(
            "eval-payload",
            outcomeSafeReason: "matched private token SECRET-PAYLOAD-XYZ");
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-payload"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain("SECRET-PAYLOAD-XYZ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("matched private token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tx-eval-payload", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_status_does_not_list_transaction_ids_or_allocation_ids()
    {
        await SeedApplyAsync("apply-priv", itemStates: ["applied", "planned"]);
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Apply, "apply-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain("tx-apply-priv", json, StringComparison.Ordinal);
        Assert.DoesNotContain("alloc-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ledger.transaction", json, StringComparison.Ordinal);
        Assert.True(SafeNextActionPolicy.IsKnownNextAction(result.Value!.NextSafeOperationId));
    }

    [Fact]
    public async Task Feedback_status_does_not_expose_reason_text()
    {
        await SeedFeedbackAsync("fb-priv", reason: "owner-private-reason-text-99");
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Feedback, "fb-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain("owner-private-reason-text-99", json, StringComparison.Ordinal);
        Assert.DoesNotContain("out-prev-fb-priv", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_subject_does_not_search_payloads_or_return_partial_data()
    {
        await SeedEvaluationWithOutcomeAsync("eval-exists", outcomeSafeReason: "hidden");
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-missing"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Status_never_requires_idempotency_key()
    {
        await SeedEvaluationAsync("eval-idem", "completed", 0);
        // HandleAsync signature has no idempotency parameter — query-only surface.
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-idem"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
    }

    [Fact]
    public async Task Status_does_not_mutate_durable_rows()
    {
        await SeedEvaluationAsync("eval-imut", "completed", 1);
        await using (var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None))
        {
            var before = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evaluation_run;");
            _ = await services.Status.HandleAsync(
                new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-imut"),
                actor,
                CancellationToken.None);
            var after = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM evaluation_run;");
            Assert.Equal(before, after);
            var lifecycle = await ScalarStringAsync(
                connection,
                "SELECT lifecycle_state FROM evaluation_run WHERE evaluation_id = 'eval-imut';");
            Assert.Equal("completed", lifecycle);
        }
    }

    [Fact]
    public async Task Preview_status_does_not_reread_ledger_or_corpus_fields_into_result()
    {
        await SeedPreviewAsync("prev-priv", expiresAt: "2099-01-01T00:00:00Z");
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Preview, "prev-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain("store_generation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("projection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snap-p", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abandonment_status_does_not_echo_reason_or_actor_in_public_result()
    {
        await SeedRuleVersionAsync("rv-priv-ab");
        await InsertTombstoneAsync("tomb-priv", "rule", "rv-priv-ab", reason: "secret-abandon-reason");
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Abandonment, "tomb-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        Assert.DoesNotContain("secret-abandon-reason", json, StringComparison.Ordinal);
        Assert.DoesNotContain("human:owner", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_status_does_not_expose_policy_path_or_counts_in_public_shape()
    {
        await InsertCleanupEventAsync("clean-priv", removed: 2);
        var result = await services.Status.HandleAsync(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Cleanup, "clean-priv"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyStatusResult);
        // Public ClassifyStatusResult is intentionally sparse — counts stay internal to decision.
        Assert.DoesNotContain("removedArtifactCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cleanup_v1", json, StringComparison.Ordinal);
        Assert.Equal(SafeNextActionPolicy.None, result.Value!.NextSafeOperationId);
    }

    [Fact]
    public void Mapper_apply_totals_are_aggregate_only()
    {
        var items = new[]
        {
            new ClassifyApplyItemRow(
                "a", 0, "tx-1", "ledger.transaction.category.assign", "cat",
                null, "rev", "none", "none", null,
                new string('1', 64), "k1", "applied", null, "alloc-1", null, null),
            new ClassifyApplyItemRow(
                "a", 1, "tx-2", "ledger.transaction.category.assign", "cat",
                null, "rev", "none", "none", null,
                new string('2', 64), "k2", "planned", null, null, null, null)
        };
        var totals = ClassifyContractMapper.ToApplyStatusTotals(items);
        Assert.Equal(1, totals.AppliedCount);
        Assert.Equal(1, totals.UnresolvedCount);
        Assert.Equal(0, totals.FailedCount);
    }

    // ── Seeds ───────────────────────────────────────────────────────────────

    private async Task SeedRuleVersionAsync(string ruleVersionId)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO classification_rule (rule_id, created_at, created_by)
            VALUES ('rule-priv', '2026-08-01T00:00:00Z', 'human:owner');
            INSERT INTO rule_version (
                rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                broad_apply_allowed, validation_run_id, created_at, created_by
            ) VALUES (
                '{ruleVersionId}', 'rule-priv', NULL, 'normalization_v1', 'cat-1',
                '{new string('a', 64)}', 'owner_authored', NULL, 'seed', 'draft',
                0, NULL, '2026-08-01T00:00:00Z', 'human:owner');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedEvaluationAsync(string evaluationId, string lifecycle, int conflictCount)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await EnsureRuleSetAsync(connection, transaction);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                '{evaluationId}', NULL, 'rsv-priv', 'normalization_v1',
                '1.0', 'classification_v1', '{new string('b', 64)}', 'snap-1',
                '2099-01-01T00:00:00Z', '{new string('c', 64)}', '{new string('d', 64)}',
                1, 0, 0, {conflictCount}, 0, '{lifecycle}', 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedEvaluationWithOutcomeAsync(string evaluationId, string outcomeSafeReason)
    {
        await SeedEvaluationAsync(evaluationId, "completed", 0);
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                'out-{evaluationId}', '{evaluationId}', 0, 'tx-{evaluationId}', 'suggestion',
                'cat-1', '{new string('e', 64)}', '{outcomeSafeReason}');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedPreviewAsync(string previewId, string expiresAt)
    {
        await SeedEvaluationAsync("eval-" + previewId, "completed", 0);
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO apply_preview (
                preview_id, operation_idempotency_key, evaluation_id, evaluation_fingerprint, selection_mode,
                selection_hash, ledger_contract_version, projection_version, store_generation_fingerprint,
                preflight_snapshot_id, preflight_expires_at, category_lifecycle_fingerprint,
                target_category_fingerprint, rule_authority_fingerprint, expires_at,
                selected_count, exclusion_count, no_suggestion_count, conflict_count, actor, created_at
            ) VALUES (
                '{previewId}', NULL, 'eval-{previewId}', '{new string('1', 64)}', 'selected_outcomes',
                '{new string('2', 64)}', '1.0', 'classification_v1', '{new string('b', 64)}',
                'snap-p', '2099-01-01T00:00:00Z', '{new string('c', 64)}',
                '{new string('3', 64)}', '{new string('4', 64)}', '{expiresAt}',
                1, 0, 0, 0, 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task SeedApplyAsync(string applyId, string[] itemStates)
    {
        var previewId = "prev-" + applyId;
        await SeedPreviewAsync(previewId, "2099-01-01T00:00:00Z");
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO apply_run (
                apply_id, preview_id, request_fingerprint, lifecycle_state, unresolved_frontier,
                actor, started_at, completed_at
            ) VALUES (
                '{applyId}', '{previewId}', '{new string('5', 64)}', 'running', 1,
                'human:owner', '2026-08-01T00:00:00Z', NULL);
            """);
        for (var i = 0; i < itemStates.Length; i++)
        {
            var alloc = itemStates[i] == "applied" ? $"'alloc-{applyId}-{i}'" : "NULL";
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
                    NULL, {alloc}, NULL, NULL);
                """);
        }

        await transaction.CommitAsync();
    }

    private async Task SeedFeedbackAsync(string feedbackId, string reason)
    {
        await SeedPreviewAsync("prev-" + feedbackId, "2099-01-01T00:00:00Z");
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                'out-prev-{feedbackId}', 'eval-prev-{feedbackId}', 0, 'tx-prev-{feedbackId}', 'suggestion',
                'cat-1', '{new string('e', 64)}', 'suggestion');
            INSERT INTO classification_feedback (
                feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                rule_set_version_id, decision_type, reason, actor, occurred_at
            ) VALUES (
                '{feedbackId}', 'out-prev-{feedbackId}', 'tx-prev-{feedbackId}', 'eval-prev-{feedbackId}',
                'normalization_v1', 'rsv-priv', 'accept', '{reason}', 'human:owner', '2026-08-01T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private async Task InsertTombstoneAsync(
        string tombstoneId,
        string subjectType,
        string subjectId,
        string reason)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await services.RecoveryStore.InsertTombstoneAsync(
            connection,
            transaction,
            new ClassifyAbandonmentTombstoneRow(
                tombstoneId, subjectType, subjectId, reason, "human:owner",
                "2026-08-01T00:00:00Z", 0),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task InsertCleanupEventAsync(string cleanupId, int removed)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = services.State.Store.BeginImmediate(connection);
        await services.RecoveryStore.InsertCleanupEventAsync(
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

    private static async Task EnsureRuleSetAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO validation_run (
                validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, completed_at, lifecycle_state, actor
            ) VALUES (
                'val-priv', '{new string('a', 64)}', 'owner_authored', '{new string('a', 64)}',
                '{new string('a', 64)}', 'classification_v1', '{new string('a', 64)}',
                'normalization_v1', '2026-08-01T00:00:00Z', '2026-08-01T00:00:01Z', 'completed', 'human:owner');
            INSERT OR IGNORE INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                'rsv-priv', NULL, 'normalization_v1', 'val-priv', 'seed',
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

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
