using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Classify.Storage;
using Xunit;

namespace Tally.Tests.Classify.Storage;

[SupportedOSPlatform("linux")]
public sealed class ClassifyHistoryInvariantTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-history-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rule_versions_and_conditions_are_immutable()
    {
        await using var connection = await SeedRuleGraphAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE rule_version SET reason = 'changed' WHERE rule_version_id = 'rv-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM rule_version WHERE rule_version_id = 'rv-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE rule_condition SET value_text = 'x' WHERE rule_version_id = 'rv-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM rule_condition WHERE rule_version_id = 'rv-1';"));
    }

    [Fact]
    public async Task Outcomes_and_match_evidence_are_append_only()
    {
        await using var connection = await SeedEvaluationAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE classification_outcome SET safe_reason = 'x' WHERE outcome_id = 'out-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM classification_outcome WHERE outcome_id = 'out-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE match_evidence SET field_key = 'account.id' WHERE outcome_id = 'out-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM match_evidence WHERE outcome_id = 'out-1';"));
    }

    [Fact]
    public async Task Feedback_and_lifecycle_events_cannot_be_updated_or_deleted()
    {
        await using var connection = await SeedEvaluationAsync();
        await ExecuteAsync(connection, """
            INSERT INTO classification_feedback (
                feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                rule_set_version_id, decision_type, reason, actor, occurred_at
            ) VALUES (
                'fb-1', 'out-1', 'tx-1', 'eval-1', 'normalization_v1',
                'rsv-1', 'accept', 'ok', 'human:owner', '2026-07-31T00:00:00Z'
            );
            """);
        await ExecuteAsync(connection, """
            INSERT INTO rule_lifecycle_event (
                event_id, subject_id, prior_state, resulting_state, reason, actor, occurred_at
            ) VALUES (
                'rle-1', 'rv-1', 'draft', 'validated', 'validated', 'human:owner', '2026-07-31T00:00:00Z'
            );
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE classification_feedback SET reason = 'nope' WHERE feedback_id = 'fb-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM classification_feedback WHERE feedback_id = 'fb-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE rule_lifecycle_event SET reason = 'nope' WHERE event_id = 'rle-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM rule_lifecycle_event WHERE event_id = 'rle-1';"));
    }

    [Fact]
    public async Task Evaluation_run_rejects_invalid_lifecycle_transition()
    {
        await using var connection = await SeedEvaluationAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE evaluation_run SET lifecycle_state = 'running' WHERE evaluation_id = 'eval-1';"));
        // completed is already terminal via seed as 'completed' — cannot go back
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE evaluation_run SET lifecycle_state = 'failed' WHERE evaluation_id = 'eval-1';"));
    }

    [Fact]
    public async Task Evaluation_run_allows_running_to_completed_with_expected_prior_state()
    {
        var store = new ClassifyStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await SeedRuleGraphOnAsync(connection);
        await SeedRunningEvaluationAsync(connection);

        await using (var tx = store.BeginImmediate(connection))
        {
            var ok = await store.TryTransitionEvaluationLifecycleAsync(
                connection, tx, "eval-run", "running", "completed", CancellationToken.None);
            Assert.True(ok);
            await tx.CommitAsync();
        }

        await using (var tx = store.BeginImmediate(connection))
        {
            var again = await store.TryTransitionEvaluationLifecycleAsync(
                connection, tx, "eval-run", "running", "failed", CancellationToken.None);
            Assert.False(again);
        }
    }

    [Fact]
    public async Task Evaluation_run_content_columns_are_immutable()
    {
        await using var connection = await SeedEvaluationAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE evaluation_run SET actor = 'other' WHERE evaluation_id = 'eval-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE evaluation_run SET input_count = 99 WHERE evaluation_id = 'eval-1';"));
    }

    [Fact]
    public async Task Terminal_apply_items_are_immutable()
    {
        await using var connection = await SeedApplyAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE apply_item SET item_state = 'failed' WHERE apply_id = 'apply-1' AND ordinal = 0;"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM apply_item WHERE apply_id = 'apply-1';"));
    }

    [Fact]
    public async Task Planned_apply_item_may_advance_to_terminal()
    {
        await using var connection = await SeedApplyAsync(plannedItem: true);
        await ExecuteAsync(connection, """
            UPDATE apply_item
            SET item_state = 'applied', ledger_allocation_id = 'alloc-1'
            WHERE apply_id = 'apply-1' AND ordinal = 0 AND item_state = 'planned';
            """);
        Assert.Equal("applied", await ScalarStringAsync(connection, "SELECT item_state FROM apply_item WHERE apply_id = 'apply-1';"));
    }

    [Fact]
    public async Task Apply_item_frozen_replay_request_cannot_change_before_terminal_state()
    {
        await using var connection = await SeedApplyAsync(plannedItem: true);
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE apply_item SET category_id = 'cat-other' WHERE apply_id = 'apply-1' AND ordinal = 0;"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE apply_item SET item_state = 'planned' WHERE apply_id = 'apply-1' AND ordinal = 0;"));
    }

    [Fact]
    public async Task Apply_run_completion_is_writable_only_during_terminal_transition()
    {
        await using var connection = await SeedApplyAsync(plannedItem: true);

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE apply_run SET completed_at = '2026-07-31T01:00:00Z' WHERE apply_id = 'apply-1';"));

        await ExecuteAsync(connection, """
            UPDATE apply_run
            SET lifecycle_state = 'completed', completed_at = '2026-07-31T01:00:00Z'
            WHERE apply_id = 'apply-1' AND lifecycle_state = 'running';
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE apply_run SET completed_at = '2026-07-31T02:00:00Z' WHERE apply_id = 'apply-1';"));
    }

    [Fact]
    public async Task Foreign_keys_restrict_delete_of_referenced_history()
    {
        await using var connection = await SeedEvaluationAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM evaluation_run WHERE evaluation_id = 'eval-1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM rule_set_version WHERE rule_set_version_id = 'rsv-1';"));
    }

    [Fact]
    public async Task Cleanup_and_abandonment_events_are_append_only()
    {
        await using var connection = await MigratedAsync();
        await ExecuteAsync(connection, """
            INSERT INTO abandonment_tombstone VALUES (
                't1', 'evaluation', 'e1', 'reason', 'human:owner', '2026-07-31T00:00:00Z', 0);
            """);
        await ExecuteAsync(connection, """
            INSERT INTO cleanup_event (
                cleanup_id, policy_version, recognized_removed_count, expired_preview_count,
                abandoned_payload_count, actor, occurred_at,
                removed_artifact_count, retained_artifact_count
            ) VALUES (
                'c1', 'policy_v1', 1, 0, 0, 'human:owner', '2026-07-31T00:00:00Z',
                0, 0);
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE abandonment_tombstone SET reason = 'x' WHERE tombstone_id = 't1';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM cleanup_event WHERE cleanup_id = 'c1';"));
    }

    // ── seed helpers ─────────────────────────────────────────────────────────

    private async Task<SqliteConnection> MigratedAsync()
    {
        var store = new ClassifyStateStore(root);
        return await store.OpenMigratedAsync(CancellationToken.None);
    }

    private async Task SeedRuleGraphOnAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO classification_rule VALUES ('rule-1', '2026-07-31T00:00:00Z', 'human:owner');
            INSERT INTO rule_version VALUES (
                'rv-1', 'rule-1', NULL, 'normalization_v1', 'cat-1',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'owner_authored', NULL, 'seed', 'draft', 0, NULL, '2026-07-31T00:00:00Z', 'human:owner');
            INSERT INTO rule_condition VALUES (
                'rv-1', 0, 'account.id', 'equals', 'acct-1', NULL, NULL, NULL);
            INSERT INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by,
                owner_rulebook_gate_receipt_id, owner_rulebook_gate_receipt_fingerprint
            ) VALUES (
                'rsv-1', NULL, 'normalization_v1', 'val-1', 'activate', '2026-07-31T00:00:00Z', 'human:owner',
                NULL, NULL);
            INSERT INTO rule_set_member VALUES ('rsv-1', 'rv-1');
            """);
    }

    private async Task<SqliteConnection> SeedRuleGraphAsync()
    {
        var connection = await MigratedAsync();
        await SeedRuleGraphOnAsync(connection);
        return connection;
    }

    private async Task SeedRunningEvaluationAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                'eval-run', NULL, 'rsv-1', 'normalization_v1', '1.0', 'classification_v1',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'snap-1', '2026-07-31T01:00:00Z',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
                1, 0, 1, 0, 0, 'running', 'human:owner', '2026-07-31T00:00:00Z'
            );
            """);
    }

    private async Task<SqliteConnection> SeedEvaluationAsync()
    {
        var connection = await SeedRuleGraphAsync();
        await ExecuteAsync(connection, """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                'eval-1', NULL, 'rsv-1', 'normalization_v1', '1.0', 'classification_v1',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'snap-1', '2026-07-31T01:00:00Z',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
                1, 1, 0, 0, 0, 'completed', 'human:owner', '2026-07-31T00:00:00Z'
            );
            INSERT INTO classification_outcome VALUES (
                'out-1', 'eval-1', 0, 'tx-1', 'suggestion', 'cat-1',
                'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
                'matched');
            INSERT INTO match_evidence VALUES (
                'out-1', 'rv-1', 'cond-0', 'account.id', 'equals',
                'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff');
            """);
        return connection;
    }

    private async Task<SqliteConnection> SeedApplyAsync(bool plannedItem = false)
    {
        var connection = await SeedEvaluationAsync();
        await ExecuteAsync(connection, """
            INSERT INTO apply_preview (
                preview_id, operation_idempotency_key, evaluation_id, evaluation_fingerprint, selection_mode,
                selection_hash, ledger_contract_version, projection_version, store_generation_fingerprint,
                preflight_snapshot_id, preflight_expires_at, category_lifecycle_fingerprint,
                target_category_fingerprint, rule_authority_fingerprint, expires_at,
                selected_count, exclusion_count, no_suggestion_count, conflict_count, actor, created_at
            ) VALUES (
                'prev-1', NULL, 'eval-1',
                '1111111111111111111111111111111111111111111111111111111111111111',
                'selected_outcomes',
                '2222222222222222222222222222222222222222222222222222222222222222',
                '1.0', 'classification_v1',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'snap-2', '2026-07-31T01:00:00Z',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                '3333333333333333333333333333333333333333333333333333333333333333',
                '4444444444444444444444444444444444444444444444444444444444444444',
                '2026-07-31T01:00:00Z', 1, 0, 0, 0, 'human:owner', '2026-07-31T00:00:00Z'
            );
            INSERT INTO apply_preview_item VALUES (
                'prev-1', 0, 'out-1', 'tx-1', 'assign', 'cat-1', 'rv-1', NULL, NULL,
                'genesis:tx-1', 'none', 'none', NULL);
            INSERT INTO apply_run VALUES (
                'apply-1', 'prev-1',
                '5555555555555555555555555555555555555555555555555555555555555555',
                'running', 0, 'human:owner', '2026-07-31T00:00:00Z', NULL);
            """);
        var state = plannedItem ? "planned" : "applied";
        await ExecuteAsync(connection, $"""
            INSERT INTO apply_item (
                apply_id, ordinal, transaction_id, ledger_operation_id, category_id,
                expected_active_allocation_id, expected_transaction_revision, expected_relationship_revision,
                expected_allocation_revision, correction_reason, ledger_request_fingerprint,
                ledger_idempotency_key, item_state, ledger_result_fingerprint, ledger_allocation_id,
                prior_ledger_allocation_id, safe_error_code
            ) VALUES (
                'apply-1', 0, 'tx-1', 'ledger.transaction.category.assign', 'cat-1',
                NULL, 'genesis:tx-1', 'none', 'none', NULL,
                '6666666666666666666666666666666666666666666666666666666666666666',
                'item-key-1', '{state}', NULL, NULL, NULL, NULL);
            """);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }
}
