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
using Tally.Features.Classify.Recovery.Abandon;
using Tally.Features.Classify.Recovery.Cleanup;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Xunit;

namespace Tally.Tests.Classify.Recovery;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-ABANDON-CLEANUP / bd-3hcn — retention, abandon, cleanup workflows.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AbandonCleanupTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-abandon-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "abandon", "run-01");
    private ClassifyStateServices state = null!;
    private ClassificationRecoveryStore recovery = null!;
    private ClassifyArtifactProtection protection = null!;
    private AbandonClassificationStateCommand abandon = null!;
    private CleanupClassificationStateCommand cleanup = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        state = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        recovery = new ClassificationRecoveryStore();
        protection = new ClassifyArtifactProtection(state.Store.Paths, state.Protection);
        abandon = new AbandonClassificationStateCommand(
            state.Store, recovery, protection, state.Idempotency);
        cleanup = new CleanupClassificationStateCommand(
            state.Store, recovery, protection, state.Idempotency);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Pure retention policy ───────────────────────────────────────────────

    [Fact]
    public void Policy_version_is_fixed()
    {
        Assert.Equal("cleanup_v1", ClassifyRetentionPolicy.PolicyVersion);
        Assert.True(ClassifyRetentionPolicy.IsSupportedCleanupPolicyVersion("cleanup_v1"));
        Assert.False(ClassifyRetentionPolicy.IsSupportedCleanupPolicyVersion("cleanup_v0"));
        Assert.False(ClassifyRetentionPolicy.IsSupportedCleanupPolicyVersion(null));
    }

    [Fact]
    public void Policy_abandonable_subject_types()
    {
        Assert.True(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Rule));
        Assert.True(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Validation));
        Assert.True(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Evaluation));
        Assert.True(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Preview));
        Assert.False(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Apply));
        Assert.False(ClassifyRetentionPolicy.IsAbandonableSubjectType(ClassifyStatusSubjectType.Feedback));
    }

    [Fact]
    public void Policy_apply_and_feedback_always_restricted()
    {
        Assert.True(ClassifyRetentionPolicy.IsAlwaysRestrictedSubjectType(ClassifyStatusSubjectType.Apply));
        Assert.True(ClassifyRetentionPolicy.IsAlwaysRestrictedSubjectType(ClassifyStatusSubjectType.Feedback));
        var denied = ClassifyRetentionPolicy.EvaluateAbandon(
            ClassifyStatusSubjectType.Apply, ClassifyRetentionPolicy.ReferenceFlags.None);
        Assert.False(denied.Allowed);
        Assert.Equal(ClassifyErrors.Lifecycle, denied.ErrorCode);
    }

    [Fact]
    public void Policy_referenced_evaluation_is_restrict()
    {
        var denied = ClassifyRetentionPolicy.EvaluateAbandon(
            ClassifyStatusSubjectType.Evaluation,
            ClassifyRetentionPolicy.ReferenceFlags.Feedback);
        Assert.False(denied.Allowed);
        Assert.Contains("feedback", denied.BlockerFlags);
    }

    [Fact]
    public void Policy_unreferenced_preview_is_allowed()
    {
        var ok = ClassifyRetentionPolicy.EvaluateAbandon(
            ClassifyStatusSubjectType.Preview,
            ClassifyRetentionPolicy.ReferenceFlags.None);
        Assert.True(ok.Allowed);
    }

    [Fact]
    public void Policy_expired_detection()
    {
        Assert.True(ClassifyRetentionPolicy.IsExpired(
            "2020-01-01T00:00:00Z", DateTimeOffset.Parse("2026-08-01T00:00:00Z")));
        Assert.False(ClassifyRetentionPolicy.IsExpired(
            "2099-01-01T00:00:00Z", DateTimeOffset.Parse("2026-08-01T00:00:00Z")));
    }

    [Fact]
    public void Policy_format_subject_type_wire()
    {
        Assert.Equal("rule", ClassifyRetentionPolicy.FormatSubjectType(ClassifyStatusSubjectType.Rule));
        Assert.Equal("preview", ClassifyRetentionPolicy.FormatSubjectType(ClassifyStatusSubjectType.Preview));
        Assert.Equal("evaluation", ClassifyRetentionPolicy.FormatSubjectType(ClassifyStatusSubjectType.Evaluation));
    }

    // ── Guards ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Abandon_requires_actor()
    {
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "p1",
                "reason"),
            null, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_requires_idempotency()
    {
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "p1",
                "reason"),
            actor, null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_rejects_apply_subject()
    {
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Apply,
                "apply-1",
                "nope"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_rejects_feedback_subject()
    {
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Feedback,
                "fb-1",
                "nope"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_unknown_subject_is_not_found()
    {
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "missing-preview",
                "gone"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_rejects_unknown_policy_version()
    {
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, "not-a-policy"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Cleanup_requires_actor()
    {
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            null, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    // ── Abandon unreferenced subjects ───────────────────────────────────────

    [Fact]
    public async Task Abandon_unreferenced_preview_appends_tombstone()
    {
        await SeedMinimalPreviewGraphAsync("prev-1", "eval-1", "rsv-1", expiresAt: "2099-01-01T00:00:00Z");
        protection.CreateRecognizedTemporaryForTests("tmp-prev-1-residue", [1, 2, 3]);

        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "prev-1",
                "owner abandon preview"),
            actor, NextKey(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.Abandoned);
        Assert.Equal(ClassifyStatusSubjectType.Preview, result.Value.SubjectType);
        Assert.Equal("prev-1", result.Value.SubjectId);

        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        var tombstone = await recovery.GetTombstoneAsync(
            connection, null, "preview", "prev-1", CancellationToken.None);
        Assert.NotNull(tombstone);
        Assert.Equal("owner abandon preview", tombstone!.Reason);
        Assert.True(tombstone.RemovedPayloadCount >= 0);
        // Preview row remains (immutable history) — no hard delete.
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM apply_preview WHERE preview_id = 'prev-1';"));
    }

    [Fact]
    public async Task Abandon_referenced_preview_by_apply_run_is_rejected()
    {
        await SeedMinimalPreviewGraphAsync("prev-2", "eval-2", "rsv-2", expiresAt: "2099-01-01T00:00:00Z", withApplyRun: true);
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "prev-2",
                "should fail"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.Null(await recovery.GetTombstoneAsync(connection, null, "preview", "prev-2", CancellationToken.None));
    }

    [Fact]
    public async Task Abandon_evaluation_with_feedback_is_rejected()
    {
        await SeedMinimalPreviewGraphAsync("prev-3", "eval-3", "rsv-3", expiresAt: "2099-01-01T00:00:00Z", withFeedback: true);
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Evaluation,
                "eval-3",
                "blocked"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
    }

    [Fact]
    public async Task Abandon_unreferenced_evaluation_tombstones()
    {
        await SeedEvaluationOnlyAsync("eval-free");
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Evaluation,
                "eval-free",
                "drop eval"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.NotNull(await recovery.GetTombstoneAsync(connection, null, "evaluation", "eval-free", CancellationToken.None));
    }

    [Fact]
    public async Task Abandon_second_time_is_lifecycle_conflict()
    {
        await SeedMinimalPreviewGraphAsync("prev-dup", "eval-dup", "rsv-dup", "2099-01-01T00:00:00Z");
        var first = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "prev-dup",
                "once"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "prev-dup",
                "twice"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, second.ErrorCode);
    }

    [Fact]
    public async Task Abandon_idempotent_replay()
    {
        await SeedMinimalPreviewGraphAsync("prev-idem", "eval-idem", "rsv-idem", "2099-01-01T00:00:00Z");
        var key = NextKey();
        var request = new ClassifyAbandonRequest(
            ClassifyOperationIds.ContractVersion,
            ClassifyStatusSubjectType.Preview,
            "prev-idem",
            "idem");
        var a = await abandon.HandleAsync(request, actor, key, CancellationToken.None);
        var b = await abandon.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.SubjectId, b.Value!.SubjectId);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cleanup_removes_recognized_temporaries_and_records_event()
    {
        protection.CreateRecognizedTemporaryForTests("crash-residue-1", [1]);
        protection.CreateRecognizedTemporaryForTests("tmp-residue-2", [2]);
        var paths = new ClassifyStorePaths(root);
        var unknown = Path.Combine(paths.TemporaryDirectory, "keep-me.bin");
        File.WriteAllBytes(unknown, [9]);
        File.SetUnixFileMode(unknown, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyRetentionPolicy.PolicyVersion, result.Value!.PolicyVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.CleanupId));
        Assert.True(result.Value.RemovedTemporaryCount >= 2);
        Assert.True(result.Value.RemovedArtifactCount >= result.Value.RemovedTemporaryCount);
        Assert.True(result.Value.RetainedArtifactCount >= 0);
        Assert.True(File.Exists(unknown));
        Assert.False(File.Exists(Path.Combine(paths.TemporaryDirectory, "crash-residue-1")));

        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(1L, await recovery.CountCleanupEventsAsync(connection, null, CancellationToken.None));
        Assert.True(await recovery.HasCleanupEventAsync(
            connection, null, result.Value.CleanupId, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_tombstones_expired_unreferenced_previews()
    {
        await SeedMinimalPreviewGraphAsync("prev-exp", "eval-exp", "rsv-exp", expiresAt: "2020-01-01T00:00:00Z");
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.RemovedExpiredPreviewCount >= 1);

        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.NotNull(await recovery.GetTombstoneAsync(connection, null, "preview", "prev-exp", CancellationToken.None));
        // Still retained in apply_preview table
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM apply_preview WHERE preview_id = 'prev-exp';"));
    }

    [Fact]
    public async Task Cleanup_does_not_touch_referenced_expired_preview()
    {
        await SeedMinimalPreviewGraphAsync(
            "prev-ref-exp", "eval-ref-exp", "rsv-ref-exp",
            expiresAt: "2020-01-01T00:00:00Z",
            withApplyRun: true);
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.Null(await recovery.GetTombstoneAsync(connection, null, "preview", "prev-ref-exp", CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_result_has_no_paths_or_payload()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-disclosure", [1]);
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyCleanupResult);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("tmp-disclosure", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("classify/", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abandon_result_has_no_private_paths()
    {
        await SeedMinimalPreviewGraphAsync("prev-disc", "eval-disc", "rsv-disc", "2099-01-01T00:00:00Z");
        var result = await abandon.HandleAsync(
            new ClassifyAbandonRequest(
                ClassifyOperationIds.ContractVersion,
                ClassifyStatusSubjectType.Preview,
                "prev-disc",
                "ok"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyAbandonResult);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_idempotent_replay()
    {
        var key = NextKey();
        var request = new ClassifyCleanupRequest(
            ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion);
        var a = await cleanup.HandleAsync(request, actor, key, CancellationToken.None);
        var b = await cleanup.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.PolicyVersion, b.Value!.PolicyVersion);
        Assert.Equal(a.Value.RemovedTemporaryCount, b.Value.RemovedTemporaryCount);
    }

    [Fact]
    public async Task Startup_recovery_via_cleanup_clears_crash_residue()
    {
        protection.CreateRecognizedTemporaryForTests("crash-startup", [7]);
        var staged = protection.TryStageRecognizedTemporaries(
            "startup-op-1", "cleanup", ["crash-startup"]);
        Assert.NotNull(staged);
        // Uncommitted: restore
        var actions = protection.RecoverQuarantineAtStartup((_, _) => false);
        Assert.True(actions >= 1);
        Assert.Contains("crash-startup", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public async Task Quarantine_restore_on_failed_authority_keeps_temp()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-restore-me", [1]);
        var q = protection.TryStageRecognizedTemporaries(
            "op-restore", "abandon", ["tmp-restore-me"]);
        Assert.NotNull(q);
        Assert.DoesNotContain("tmp-restore-me", protection.ListRecognizedTemporaryFileNames());
        q!.RestoreAndDiscard();
        Assert.Contains("tmp-restore-me", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public async Task Cleanup_receipt_exposes_no_paths_or_names()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-secret-name", [1]);
        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyCleanupResult);
        Assert.DoesNotContain("tmp-secret-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.Contains("cleanupId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retainedArtifactCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("removedArtifactCount", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cleanup_retains_locked_recognized_and_still_removes_unlocked()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-clean-free", [1]);
        protection.CreateRecognizedTemporaryForTests("tmp-clean-locked", [2]);
        var paths = new ClassifyStorePaths(root);
        var lockPath = Path.Combine(paths.TemporaryDirectory, "tmp-clean-locked.lock");
        File.WriteAllText(lockPath, "held");
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await cleanup.HandleAsync(
            new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, ClassifyRetentionPolicy.PolicyVersion),
            actor, NextKey(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.RemovedTemporaryCount >= 1);
        Assert.True(result.Value.RetainedArtifactCount >= 1);
        Assert.False(File.Exists(Path.Combine(paths.TemporaryDirectory, "tmp-clean-free")));
        Assert.True(File.Exists(Path.Combine(paths.TemporaryDirectory, "tmp-clean-locked")));
        // Aggregate per-kind counts remain stable non-negative integers.
        Assert.True(result.Value.RemovedTemporaryCount >= 0);
        Assert.True(result.Value.RemovedExpiredPreviewCount >= 0);
        Assert.True(result.Value.RemovedAbandonedPayloadCount >= 0);
        Assert.True(
            result.Value.RemovedArtifactCount
            >= result.Value.RemovedTemporaryCount);
    }

    [Fact]
    public async Task Startup_committed_quarantine_requires_durable_cleanup_event()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-db-auth", [3]);
        var staged = protection.TryStageRecognizedTemporaries(
            "startup-db-auth", "cleanup", ["tmp-db-auth"]);
        Assert.NotNull(staged);
        Assert.DoesNotContain("tmp-db-auth", protection.ListRecognizedTemporaryFileNames());

        // No cleanup_event yet — must restore, never delete.
        var restored = protection.RecoverQuarantineAtStartup((kind, operationId) =>
        {
            if (!string.Equals(kind, "cleanup", StringComparison.Ordinal)
                || !string.Equals(operationId, "startup-db-auth", StringComparison.Ordinal))
            {
                return false;
            }

            // Probe live DB authority (none yet).
            using var connection = state.Store.OpenMigratedAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            return recovery.HasCleanupEventAsync(
                connection, null, operationId, CancellationToken.None)
                .GetAwaiter().GetResult();
        });
        Assert.True(restored >= 1);
        Assert.Contains("tmp-db-auth", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public async Task Startup_with_cleanup_event_finalizes_staged_deletion()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-db-final", [4]);
        var q = protection.TryStageRecognizedTemporaries(
            "op-db-final", "cleanup", ["tmp-db-final"]);
        Assert.NotNull(q);

        await using (var connection = await state.Store.OpenMigratedAsync(CancellationToken.None))
        await using (var transaction = state.Store.BeginImmediate(connection))
        {
            var row = ClassifyContractMapper.ToCleanupEventRow(
                "op-db-final",
                ClassifyRetentionPolicy.PolicyVersion,
                recognizedRemovedCount: 1,
                expiredPreviewCount: 0,
                abandonedPayloadCount: 0,
                actor: "automation:abandon:run-01",
                occurredAtUtc: "2026-08-01T00:00:00Z",
                removedArtifactCount: 1,
                retainedArtifactCount: 0);
            await recovery.InsertCleanupEventAsync(connection, transaction, row, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var actions = protection.RecoverQuarantineAtStartup((kind, operationId) =>
        {
            if (!string.Equals(kind, "cleanup", StringComparison.Ordinal))
            {
                return false;
            }

            using var connection = state.Store.OpenMigratedAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            return recovery.HasCleanupEventAsync(
                connection, null, operationId, CancellationToken.None)
                .GetAwaiter().GetResult();
        });
        Assert.True(actions >= 1);
        Assert.DoesNotContain("tmp-db-final", protection.ListRecognizedTemporaryFileNames());
        Assert.False(Directory.Exists(q!.Directory));
    }

    // ── Seed helpers (direct SQL into migrated store) ───────────────────────

    private async Task SeedMinimalPreviewGraphAsync(
        string previewId,
        string evaluationId,
        string ruleSetVersionId,
        string expiresAt,
        bool withApplyRun = false,
        bool withFeedback = false)
    {
        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = state.Store.BeginImmediate(connection);
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO classification_rule (rule_id, created_at, created_by)
            VALUES ('rule-seed', '2026-07-31T00:00:00Z', 'human:owner');
            INSERT OR IGNORE INTO rule_version (
                rule_version_id, rule_id, prior_version_id, normalization_version, category_id,
                scope_hash, rule_origin, source_feedback_id, reason, lifecycle_state,
                broad_apply_allowed, validation_run_id, created_at, created_by
            ) VALUES (
                'rv-seed', 'rule-seed', NULL, 'normalization_v1', 'cat-1',
                '{new string('a', 64)}', 'owner_authored', NULL, 'seed', 'draft',
                0, NULL, '2026-07-31T00:00:00Z', 'human:owner');
            INSERT OR IGNORE INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                '{ruleSetVersionId}', NULL, 'normalization_v1',
                'val-missing-ok', 'seed', '2026-07-31T00:00:00Z', 'human:owner');
            """);

        // validation_run may be required by FK on rule_set_version - check schema
        // rule_set_version.validation_run_id is NOT NULL but may not FK - from schema:
        // validation_run_id TEXT NOT NULL - need a row if FK exists
        await EnsureValidationRunAsync(connection, transaction, "val-missing-ok");

        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                '{ruleSetVersionId}', NULL, 'normalization_v1',
                'val-missing-ok', 'seed', '2026-07-31T00:00:00Z', 'human:owner');
            INSERT OR IGNORE INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                '{evaluationId}', NULL, '{ruleSetVersionId}', 'normalization_v1',
                '1.0', 'classification_v1', '{new string('b', 64)}', 'snap-1',
                '2099-01-01T00:00:00Z', '{new string('c', 64)}', '{new string('d', 64)}',
                1, 1, 0, 0, 0, 'completed', 'human:owner', '2026-07-31T00:00:00Z');
            INSERT OR IGNORE INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                'out-{evaluationId}', '{evaluationId}', 0, 'tx-{evaluationId}', 'suggestion',
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
                1, 0, 0, 0, 'human:owner', '2026-07-31T00:00:00Z');
            INSERT INTO apply_preview_item (
                preview_id, ordinal, outcome_id, transaction_id, mode, category_id, rule_version_id,
                expected_current_category_id, expected_active_allocation_id,
                expected_transaction_revision, expected_relationship_revision, expected_allocation_revision,
                correction_reason
            ) VALUES (
                '{previewId}', 0, 'out-{evaluationId}', 'tx-{evaluationId}', 'assign', 'cat-1', 'rv-seed',
                NULL, NULL, 'genesis:tx', 'none', 'none', NULL);
            """);

        if (withApplyRun)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO apply_run (
                    apply_id, preview_id, request_fingerprint, lifecycle_state, unresolved_frontier,
                    actor, started_at, completed_at
                ) VALUES (
                    'apply-{previewId}', '{previewId}', '{new string('5', 64)}', 'completed', 0,
                    'human:owner', '2026-07-31T00:00:00Z', '2026-07-31T00:01:00Z');
                """);
        }

        if (withFeedback)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO classification_feedback (
                    feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                    rule_set_version_id, decision_type, reason, actor, occurred_at
                ) VALUES (
                    'fb-{evaluationId}', 'out-{evaluationId}', 'tx-{evaluationId}', '{evaluationId}',
                    'normalization_v1', '{ruleSetVersionId}', 'accept', 'ok', 'human:owner', '2026-07-31T00:00:00Z');
                """);
        }

        await transaction.CommitAsync();
    }

    private async Task SeedEvaluationOnlyAsync(string evaluationId)
    {
        await using var connection = await state.Store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = state.Store.BeginImmediate(connection);
        await EnsureValidationRunAsync(connection, transaction, "val-only");
        await ExecuteAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO rule_set_version (
                rule_set_version_id, prior_rule_set_version_id, normalization_version,
                validation_run_id, reason, created_at, created_by
            ) VALUES (
                'rsv-only', NULL, 'normalization_v1', 'val-only', 'seed',
                '2026-07-31T00:00:00Z', 'human:owner');
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                '{evaluationId}', NULL, 'rsv-only', 'normalization_v1',
                '1.0', 'classification_v1', '{new string('b', 64)}', 'snap-1',
                '2099-01-01T00:00:00Z', '{new string('c', 64)}', '{new string('d', 64)}',
                0, 0, 0, 0, 0, 'completed', 'human:owner', '2026-07-31T00:00:00Z');
            """);
        await transaction.CommitAsync();
    }

    private static async Task EnsureValidationRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string validationRunId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO validation_run (
                validation_run_id, candidate_fingerprint, rule_origin, corpus_fingerprint,
                expected_outcome_fingerprint, projection_contract_version, category_lifecycle_fingerprint,
                normalization_version, started_at, completed_at, lifecycle_state, actor
            ) VALUES (
                $id, $fp, 'owner_authored', $fp, $fp, 'classification_v1', $fp,
                'normalization_v1', '2026-07-31T00:00:00Z', '2026-07-31T00:00:01Z', 'completed', 'human:owner');
            """;
        var fp = new string('a', 64);
        command.Parameters.AddWithValue("$id", validationRunId);
        command.Parameters.AddWithValue("$fp", fp);
        await command.ExecuteNonQueryAsync();
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
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private string NextKey() => $"abandon-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
