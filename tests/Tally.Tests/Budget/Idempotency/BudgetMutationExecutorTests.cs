using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Features.Budget.Contract;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Xunit;

namespace Tally.Tests.Budget.Idempotency;

[SupportedOSPlatform("linux")]
public sealed class BudgetMutationExecutorTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-mut-{Guid.NewGuid():N}");

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / missing key
    [Fact]
    public async Task Missing_idempotency_key_fails_before_begin_immediate_and_does_not_run_mutation()
    {
        var calls = new Counter();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            new BudgetMutationIdentity(" ", BudgetOperationIds.ContractVersion, BudgetOperationIds.DraftCreate, Hash("req")),
            (connection, transaction, ct) =>
            {
                calls.Value++;
                return Task.FromResult(BudgetMutationWorkResult.Success(DummyOutcome()));
            },
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Rejected, result.Disposition);
        Assert.Equal(BudgetErrors.IdempotencyRequired, result.ErrorCode);
        Assert.Equal(0, calls.Value);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / missing key
    [Fact]
    public async Task Null_empty_key_fails_without_opening_writer_side_effects()
    {
        var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(
            new BudgetMutationIdentity(string.Empty, BudgetOperationIds.ContractVersion, BudgetOperationIds.DraftCreate, Hash("req")),
            (_, _, _) => Task.FromResult(BudgetMutationWorkResult.Success(DummyOutcome())),
            CancellationToken.None);

        Assert.Equal(BudgetErrors.IdempotencyRequired, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / equivalent input replay
    [Fact]
    public async Task Same_key_and_equivalent_normalized_input_replays_exact_revision_and_lifecycle()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var identity = DraftIdentity("key-1", entries: [("cat-b", 20), ("cat-a", 10)]);

        var first = await executor.ExecuteAsync(identity, (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10), ("cat-b", 20)]), CancellationToken.None);
        var replay = await executor.ExecuteAsync(identity, (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", calls, [("cat-a", 99)]), CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Committed, first.Disposition);
        Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, calls.Value);
        Assert.Equal("rev-1", replay.Snapshot!.ResultRevisionId);
        Assert.Equal(first.Snapshot!.Revision.PayloadHash, replay.Snapshot.Revision.PayloadHash);
        Assert.Equal(first.Snapshot.LifecycleEvents.Select(e => e.EventId), replay.Snapshot.LifecycleEvents.Select(e => e.EventId));
        Assert.Equal(first.Snapshot.Entries.Select(e => (e.CategoryId, e.PlannedMinorUnits)), replay.Snapshot.Entries.Select(e => (e.CategoryId, e.PlannedMinorUnits)));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / equivalent normalized input
    [Fact]
    public async Task Entry_order_does_not_change_request_hash_or_replay_identity()
    {
        var a = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-b", 5), ("cat-a", 1)]));
        var b = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-a", 1), ("cat-b", 5)]));
        Assert.Equal(a, b);

        var calls = new Counter();
        var executor = CreateExecutor();
        var first = await executor.ExecuteAsync(
            DraftIdentity("key-order", requestHash: a),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 1), ("cat-b", 5)]),
            CancellationToken.None);
        var replay = await executor.ExecuteAsync(
            DraftIdentity("key-order", requestHash: b),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-x", "evt-x", calls, [("cat-a", 1), ("cat-b", 5)]),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, calls.Value);
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / conflict
    [Fact]
    public async Task Same_key_with_different_request_hash_conflicts_without_plan_change()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(
            DraftIdentity("key-conflict", requestHash: Hash("req-a")),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)]),
            CancellationToken.None);

        var conflict = await executor.ExecuteAsync(
            DraftIdentity("key-conflict", requestHash: Hash("req-b")),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", calls, [("cat-a", 99)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal(BudgetErrors.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(1, calls.Value);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE revision_id = 'rev-2';"));
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / conflict
    [Fact]
    public async Task Same_key_with_different_operation_id_conflicts()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var hash = Hash("shared");
        await executor.ExecuteAsync(
            new BudgetMutationIdentity("key-op", BudgetOperationIds.ContractVersion, BudgetOperationIds.DraftCreate, hash),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 1)]),
            CancellationToken.None);

        var conflict = await executor.ExecuteAsync(
            new BudgetMutationIdentity("key-op", BudgetOperationIds.ContractVersion, BudgetOperationIds.RevisionActivate, hash),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", calls, [("cat-a", 1)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal(1, calls.Value);
    }

    // FR-BUDGET-IDEMPOTENT-MUTATIONS / conflict
    [Fact]
    public async Task Same_key_with_different_contract_version_conflicts()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var hash = Hash("shared");
        await executor.ExecuteAsync(
            new BudgetMutationIdentity("key-ver", "1.0", BudgetOperationIds.DraftCreate, hash),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 1)]),
            CancellationToken.None);

        var conflict = await executor.ExecuteAsync(
            new BudgetMutationIdentity("key-ver", "2.0", BudgetOperationIds.DraftCreate, hash),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", calls, [("cat-a", 1)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal(1, calls.Value);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS / event-time replay
    [Fact]
    public async Task Replay_returns_event_time_revision_status_not_later_live_enrichment()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var identity = DraftIdentity("key-event-time");
        var first = await executor.ExecuteAsync(
            identity,
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)]),
            CancellationToken.None);

        // Later lifecycle transition: activate the draft so live status becomes Active.
        await ActivateOutsideExecutorAsync("plan-1", "rev-1", "evt-activate");

        var replay = await executor.ExecuteAsync(
            identity,
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-x", "evt-x", calls, [("cat-a", 10)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
        // Event-time projection keeps the original Draft status even after a later activation.
        Assert.Equal(BudgetRevisionStatus.Draft, first.Snapshot!.Revision.Status);
        Assert.Equal(BudgetRevisionStatus.Draft, replay.Snapshot!.Revision.Status);
        Assert.Null(replay.Snapshot.Revision.ActivatedAtUtc);
        Assert.Equal("DraftCreated", first.Snapshot.LifecycleEvents.Single().EventType);
        Assert.Equal("DraftCreated", replay.Snapshot.LifecycleEvents.Single().EventType);
        // Live store actually advanced:
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active' AND revision_id = 'rev-1';"));
        Assert.All(replay.Snapshot.Entries, e => Assert.False(string.IsNullOrEmpty(e.CategoryId)));
        // No live enrichment surface on the snapshot type itself (compile-time + reflection guard).
        var props = typeof(BudgetMutationSnapshot).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(props, name => name.Contains("Period", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, name => name.Contains("DisplayName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, name => name.Contains("CategoryLifecycle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, name => name.Contains("Position", StringComparison.OrdinalIgnoreCase));
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS / event-time lifecycle
    [Fact]
    public async Task Replay_preserves_original_lifecycle_event_sequence_and_attribution()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var identity = DraftIdentity("key-life");
        var first = await executor.ExecuteAsync(
            identity,
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)], actorLabel: "owner-a", reason: "first-reason"),
            CancellationToken.None);
        var replay = await executor.ExecuteAsync(identity, (_, _, _) => Task.FromResult(BudgetMutationWorkResult.Failure("should-not-run")), CancellationToken.None);

        var evt = replay.Snapshot!.LifecycleEvents.Single();
        Assert.Equal("evt-1", evt.EventId);
        Assert.Equal("owner-a", evt.ActorLabel);
        Assert.Equal("first-reason", evt.Reason);
        Assert.Equal(first.Snapshot!.LifecycleEvents.Single().OccurredAtUtc, evt.OccurredAtUtc);
        Assert.Equal(1, evt.EventSequence);
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / pre-commit interruption
    [Fact]
    public async Task Pre_commit_interruption_leaves_prior_state_and_key_reusable()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
            executor.ExecuteAsync(
                DraftIdentity("key-pre"),
                (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)]),
                CancellationToken.None));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        var retry = await executor.ExecuteAsync(
            DraftIdentity("key-pre"),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Committed, retry.Disposition);
        Assert.Equal(2, calls.Value);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / post-commit interruption
    [Fact]
    public async Task Post_commit_interruption_then_retry_replays_single_committed_outcome()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        executor.FaultPoint = BudgetMutationFaultPoint.AfterCommit;

        var fault = await Assert.ThrowsAsync<BudgetMutationFaultException>(() =>
            executor.ExecuteAsync(
                DraftIdentity("key-post"),
                (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 10)]),
                CancellationToken.None));
        Assert.Equal(BudgetMutationFaultPoint.AfterCommit, fault.Point);
        Assert.Equal(1, calls.Value);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));

        executor.FaultPoint = BudgetMutationFaultPoint.None;
        var replay = await executor.ExecuteAsync(
            DraftIdentity("key-post"),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", calls, [("cat-a", 99)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal("rev-1", replay.Snapshot!.ResultRevisionId);
        Assert.Equal(1, calls.Value);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / domain failure
    [Fact]
    public async Task Domain_failure_rolls_back_and_does_not_consume_key()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var failed = await executor.ExecuteAsync(
            DraftIdentity("key-fail"),
            async (c, t, ct) =>
            {
                calls.Value++;
                await InsertPlanAndDraftAsync(c, t, ct, "plan-1", "rev-1", "evt-1", [("cat-a", 1)]);
                return BudgetMutationWorkResult.Failure(BudgetErrors.InvalidAmount);
            },
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Rejected, failed.Disposition);
        Assert.Equal(BudgetErrors.InvalidAmount, failed.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));

        var success = await executor.ExecuteAsync(
            DraftIdentity("key-fail"),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 1)]),
            CancellationToken.None);
        Assert.Equal(BudgetMutationDisposition.Committed, success.Disposition);
        Assert.Equal(2, calls.Value);
    }

    // NFR-BUDGET-ATOMIC-DURABLE-MUTATIONS / thrown failure
    [Fact]
    public async Task Thrown_mutation_rolls_back_and_leaves_key_reusable()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                DraftIdentity("key-throw"),
                async (c, t, ct) =>
                {
                    calls.Value++;
                    await InsertPlanAndDraftAsync(c, t, ct, "plan-1", "rev-1", "evt-1", [("cat-a", 1)]);
                    throw new InvalidOperationException("injected");
                },
                CancellationToken.None));

        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM budget_plan;"));
        var retry = await executor.ExecuteAsync(
            DraftIdentity("key-throw"),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", calls, [("cat-a", 1)]),
            CancellationToken.None);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, calls.Value);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS / payload exclusion
    [Fact]
    public async Task Idempotency_record_stores_digest_and_refs_not_raw_key_or_financial_payload()
    {
        var executor = CreateExecutor();
        const string rawKey = "raw-secret-key-value-xyz";
        var result = await executor.ExecuteAsync(
            DraftIdentity(rawKey),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", new Counter(), [("cat-groceries", 12_345)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using var connection = await new BudgetStateStore(root).OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_digest, contract_version, operation_id, request_hash, state,
                   plan_id, result_revision_id, prior_active_revision_id,
                   lifecycle_event_ids, result_hash, created_at_utc, completed_at_utc
            FROM budget_idempotency_record;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "").ToArray();
        var joined = string.Join('|', values);

        Assert.Equal(BudgetMutationCanonicalizer.DigestKey(rawKey), values[0]);
        Assert.DoesNotContain(rawKey, joined, StringComparison.Ordinal);
        Assert.DoesNotContain("cat-groceries", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("planned", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", joined, StringComparison.Ordinal);
        Assert.Equal(64, values[0].Length);
        Assert.Equal(64, values[3].Length);
        Assert.Equal(64, values[9].Length);
        Assert.Equal(BudgetIdempotencyStore.CompletedState, values[4]);
        Assert.Equal("plan-1", values[5]);
        Assert.Equal("rev-1", values[6]);
        Assert.Equal("evt-1", values[8]);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS / diagnostics surface
    [Fact]
    public async Task Snapshot_diagnostics_surface_has_no_amount_or_category_name_fields_outside_entries()
    {
        var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(
            DraftIdentity("key-diag"),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", new Counter(), [("cat-a", 42)]),
            CancellationToken.None);

        // Entry rows intentionally hold planned minor units (immutable payload);
        // idempotency record and non-entry snapshot refs must not duplicate them.
        Assert.Equal(42, result.Snapshot!.Entries.Single().PlannedMinorUnits);
        Assert.DoesNotContain("42", result.Record!.ResultHash, StringComparison.Ordinal);
        Assert.DoesNotContain("42", result.Record.KeyDigest, StringComparison.Ordinal);
        Assert.DoesNotContain("42", result.Record.LifecycleEventIds, StringComparison.Ordinal);
        Assert.Equal(result.Snapshot.ResultHash, result.Record.ResultHash);
    }

    // DD-BUDGET-IDEMPOTENT-MUTATIONS / activate path
    [Fact]
    public async Task Activate_mutation_commits_once_and_replays_with_prior_active_ref()
    {
        var store = new BudgetStateStore(root);
        await store.InitializeAsync(CancellationToken.None);
        await using (var connection = await store.OpenMigratedAsync(CancellationToken.None))
        await using (var seed = store.BeginImmediate(connection))
        {
            await store.InsertPlanAsync(connection, seed, Plan("plan-1"), CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, seed, Draft("rev-1", "plan-1", 1),
                [new BudgetPlanEntryRow("rev-1", "cat-a", 10)],
                DraftEvent("evt-draft-1", "plan-1", "rev-1", 1), CancellationToken.None);
            await store.ActivateRevisionAsync(
                connection, seed, "plan-1", "rev-1", "2026-07-02T00:00:00.000Z", "seed",
                "user", "owner", null, "evt-act-1", null, CancellationToken.None);
            await store.InsertDraftRevisionAsync(
                connection, seed, Draft("rev-2", "plan-1", 2),
                [new BudgetPlanEntryRow("rev-2", "cat-a", 20)],
                DraftEvent("evt-draft-2", "plan-1", "rev-2", 3), CancellationToken.None);
            await seed.CommitAsync();
        }

        var calls = new Counter();
        var executor = new BudgetMutationExecutor(store);
        var activateHash = BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.RevisionActivate,
            "user",
            "owner",
            "run-9",
            "activate-now",
            "rev-2"));
        var identity = new BudgetMutationIdentity(
            "key-activate",
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.RevisionActivate,
            activateHash);

        var first = await executor.ExecuteAsync(identity, async (c, t, ct) =>
        {
            calls.Value++;
            await store.ActivateRevisionAsync(
                c, t, "plan-1", "rev-2", "2026-07-03T00:00:00.000Z", "activate-now",
                "user", "owner", "run-9", "evt-act-2", "evt-sup-1", ct);
            return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
                "plan-1",
                "rev-2",
                "rev-1",
                ["evt-sup-1", "evt-act-2"],
                "2026-07-03T00:00:00.000Z",
                "2026-07-03T00:00:00.000Z"));
        }, CancellationToken.None);

        var replay = await executor.ExecuteAsync(identity, async (c, t, ct) =>
        {
            calls.Value++;
            await store.ActivateRevisionAsync(
                c, t, "plan-1", "rev-2", "2026-07-04T00:00:00.000Z", "activate-now",
                "user", "owner", "run-9", "evt-act-x", "evt-sup-x", ct);
            return BudgetMutationWorkResult.Success(DummyOutcome());
        }, CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Committed, first.Disposition);
        Assert.Equal(BudgetMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal("rev-1", first.Snapshot!.PriorActiveRevisionId);
        Assert.Equal("rev-1", replay.Snapshot!.PriorActiveRevisionId);
        Assert.Equal(["evt-sup-1", "evt-act-2"], replay.Snapshot.LifecycleEvents.Select(e => e.EventId));
        Assert.Equal(1, calls.Value);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal("rev-2", (await GetActiveRevisionIdAsync())!);
    }

    // BudgetMutationCanonicalizer
    [Fact]
    public void Canonicalizer_digest_key_is_sha256_hex_and_stable()
    {
        var digest = BudgetMutationCanonicalizer.DigestKey("abc");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))).ToLowerInvariant();
        Assert.Equal(expected, digest);
        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, BudgetMutationCanonicalizer.DigestKey("abc"));
    }

    // BudgetMutationCanonicalizer
    [Fact]
    public void Canonicalizer_draft_hash_changes_when_amount_or_category_changes()
    {
        var baseHash = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-a", 10)]));
        var amount = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-a", 11)]));
        var category = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-b", 10)]));
        var reason = BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical([("cat-a", 10)], reason: "other"));
        Assert.NotEqual(baseHash, amount);
        Assert.NotEqual(baseHash, category);
        Assert.NotEqual(baseHash, reason);
    }

    // BudgetMutationCanonicalizer
    [Fact]
    public void Canonicalizer_activate_hash_includes_revision_and_actor()
    {
        var a = BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
            "1.0", BudgetOperationIds.RevisionActivate, "user", "owner", null, "go", "rev-1"));
        var b = BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
            "1.0", BudgetOperationIds.RevisionActivate, "user", "owner", null, "go", "rev-2"));
        var c = BudgetMutationCanonicalizer.HashActivateRequest(new BudgetActivateLogicalRequest(
            "1.0", BudgetOperationIds.RevisionActivate, "user", "other", null, "go", "rev-1"));
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }

    // Invalid identity fields
    [Fact]
    public async Task Invalid_request_hash_is_rejected_before_mutation()
    {
        var calls = new Counter();
        var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(
            new BudgetMutationIdentity("key", BudgetOperationIds.ContractVersion, BudgetOperationIds.DraftCreate, "not-a-hash"),
            (_, _, _) =>
            {
                calls.Value++;
                return Task.FromResult(BudgetMutationWorkResult.Success(DummyOutcome()));
            },
            CancellationToken.None);

        Assert.Equal(BudgetErrors.InvalidInput, result.ErrorCode);
        Assert.Equal(0, calls.Value);
    }

    // Result hash stability
    [Fact]
    public async Task Result_hash_is_stable_across_commit_and_replay()
    {
        var executor = CreateExecutor();
        var identity = DraftIdentity("key-hash");
        var first = await executor.ExecuteAsync(
            identity,
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", new Counter(), [("cat-a", 10)]),
            CancellationToken.None);
        var replay = await executor.ExecuteAsync(
            identity,
            (_, _, _) => Task.FromResult(BudgetMutationWorkResult.Failure("nope")),
            CancellationToken.None);

        Assert.Equal(first.Snapshot!.ResultHash, replay.Snapshot!.ResultHash);
        Assert.Equal(64, first.Snapshot.ResultHash.Length);
        Assert.Equal(first.Record!.ResultHash, replay.Record!.ResultHash);
    }

    // Conflict leaves active pointer untouched
    [Fact]
    public async Task Conflict_after_activation_does_not_change_active_pointer()
    {
        var executor = CreateExecutor();
        await executor.ExecuteAsync(
            DraftIdentity("key-a", requestHash: Hash("a")),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-1", "evt-1", new Counter(), [("cat-a", 1)]),
            CancellationToken.None);
        await ActivateOutsideExecutorAsync("plan-1", "rev-1", "evt-act");

        var before = await GetActiveRevisionIdAsync();
        var conflict = await executor.ExecuteAsync(
            DraftIdentity("key-a", requestHash: Hash("b")),
            (c, t, ct) => DraftMutate(c, t, ct, "plan-1", "rev-2", "evt-2", new Counter(), [("cat-a", 2)]),
            CancellationToken.None);

        Assert.Equal(BudgetMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal(before, await GetActiveRevisionIdAsync());
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private BudgetMutationExecutor CreateExecutor()
    {
        var store = new BudgetStateStore(root);
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new BudgetMutationExecutor(store);
    }

    private static BudgetMutationIdentity DraftIdentity(
        string key,
        string? requestHash = null,
        IReadOnlyList<(string CategoryId, long Amount)>? entries = null) =>
        new(
            key,
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.DraftCreate,
            requestHash ?? BudgetMutationCanonicalizer.HashDraftRequest(DraftLogical(entries ?? [("cat-a", 10)])));

    private static BudgetDraftLogicalRequest DraftLogical(
        IReadOnlyList<(string CategoryId, long Amount)> entries,
        string reason = "draft reason") =>
        new(
            BudgetOperationIds.ContractVersion,
            BudgetOperationIds.DraftCreate,
            "user",
            "owner",
            null,
            reason,
            2026,
            7,
            "ZAR",
            entries.Select(e => new BudgetCanonicalEntry(e.CategoryId, e.Amount)).ToArray());

    private static async Task<BudgetMutationWorkResult> DraftMutate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string planId,
        string revisionId,
        string eventId,
        Counter calls,
        IReadOnlyList<(string CategoryId, long Amount)> entries,
        string actorLabel = "owner",
        string reason = "draft reason")
    {
        calls.Value++;
        await InsertPlanAndDraftAsync(connection, transaction, cancellationToken, planId, revisionId, eventId, entries, actorLabel, reason);
        return BudgetMutationWorkResult.Success(new BudgetMutationWorkOutcome(
            planId,
            revisionId,
            null,
            [eventId],
            "2026-07-01T00:00:00.000Z",
            "2026-07-01T00:00:00.000Z"));
    }

    private static async Task InsertPlanAndDraftAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken,
        string planId,
        string revisionId,
        string eventId,
        IReadOnlyList<(string CategoryId, long Amount)> entries,
        string actorLabel = "owner",
        string reason = "draft reason")
    {
        // Instance methods need a store; Paths are unused when a caller-supplied connection is provided.
        var store = new BudgetStateStore(Path.Combine(Path.GetTempPath(), "budget-helper-unused"));
        await store.InsertPlanAsync(connection, transaction, Plan(planId), cancellationToken);
        var entryRows = entries.Select(e => new BudgetPlanEntryRow(revisionId, e.CategoryId, e.Amount)).ToArray();
        var payloadHash = Hash("payload-" + revisionId + string.Join(',', entries.Select(e => e.CategoryId + ":" + e.Amount)));
        var revision = new BudgetPlanRevisionRow(
            revisionId, planId, 1, BudgetRevisionStatus.Draft, "user", actorLabel, null, reason,
            "2026-07-01T00:00:00.000Z", "1.0", payloadHash, null, null, null);
        var lifecycle = new BudgetLifecycleEventRow(
            eventId, planId, revisionId, "DraftCreated", "user", actorLabel, null, reason,
            "2026-07-01T00:00:00.000Z", null, "Draft", null, 1);
        await store.InsertDraftRevisionAsync(connection, transaction, revision, entryRows, lifecycle, cancellationToken);
    }

    private async Task ActivateOutsideExecutorAsync(string planId, string revisionId, string activateEventId)
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.ActivateRevisionAsync(
            connection, transaction, planId, revisionId, "2026-07-02T00:00:00.000Z", "activate",
            "user", "owner", null, activateEventId, null, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<string?> GetActiveRevisionIdAsync()
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var plan = await store.GetPlanAsync(connection, null, "plan-1", CancellationToken.None);
        return plan?.ActiveRevisionId;
    }

    private async Task<long> CountAsync(string sql)
    {
        var store = new BudgetStateStore(root);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static BudgetPlanRow Plan(string planId) =>
        new(planId, "2026-07-01", "2026-08-01", "ZAR", null, "2026-07-01T00:00:00.000Z");

    private static BudgetPlanRevisionRow Draft(string revisionId, string planId, int number) => new(
        revisionId, planId, number, BudgetRevisionStatus.Draft, "user", "owner", null, "draft reason",
        "2026-07-01T00:00:00.000Z", "1.0", Hash("payload-" + revisionId), null, null, null);

    private static BudgetLifecycleEventRow DraftEvent(string eventId, string planId, string revisionId, int sequence) => new(
        eventId, planId, revisionId, "DraftCreated", "user", "owner", null, "draft reason",
        "2026-07-01T00:00:00.000Z", null, "Draft", null, sequence);

    private static BudgetMutationWorkOutcome DummyOutcome() =>
        new("plan-x", "rev-x", null, ["evt-x"], "2026-07-01T00:00:00.000Z", "2026-07-01T00:00:00.000Z");

    private static string Hash(string seed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private sealed class Counter
    {
        public int Value { get; set; }
    }
}
