using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget.Plans;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Xunit;

namespace Tally.Tests.Budget.Recovery;

/// <summary>
/// TASK-BUDGET-GATE-ATOMIC-RECOVERY / NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS /
/// NFR-BUDGET-ATTRIBUTABLE-HISTORY / TC-BUDGET-ATOMIC-DURABLE-MUTATIONS.
/// Failure-injection restart matrix against real budget.db — never mocks the transaction.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetAtomicRecoveryTests
{
    private static readonly UnixFileMode OwnerDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode OwnerFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // ── Draft cutpoints ──────────────────────────────────────────────────────

    /// <summary>
    /// Named draft cutpoints: before_validation, after_validation, replay_lookup,
    /// revision_insert, entry_insert, events, outcome_references, commit, result_delivery.
    /// </summary>
    [Theory]
    [InlineData("before_validation")]
    [InlineData("after_validation")]
    [InlineData("replay_lookup")]
    [InlineData("revision_insert")]
    [InlineData("entry_insert")]
    [InlineData("events")]
    [InlineData("outcome_references")]
    [InlineData("commit")]
    [InlineData("result_delivery")]
    public async Task Draft_cutpoint_restart_is_prior_or_complete(string cutpoint)
    {
        var root = NewRoot("draft");
        try
        {
            var prior = await CaptureStateAsync(root);
            var key = $"draft-key-{cutpoint}";
            var identity = DraftIdentity(key);
            var interrupted = await InterruptDraftAsync(root, identity, cutpoint);

            // Restart inspection: reopen a brand-new store against the same durable files.
            var afterFault = await CaptureStateAsync(root);
            AssertPriorOrCompleteDraft(prior, afterFault, interrupted);
            AssertAtMostOneActive(afterFault);
            AssertNoPartialDraftChains(afterFault);
            AssertOwnerOnlyArtifacts(root);

            // Retry the same key: pre-commit cutpoints commit once; post-commit replays exactly.
            var retry = await RetryDraftAsync(root, identity, cutpoint, interrupted);
            var afterRetry = await CaptureStateAsync(root);

            AssertExactlyOneCompleteDraftOutcome(afterRetry, key, retry);
            AssertAtMostOneActive(afterRetry);
            AssertHistoryReconciles(afterRetry);
            AssertOwnerOnlyArtifacts(root);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Activation cutpoints ─────────────────────────────────────────────────

    /// <summary>
    /// Named activation cutpoints: before_validation, after_validation, replay_lookup,
    /// prior_supersession, activation, active_pointer, events, outcome_references,
    /// commit, result_delivery.
    /// </summary>
    [Theory]
    [InlineData("before_validation")]
    [InlineData("after_validation")]
    [InlineData("replay_lookup")]
    [InlineData("prior_supersession")]
    [InlineData("activation")]
    [InlineData("active_pointer")]
    [InlineData("events")]
    [InlineData("outcome_references")]
    [InlineData("commit")]
    [InlineData("result_delivery")]
    public async Task Activate_cutpoint_restart_is_prior_or_complete(string cutpoint)
    {
        var root = NewRoot("activate");
        try
        {
            await SeedPriorActiveAndDraftAsync(root);
            var prior = await CaptureStateAsync(root);
            Assert.Equal(1, prior.ActiveRevisionCount);
            Assert.Equal("rev-active", prior.ActiveRevisionId);

            var key = $"activate-key-{cutpoint}";
            var identity = ActivateIdentity(key, "rev-draft");
            var interrupted = await InterruptActivateAsync(root, identity, cutpoint);

            var afterFault = await CaptureStateAsync(root);
            AssertPriorOrCompleteActivate(prior, afterFault, interrupted);
            AssertAtMostOneActive(afterFault);
            AssertNoPartialActivationChains(afterFault, prior);
            AssertOwnerOnlyArtifacts(root);

            var retry = await RetryActivateAsync(root, identity, cutpoint, interrupted);
            var afterRetry = await CaptureStateAsync(root);

            AssertExactlyOneCompleteActivateOutcome(afterRetry, key, retry, prior);
            AssertAtMostOneActive(afterRetry);
            AssertHistoryReconciles(afterRetry);
            AssertOwnerOnlyArtifacts(root);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Cross-cutting guarantees ─────────────────────────────────────────────

    [Fact]
    public async Task Post_commit_result_delivery_replay_matches_event_time_snapshot_exactly()
    {
        var root = NewRoot("replay-exact");
        try
        {
            var store = await OpenStoreAsync(root);
            var executor = new BudgetMutationExecutor(store);
            var identity = DraftIdentity("exact-replay-key");

            executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;
            var fault = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                executor.ExecuteAsync(
                    identity,
                    (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1"),
                    CancellationToken.None));
            Assert.Equal(BudgetMutationFaultPoint.AfterCommit, fault.Point);

            // Force restart boundary: dispose connection surface, reopen.
            store = await OpenStoreAsync(root);
            executor = new BudgetMutationExecutor(store);
            var firstRead = await CaptureStateAsync(root);

            var replay = await executor.ExecuteAsync(
                identity,
                (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-X", "rev-X", "evt-X"),
                CancellationToken.None);

            Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
            Assert.Equal("rev-1", replay.Snapshot!.ResultRevisionId);
            Assert.Equal("plan-1", replay.Snapshot.PlanId);
            Assert.Equal(["evt-1"], replay.Snapshot.LifecycleEvents.Select(e => e.EventId));
            Assert.Equal(BudgetRevisionStatus.Draft, replay.Snapshot.Revision.Status);
            Assert.Equal("owner", replay.Snapshot.LifecycleEvents.Single().ActorLabel);
            Assert.Equal("draft reason", replay.Snapshot.LifecycleEvents.Single().Reason);
            Assert.Equal(firstRead.RevisionPayloadHashes["rev-1"], replay.Snapshot.Revision.PayloadHash);
            Assert.Equal(firstRead.IdempotencyResultHashes.Single().Value, replay.Snapshot.ResultHash);

            var second = await CaptureStateAsync(root);
            Assert.Equal(firstRead.RevisionCount, second.RevisionCount);
            Assert.Equal(firstRead.LifecycleEventCount, second.LifecycleEventCount);
            Assert.Equal(firstRead.IdempotencyCount, second.IdempotencyCount);
            AssertOwnerOnlyArtifacts(root);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Forced_termination_artifacts_remain_owner_only_after_reopen()
    {
        var root = NewRoot("owner-only");
        try
        {
            var store = await OpenStoreAsync(root);
            var executor = new BudgetMutationExecutor(store);
            executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

            await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                executor.ExecuteAsync(
                    DraftIdentity("owner-key"),
                    (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1"),
                    CancellationToken.None));

            // Touch recognized sidecars if absent, then reopen and assert protection.
            var paths = store.Paths;
            if (!File.Exists(paths.LockPath))
            {
                await File.WriteAllTextAsync(paths.LockPath, "lock");
            }

            if (!File.Exists(paths.AtomicPath))
            {
                await File.WriteAllTextAsync(paths.AtomicPath, "atomic");
            }

            store = await OpenStoreAsync(root);
            AssertOwnerOnlyArtifacts(root);
            Assert.Equal(OwnerFile, File.GetUnixFileMode(paths.DatabasePath));
            if (File.Exists(paths.WalPath))
            {
                Assert.Equal(OwnerFile, File.GetUnixFileMode(paths.WalPath));
            }

            if (File.Exists(paths.ShmPath))
            {
                Assert.Equal(OwnerFile, File.GetUnixFileMode(paths.ShmPath));
            }

            if (File.Exists(paths.LockPath))
            {
                Assert.Equal(OwnerFile, File.GetUnixFileMode(paths.LockPath));
            }

            if (File.Exists(paths.AtomicPath))
            {
                Assert.Equal(OwnerFile, File.GetUnixFileMode(paths.AtomicPath));
            }

            // Synchronous FULL remains enforced on reopen.
            await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
            Assert.Equal(2L, await ScalarLongAsync(connection, "PRAGMA synchronous;"));
            Assert.Equal(
                "wal",
                Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Activate_replacement_history_reconciles_prior_result_replacement_and_pointer()
    {
        var root = NewRoot("history");
        try
        {
            await SeedPriorActiveAndDraftAsync(root);
            var store = await OpenStoreAsync(root);
            var executor = new BudgetMutationExecutor(store);
            var identity = ActivateIdentity("history-key", "rev-draft");

            executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;
            await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                executor.ExecuteAsync(
                    identity,
                    (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
                    CancellationToken.None));

            store = await OpenStoreAsync(root);
            executor = new BudgetMutationExecutor(store);
            var replay = await executor.ExecuteAsync(
                identity,
                (_, _, _) => Task.FromResult(BudgetMutationWorkResult.Failure("must-not-run")),
                CancellationToken.None);

            Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
            Assert.Equal("rev-draft", replay.Snapshot!.ResultRevisionId);
            Assert.Equal("rev-active", replay.Snapshot.PriorActiveRevisionId);
            Assert.Equal(["evt-sup", "evt-act"], replay.Snapshot.LifecycleEvents.Select(e => e.EventId));
            Assert.Equal(BudgetRevisionStatus.Active, replay.Snapshot.Revision.Status);

            var state = await CaptureStateAsync(root);
            Assert.Equal(1, state.ActiveRevisionCount);
            Assert.Equal("rev-draft", state.ActiveRevisionId);
            Assert.Equal(BudgetRevisionStatus.Superseded, state.RevisionStatuses["rev-active"]);
            Assert.Equal("rev-draft", state.SupersededBy["rev-active"]);

            var sup = state.LifecycleEvents.Single(e => e.EventType == "RevisionSuperseded");
            Assert.Equal("rev-active", sup.RevisionId);
            Assert.Equal("Active", sup.PriorStatus);
            Assert.Equal("Superseded", sup.ResultingStatus);
            Assert.Equal("rev-draft", sup.ReplacementRevisionId);
            Assert.Equal("owner", sup.ActorLabel);
            Assert.Equal("activate reason", sup.Reason);

            var act = state.LifecycleEvents.Single(e => e.EventType == "RevisionActivated" && e.RevisionId == "rev-draft");
            Assert.Equal("Draft", act.PriorStatus);
            Assert.Equal("Active", act.ResultingStatus);
            Assert.Null(act.ReplacementRevisionId);
            Assert.True(act.EventSequence > sup.EventSequence);
            AssertOwnerOnlyArtifacts(root);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Interruption helpers ─────────────────────────────────────────────────

    private sealed record InterruptOutcome(
        bool Committed,
        bool Threw,
        string? FaultKind,
        BudgetMutationSnapshot? Snapshot);

    private static async Task<InterruptOutcome> InterruptDraftAsync(
        string root,
        BudgetMutationIdentity identity,
        string cutpoint)
    {
        var store = await OpenStoreAsync(root);
        var executor = new BudgetMutationExecutor(store);

        switch (cutpoint)
        {
            case "before_validation":
            {
                var rejected = await executor.ExecuteAsync(
                    new BudgetMutationIdentity(" ", identity.ContractVersion, identity.OperationId, identity.RequestHash),
                    (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1"),
                    CancellationToken.None);
                Assert.Equal(BudgetMutationDisposition.Rejected, rejected.Disposition);
                Assert.Equal(Tally.Contracts.Budget.BudgetErrors.IdempotencyRequired, rejected.ErrorCode);
                return new InterruptOutcome(false, false, "rejected", null);
            }
            case "after_validation":
            {
                // Identity passes validation; throw before any durable writes inside the writer tx.
                var ex = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (_, _, _) => throw new BudgetMutationFaultException(BudgetMutationFaultPoint.BeforeCommit),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, ex.Point.ToString(), null);
            }
            case "replay_lookup":
            {
                // Lookup runs first on Miss; throw at the start of mutate after lookup completes.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (_, _, _) => throw new InvalidOperationException("fault:replay_lookup"),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, "replay_lookup", null);
            }
            case "revision_insert":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await InsertPlanAsync(store, c, t, ct, "plan-1");
                        await InsertRevisionOnlyAsync(c, t, ct, "plan-1", "rev-1");
                        throw new InvalidOperationException("fault:revision_insert");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "revision_insert", null);
            }
            case "entry_insert":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await InsertPlanAsync(store, c, t, ct, "plan-1");
                        await InsertRevisionOnlyAsync(c, t, ct, "plan-1", "rev-1");
                        await InsertEntryOnlyAsync(c, t, ct, "rev-1", "cat-a", 10);
                        throw new InvalidOperationException("fault:entry_insert");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "entry_insert", null);
            }
            case "events":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1");
                        throw new InvalidOperationException("fault:events");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "events", null);
            }
            case "outcome_references":
            case "commit":
            {
                executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;
                var ex = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1"),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, ex.Point.ToString(), null);
            }
            case "result_delivery":
            {
                executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;
                var ex = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (c, t, ct) => DraftMutateComplete(c, t, ct, store, "plan-1", "rev-1", "evt-1"),
                        CancellationToken.None));
                return new InterruptOutcome(true, true, ex.Point.ToString(), null);
            }
            default:
                throw new InvalidOperationException($"Unknown draft cutpoint '{cutpoint}'.");
        }
    }

    private static async Task<BudgetMutationExecutionResult> RetryDraftAsync(
        string root,
        BudgetMutationIdentity identity,
        string cutpoint,
        InterruptOutcome interrupted)
    {
        var store = await OpenStoreAsync(root);
        var executor = new BudgetMutationExecutor(store);
        // Distinct revision/event ids prove at-most-once for post-commit; pre-commit uses stable ids.
        var planId = interrupted.Committed ? "plan-X" : "plan-1";
        var revisionId = interrupted.Committed ? "rev-X" : "rev-1";
        var eventId = interrupted.Committed ? "evt-X" : "evt-1";

        if (cutpoint == "before_validation")
        {
            // Original key was blank; retry with the real key.
            return await executor.ExecuteAsync(
                identity,
                (c, t, ct) => DraftMutateComplete(c, t, ct, store, planId, revisionId, eventId),
                CancellationToken.None);
        }

        return await executor.ExecuteAsync(
            identity,
            (c, t, ct) => DraftMutateComplete(c, t, ct, store, planId, revisionId, eventId),
            CancellationToken.None);
    }

    private static async Task<InterruptOutcome> InterruptActivateAsync(
        string root,
        BudgetMutationIdentity identity,
        string cutpoint)
    {
        var store = await OpenStoreAsync(root);
        var executor = new BudgetMutationExecutor(store);

        switch (cutpoint)
        {
            case "before_validation":
            {
                var rejected = await executor.ExecuteAsync(
                    new BudgetMutationIdentity(string.Empty, identity.ContractVersion, identity.OperationId, identity.RequestHash),
                    (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
                    CancellationToken.None);
                Assert.Equal(BudgetMutationDisposition.Rejected, rejected.Disposition);
                return new InterruptOutcome(false, false, "rejected", null);
            }
            case "after_validation":
            {
                await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (_, _, _) => throw new BudgetMutationFaultException(BudgetMutationFaultPoint.BeforeCommit),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, "after_validation", null);
            }
            case "replay_lookup":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (_, _, _) => throw new InvalidOperationException("fault:replay_lookup"),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, "replay_lookup", null);
            }
            case "prior_supersession":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await SupersedePriorOnlyAsync(c, t, ct, store, "plan-1", "rev-active", "rev-draft", "evt-sup");
                        throw new InvalidOperationException("fault:prior_supersession");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "prior_supersession", null);
            }
            case "activation":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await SupersedePriorOnlyAsync(c, t, ct, store, "plan-1", "rev-active", "rev-draft", "evt-sup");
                        await ActivateStatusOnlyAsync(c, t, ct, "rev-draft");
                        throw new InvalidOperationException("fault:activation");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "activation", null);
            }
            case "active_pointer":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await SupersedePriorOnlyAsync(c, t, ct, store, "plan-1", "rev-active", "rev-draft", "evt-sup");
                        await ActivateStatusOnlyAsync(c, t, ct, "rev-draft");
                        await SetActivePointerOnlyAsync(c, t, ct, "plan-1", "rev-draft");
                        throw new InvalidOperationException("fault:active_pointer");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "active_pointer", null);
            }
            case "events":
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executor.ExecuteAsync(identity, async (c, t, ct) =>
                    {
                        await ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act");
                        throw new InvalidOperationException("fault:events");
                    }, CancellationToken.None));
                return new InterruptOutcome(false, true, "events", null);
            }
            case "outcome_references":
            case "commit":
            {
                executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;
                await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
                        CancellationToken.None));
                return new InterruptOutcome(false, true, "BeforeCommit", null);
            }
            case "result_delivery":
            {
                executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;
                await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
                    executor.ExecuteAsync(
                        identity,
                        (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
                        CancellationToken.None));
                return new InterruptOutcome(true, true, "AfterCommit", null);
            }
            default:
                throw new InvalidOperationException($"Unknown activate cutpoint '{cutpoint}'.");
        }
    }

    private static async Task<BudgetMutationExecutionResult> RetryActivateAsync(
        string root,
        BudgetMutationIdentity identity,
        string cutpoint,
        InterruptOutcome interrupted)
    {
        var store = await OpenStoreAsync(root);
        var executor = new BudgetMutationExecutor(store);
        // Post-commit retries must not re-run activation work; pre-commit reuses intended ids.
        if (interrupted.Committed)
        {
            return await executor.ExecuteAsync(
                identity,
                (_, _, _) => Task.FromResult(BudgetMutationWorkResult.Failure("must-not-run")),
                CancellationToken.None);
        }

        if (cutpoint == "before_validation")
        {
            return await executor.ExecuteAsync(
                identity,
                (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
                CancellationToken.None);
        }

        return await executor.ExecuteAsync(
            identity,
            (c, t, ct) => ActivateMutateComplete(c, t, ct, store, "plan-1", "rev-draft", "rev-active", "evt-sup", "evt-act"),
            CancellationToken.None);
    }

    // ── State capture & assertions ───────────────────────────────────────────

    private sealed record LifecycleSnap(
        string EventId,
        string PlanId,
        string RevisionId,
        string EventType,
        string ActorKind,
        string ActorLabel,
        string? ActorRunId,
        string Reason,
        string OccurredAtUtc,
        string? PriorStatus,
        string? ResultingStatus,
        string? ReplacementRevisionId,
        int EventSequence);

    private sealed record DurableState(
        int PlanCount,
        int RevisionCount,
        int EntryCount,
        int LifecycleEventCount,
        int IdempotencyCount,
        int ActiveRevisionCount,
        string? ActiveRevisionId,
        IReadOnlyDictionary<string, BudgetRevisionStatus> RevisionStatuses,
        IReadOnlyDictionary<string, string> RevisionPayloadHashes,
        IReadOnlyDictionary<string, string> SupersededBy,
        IReadOnlyList<LifecycleSnap> LifecycleEvents,
        IReadOnlyDictionary<string, string> IdempotencyResultHashes,
        IReadOnlyDictionary<string, string> IdempotencyLifecycleIds,
        IReadOnlySet<string> RevisionIds,
        IReadOnlySet<string> EntryRevisionIds,
        string Fingerprint);

    private static async Task<DurableState> CaptureStateAsync(string root)
    {
        // Always reopen — never reuse a connection from the interrupted attempt.
        var store = await OpenStoreAsync(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);

        var planCount = (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan;");
        var revisionCount = (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_revision;");
        var entryCount = (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_plan_entry;");
        var eventCount = (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idemCount = (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM budget_idempotency_record;");
        var activeCount = (int)await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';");

        string? activeRevisionId = null;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT active_revision_id FROM budget_plan LIMIT 1;";
            var value = await cmd.ExecuteScalarAsync();
            if (value is not null and not DBNull)
            {
                activeRevisionId = Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        var statuses = new Dictionary<string, BudgetRevisionStatus>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var supersededBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT revision_id, status, payload_hash, superseded_by_revision_id
                FROM budget_plan_revision
                ORDER BY revision_number;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                revisionIds.Add(id);
                statuses[id] = BudgetRowMapper.ParseStatus(reader.GetString(1));
                hashes[id] = reader.GetString(2);
                if (!reader.IsDBNull(3))
                {
                    supersededBy[id] = reader.GetString(3);
                }
            }
        }

        var entryRevisionIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT revision_id FROM budget_plan_entry;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entryRevisionIds.Add(reader.GetString(0));
            }
        }

        var events = new List<LifecycleSnap>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                       reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence
                FROM budget_lifecycle_event
                ORDER BY plan_id, event_sequence;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new LifecycleSnap(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt32(12)));
            }
        }

        var resultHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var lifeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT key_digest, result_hash, lifecycle_event_ids
                FROM budget_idempotency_record;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                resultHashes[reader.GetString(0)] = reader.GetString(1);
                lifeIds[reader.GetString(0)] = reader.GetString(2);
            }
        }

        var fingerprint = string.Join(
            '|',
            planCount,
            revisionCount,
            entryCount,
            eventCount,
            idemCount,
            activeCount,
            activeRevisionId ?? "-",
            string.Join(',', statuses.Select(kv => kv.Key + "=" + kv.Value)),
            string.Join(',', events.Select(e => e.EventId + ":" + e.EventType + ":" + e.EventSequence)),
            string.Join(',', resultHashes.Select(kv => kv.Key[..8] + "=" + kv.Value[..8])));

        return new DurableState(
            planCount,
            revisionCount,
            entryCount,
            eventCount,
            idemCount,
            activeCount,
            activeRevisionId,
            statuses,
            hashes,
            supersededBy,
            events,
            resultHashes,
            lifeIds,
            revisionIds,
            entryRevisionIds,
            fingerprint);
    }

    private static void AssertPriorOrCompleteDraft(DurableState prior, DurableState after, InterruptOutcome interrupted)
    {
        if (!interrupted.Committed)
        {
            // Unchanged pre-operation state — exact fingerprint, not aggregate-only.
            Assert.Equal(prior.Fingerprint, after.Fingerprint);
            Assert.Equal(prior.PlanCount, after.PlanCount);
            Assert.Equal(prior.RevisionCount, after.RevisionCount);
            Assert.Equal(prior.EntryCount, after.EntryCount);
            Assert.Equal(prior.LifecycleEventCount, after.LifecycleEventCount);
            Assert.Equal(prior.IdempotencyCount, after.IdempotencyCount);
            Assert.Equal(prior.RevisionIds, after.RevisionIds);
            return;
        }

        // Exactly one complete draft chain.
        Assert.Equal(1, after.PlanCount);
        Assert.Equal(1, after.RevisionCount);
        Assert.Equal(1, after.EntryCount);
        Assert.Equal(1, after.LifecycleEventCount);
        Assert.Equal(1, after.IdempotencyCount);
        Assert.Equal(0, after.ActiveRevisionCount);
        Assert.Null(after.ActiveRevisionId);
        Assert.Contains("rev-1", after.RevisionIds);
        Assert.Contains("rev-1", after.EntryRevisionIds);
        Assert.Equal(BudgetRevisionStatus.Draft, after.RevisionStatuses["rev-1"]);
        var evt = Assert.Single(after.LifecycleEvents);
        Assert.Equal("DraftCreated", evt.EventType);
        Assert.Equal("rev-1", evt.RevisionId);
        Assert.Equal("Draft", evt.ResultingStatus);
        Assert.Null(evt.PriorStatus);
        Assert.Equal("owner", evt.ActorLabel);
        Assert.Equal("draft reason", evt.Reason);
        Assert.Equal(1, evt.EventSequence);
    }

    private static void AssertPriorOrCompleteActivate(DurableState prior, DurableState after, InterruptOutcome interrupted)
    {
        if (!interrupted.Committed)
        {
            Assert.Equal(prior.Fingerprint, after.Fingerprint);
            Assert.Equal(prior.ActiveRevisionId, after.ActiveRevisionId);
            Assert.Equal(1, after.ActiveRevisionCount);
            Assert.Equal(BudgetRevisionStatus.Active, after.RevisionStatuses["rev-active"]);
            Assert.Equal(BudgetRevisionStatus.Draft, after.RevisionStatuses["rev-draft"]);
            return;
        }

        Assert.Equal(1, after.ActiveRevisionCount);
        Assert.Equal("rev-draft", after.ActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, after.RevisionStatuses["rev-draft"]);
        Assert.Equal(BudgetRevisionStatus.Superseded, after.RevisionStatuses["rev-active"]);
        Assert.Equal("rev-draft", after.SupersededBy["rev-active"]);
        Assert.Equal(1, after.IdempotencyCount);
        Assert.Contains(after.LifecycleEvents, e => e.EventType == "RevisionSuperseded" && e.RevisionId == "rev-active");
        Assert.Contains(after.LifecycleEvents, e => e.EventType == "RevisionActivated" && e.RevisionId == "rev-draft");
    }

    private static void AssertAtMostOneActive(DurableState state) =>
        Assert.True(state.ActiveRevisionCount <= 1, $"Expected at most one Active, found {state.ActiveRevisionCount}.");

    private static void AssertNoPartialDraftChains(DurableState state)
    {
        // Every revision must have matching entries + at least one lifecycle event; no orphan idempotency.
        foreach (var revisionId in state.RevisionIds)
        {
            Assert.Contains(revisionId, state.EntryRevisionIds);
            Assert.Contains(state.LifecycleEvents, e => e.RevisionId == revisionId);
        }

        if (state.IdempotencyCount > 0)
        {
            Assert.Equal(state.IdempotencyCount, state.IdempotencyResultHashes.Count);
            Assert.All(state.IdempotencyLifecycleIds.Values, ids => Assert.False(string.IsNullOrWhiteSpace(ids)));
        }
    }

    private static void AssertNoPartialActivationChains(DurableState state, DurableState prior)
    {
        AssertAtMostOneActive(state);
        // Pointer and Active status must agree when present.
        if (state.ActiveRevisionId is not null)
        {
            Assert.Equal(BudgetRevisionStatus.Active, state.RevisionStatuses[state.ActiveRevisionId]);
        }

        // No double-Active; superseded prior always has replacement when superseded.
        foreach (var (revisionId, status) in state.RevisionStatuses)
        {
            if (status == BudgetRevisionStatus.Superseded)
            {
                Assert.True(state.SupersededBy.ContainsKey(revisionId), $"Superseded {revisionId} missing replacement.");
            }
        }

        // Pre-commit must not leave dangling supersession events beyond prior.
        if (state.Fingerprint == prior.Fingerprint)
        {
            Assert.Equal(prior.LifecycleEventCount, state.LifecycleEventCount);
        }
    }

    private static void AssertExactlyOneCompleteDraftOutcome(
        DurableState state,
        string key,
        BudgetMutationExecutionResult retry)
    {
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(1, state.RevisionCount);
        Assert.Equal(1, state.LifecycleEventCount);
        Assert.Equal(1, state.IdempotencyCount);
        Assert.Equal(0, state.ActiveRevisionCount);

        var digest = BudgetMutationCanonicalizer.DigestKey(key);
        Assert.True(state.IdempotencyResultHashes.ContainsKey(digest));
        Assert.Equal(retry.Snapshot!.ResultHash, state.IdempotencyResultHashes[digest]);
        Assert.Equal("rev-1", retry.Snapshot.ResultRevisionId);
        Assert.Equal(BudgetRevisionStatus.Draft, retry.Snapshot.Revision.Status);
        Assert.Equal(["evt-1"], retry.Snapshot.LifecycleEvents.Select(e => e.EventId));

        if (retry.Disposition == BudgetMutationDisposition.Replayed)
        {
            // Post-commit path.
            Assert.Equal("rev-1", state.RevisionIds.Single());
        }
        else
        {
            Assert.Equal(BudgetMutationDisposition.Committed, retry.Disposition);
        }
    }

    private static void AssertExactlyOneCompleteActivateOutcome(
        DurableState state,
        string key,
        BudgetMutationExecutionResult retry,
        DurableState prior)
    {
        Assert.True(retry.IsSuccess, retry.ErrorCode);
        Assert.Equal(1, state.ActiveRevisionCount);
        Assert.Equal("rev-draft", state.ActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, state.RevisionStatuses["rev-draft"]);
        Assert.Equal(BudgetRevisionStatus.Superseded, state.RevisionStatuses["rev-active"]);
        Assert.Equal(1, state.IdempotencyCount);

        var digest = BudgetMutationCanonicalizer.DigestKey(key);
        Assert.True(state.IdempotencyResultHashes.ContainsKey(digest));
        Assert.Equal(retry.Snapshot!.ResultHash, state.IdempotencyResultHashes[digest]);
        Assert.Equal("rev-draft", retry.Snapshot.ResultRevisionId);
        Assert.Equal("rev-active", retry.Snapshot.PriorActiveRevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, retry.Snapshot.Revision.Status);

        // Exactly one supersession + one activation for the new revision (prior may already have activation).
        Assert.Equal(1, state.LifecycleEvents.Count(e => e.EventType == "RevisionSuperseded"));
        Assert.Equal(1, state.LifecycleEvents.Count(e => e.EventType == "RevisionActivated" && e.RevisionId == "rev-draft"));
        Assert.True(state.LifecycleEventCount >= prior.LifecycleEventCount);
    }

    private static void AssertHistoryReconciles(DurableState state)
    {
        // Sequences are positive and unique per plan; chains have prior/result statuses.
        var byPlan = state.LifecycleEvents.GroupBy(e => e.PlanId);
        foreach (var group in byPlan)
        {
            var ordered = group.OrderBy(e => e.EventSequence).ToArray();
            Assert.Equal(ordered.Select(e => e.EventSequence).Distinct().Count(), ordered.Length);
            Assert.All(ordered, e => Assert.True(e.EventSequence > 0));
            Assert.All(ordered, e => Assert.False(string.IsNullOrWhiteSpace(e.ActorLabel)));
            Assert.All(ordered, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
            Assert.All(ordered, e => Assert.False(string.IsNullOrWhiteSpace(e.OccurredAtUtc)));
        }

        if (state.ActiveRevisionId is not null)
        {
            Assert.Equal(BudgetRevisionStatus.Active, state.RevisionStatuses[state.ActiveRevisionId]);
        }
    }

    private static void AssertOwnerOnlyArtifacts(string root)
    {
        var store = new BudgetStateStore(root);
        store.RequireOwnerOnlyArtifacts();
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.DataRoot));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(store.Paths.BudgetDirectory));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(store.Paths.DatabasePath));
        foreach (var path in store.Paths.RecognizedArtifactPaths())
        {
            if (File.Exists(path))
            {
                Assert.Equal(OwnerFile, File.GetUnixFileMode(path));
            }
        }
    }

    // ── Seed / mutate builders ───────────────────────────────────────────────

    private static async Task SeedPriorActiveAndDraftAsync(string root)
    {
        var store = await OpenStoreAsync(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var tx = store.BeginImmediate(connection);
        await store.InsertPlanAsync(connection, tx, Plan("plan-1"), CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection,
            tx,
            Draft("rev-active", "plan-1", 1, payloadSeed: "active"),
            [new BudgetPlanEntryRow("rev-active", "cat-a", 10)],
            DraftEvent("evt-draft-active", "plan-1", "rev-active", 1),
            CancellationToken.None);
        await store.ActivateRevisionAsync(
            connection,
            tx,
            "plan-1",
            "rev-active",
            "2026-07-02T00:00:00.000Z",
            "seed activate",
            "user",
            "owner",
            null,
            "evt-act-seed",
            null,
            CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection,
            tx,
            Draft("rev-draft", "plan-1", 2, payloadSeed: "draft"),
            [new BudgetPlanEntryRow("rev-draft", "cat-a", 20)],
            DraftEvent("evt-draft-next", "plan-1", "rev-draft", 3),
            CancellationToken.None);
        await tx.CommitAsync();
        store.RequireOwnerOnlyArtifacts();
    }

    private static async Task<BudgetMutationWorkResult> DraftMutateComplete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        BudgetStateStore store,
        string planId,
        string revisionId,
        string eventId)
    {
        await InsertPlanAsync(store, connection, transaction, cancellationToken, planId);
        var entries = new[] { new BudgetPlanEntryRow(revisionId, "cat-a", 10) };
        var revision = Draft(revisionId, planId, 1);
        var lifecycle = DraftEvent(eventId, planId, revisionId, 1);
        await store.InsertDraftRevisionAsync(connection, transaction, revision, entries, lifecycle, cancellationToken);
        return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
            planId,
            revisionId,
            null,
            [eventId],
            "2026-07-01T00:00:00.000Z",
            "2026-07-01T00:00:00.000Z"));
    }

    private static async Task<BudgetMutationWorkResult> ActivateMutateComplete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        BudgetStateStore store,
        string planId,
        string revisionId,
        string priorActiveRevisionId,
        string supersedeEventId,
        string activateEventId)
    {
        await store.ActivateRevisionAsync(
            connection,
            transaction,
            planId,
            revisionId,
            "2026-07-03T00:00:00.000Z",
            "activate reason",
            "user",
            "owner",
            "run-1",
            activateEventId,
            supersedeEventId,
            cancellationToken);
        return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
            planId,
            revisionId,
            priorActiveRevisionId,
            [supersedeEventId, activateEventId],
            "2026-07-03T00:00:00.000Z",
            "2026-07-03T00:00:00.000Z"));
    }

    private static async Task InsertPlanAsync(
        BudgetStateStore store,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string planId)
    {
        var existing = await store.GetPlanAsync(connection, transaction, planId, cancellationToken);
        if (existing is null)
        {
            await store.InsertPlanAsync(connection, transaction, Plan(planId), cancellationToken);
        }
    }

    private static async Task InsertRevisionOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string planId,
        string revisionId)
    {
        var revision = Draft(revisionId, planId, 1);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO budget_plan_revision (
                revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash,
                activated_at_utc, superseded_at_utc, superseded_by_revision_id
            ) VALUES (
                $revision_id, $plan_id, $revision_number, $status, $actor_kind, $actor_label, $actor_run_id,
                $reason, $created_at_utc, $category_contract_version, $payload_hash,
                NULL, NULL, NULL
            );
            """;
        command.Parameters.AddWithValue("$revision_id", revision.RevisionId);
        command.Parameters.AddWithValue("$plan_id", revision.PlanId);
        command.Parameters.AddWithValue("$revision_number", revision.RevisionNumber);
        command.Parameters.AddWithValue("$status", BudgetRowMapper.FormatStatus(revision.Status));
        command.Parameters.AddWithValue("$actor_kind", revision.ActorKind);
        command.Parameters.AddWithValue("$actor_label", revision.ActorLabel);
        command.Parameters.AddWithValue("$actor_run_id", (object?)revision.ActorRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", revision.Reason);
        command.Parameters.AddWithValue("$created_at_utc", revision.CreatedAtUtc);
        command.Parameters.AddWithValue("$category_contract_version", revision.CategoryContractVersion);
        command.Parameters.AddWithValue("$payload_hash", revision.PayloadHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEntryOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string revisionId,
        string categoryId,
        long amount)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO budget_plan_entry (revision_id, category_id, planned_minor_units)
            VALUES ($revision_id, $category_id, $planned_minor_units);
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        command.Parameters.AddWithValue("$category_id", categoryId);
        command.Parameters.AddWithValue("$planned_minor_units", amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SupersedePriorOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        BudgetStateStore store,
        string planId,
        string priorRevisionId,
        string replacementRevisionId,
        string supersedeEventId)
    {
        await using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE budget_plan_revision
                SET status = 'Superseded',
                    superseded_at_utc = $superseded_at_utc,
                    superseded_by_revision_id = $superseded_by_revision_id
                WHERE revision_id = $revision_id AND status = 'Active';
                """;
            supersede.Parameters.AddWithValue("$superseded_at_utc", "2026-07-03T00:00:00.000Z");
            supersede.Parameters.AddWithValue("$superseded_by_revision_id", replacementRevisionId);
            supersede.Parameters.AddWithValue("$revision_id", priorRevisionId);
            var n = await supersede.ExecuteNonQueryAsync(cancellationToken);
            Assert.Equal(1, n);
        }

        var sequence = await store.NextEventSequenceAsync(connection, transaction, planId, cancellationToken);
        await store.InsertLifecycleEventAsync(
            connection,
            transaction,
            new BudgetLifecycleEventRow(
                supersedeEventId,
                planId,
                priorRevisionId,
                "RevisionSuperseded",
                "user",
                "owner",
                "run-1",
                "activate reason",
                "2026-07-03T00:00:00.000Z",
                "Active",
                "Superseded",
                replacementRevisionId,
                sequence),
            cancellationToken);
    }

    private static async Task ActivateStatusOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string revisionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE budget_plan_revision
            SET status = 'Active',
                activated_at_utc = $activated_at_utc
            WHERE revision_id = $revision_id AND status = 'Draft';
            """;
        command.Parameters.AddWithValue("$activated_at_utc", "2026-07-03T00:00:00.000Z");
        command.Parameters.AddWithValue("$revision_id", revisionId);
        var n = await command.ExecuteNonQueryAsync(cancellationToken);
        Assert.Equal(1, n);
    }

    private static async Task SetActivePointerOnlyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string planId,
        string revisionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE budget_plan
            SET active_revision_id = $active_revision_id
            WHERE plan_id = $plan_id;
            """;
        command.Parameters.AddWithValue("$active_revision_id", revisionId);
        command.Parameters.AddWithValue("$plan_id", planId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string NewRoot(string label) =>
        Path.Combine(Path.GetTempPath(), $"tally-budget-recovery-{label}-{Guid.NewGuid():N}");

    private static async Task<BudgetStateStore> OpenStoreAsync(string root)
    {
        var store = new BudgetStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BudgetMutationIdentity DraftIdentity(string key) =>
        new(
            key,
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.DraftCreate,
            BudgetMutationCanonicalizer.HashDraftRequest(new BudgetDraftLogicalRequest(
                BudgetOperationIds.ContractVersion,
                BudgetOperationIds.DraftCreate,
                "user",
                "owner",
                null,
                "draft reason",
                2026,
                7,
                "ZAR",
                [new BudgetCanonicalEntry("cat-a", 10)])));

    private static BudgetMutationIdentity ActivateIdentity(string key, string revisionId) =>
        new(
            key,
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.RevisionActivate,
            BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
                BudgetOperationIds.ContractVersion,
                BudgetOperationIds.RevisionActivate,
                "user",
                "owner",
                "run-1",
                "activate reason",
                revisionId)));

    private static BudgetPlanRow Plan(string planId) =>
        new(planId, "2026-07-01", "2026-08-01", "ZAR", null, "2026-07-01T00:00:00.000Z");

    private static BudgetPlanRevisionRow Draft(string revisionId, string planId, int number, string payloadSeed = "payload") =>
        new(
            revisionId,
            planId,
            number,
            BudgetRevisionStatus.Draft,
            "user",
            "owner",
            null,
            "draft reason",
            "2026-07-01T00:00:00.000Z",
            "1.0",
            Hash(payloadSeed + "-" + revisionId),
            null,
            null,
            null);

    private static BudgetLifecycleEventRow DraftEvent(string eventId, string planId, string revisionId, int sequence) =>
        new(
            eventId,
            planId,
            revisionId,
            "DraftCreated",
            "user",
            "owner",
            null,
            "draft reason",
            "2026-07-01T00:00:00.000Z",
            null,
            "Draft",
            null,
            sequence);

    private static string Hash(string seed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
