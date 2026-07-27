using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;

namespace Tally.Infrastructure.Budget.Storage.Idempotency;

/// <summary>
/// Transactional replay coordinator for Create Draft and Activate Plan Revision
/// (DD-BUDGET-IDEMPOTENT-MUTATIONS / DM-BUDGET-LIFECYCLE-IDEMPOTENCY).
/// One BEGIN IMMEDIATE spans lookup, mutation, lifecycle refs, and outcome commit.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetMutationExecutor
{
    private readonly BudgetStateStore stateStore;
    private readonly BudgetIdempotencyStore idempotencyStore;

    public BudgetMutationExecutor(BudgetStateStore stateStore, BudgetIdempotencyStore? idempotencyStore = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        this.stateStore = stateStore;
        this.idempotencyStore = idempotencyStore ?? new BudgetIdempotencyStore();
    }

    public BudgetStateStore StateStore => stateStore;

    public BudgetIdempotencyStore IdempotencyStore => idempotencyStore;

    /// <summary>
    /// Test-only fault injection. Production callers leave this at <see cref="BudgetMutationFaultPoint.None"/>.
    /// </summary>
    public BudgetMutationFaultPoint FaultPoint { get; set; } = BudgetMutationFaultPoint.None;

    /// <summary>
    /// Execute a mutation under module-wide idempotency. Missing keys fail before BEGIN IMMEDIATE.
    /// </summary>
    public async Task<BudgetMutationExecutionResult> ExecuteAsync(
        BudgetMutationIdentity identity,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<BudgetMutationWorkResult>> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(mutate);

        // Validation must run before any writer transaction (FR-BUDGET-IDEMPOTENT-MUTATIONS).
        if (string.IsNullOrWhiteSpace(identity.IdempotencyKey))
        {
            return BudgetMutationExecutionResult.Rejected(BudgetErrors.IdempotencyRequired);
        }

        if (string.IsNullOrWhiteSpace(identity.ContractVersion)
            || string.IsNullOrWhiteSpace(identity.OperationId)
            || string.IsNullOrWhiteSpace(identity.RequestHash)
            || identity.RequestHash.Length != 64)
        {
            return BudgetMutationExecutionResult.Rejected(BudgetErrors.InvalidInput);
        }

        var keyDigest = BudgetMutationCanonicalizer.DigestKey(identity.IdempotencyKey);

        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        await using var transaction = stateStore.BeginImmediate(connection);
        try
        {
            var existing = await idempotencyStore.FindAsync(connection, transaction, keyDigest, cancellationToken);
            var lookup = idempotencyStore.Resolve(
                existing,
                identity.ContractVersion,
                identity.OperationId,
                identity.RequestHash);

            switch (lookup.Disposition)
            {
                case BudgetIdempotencyDisposition.Replay:
                {
                    var snapshot = await RehydrateAsync(connection, transaction, lookup.Record!, cancellationToken);
                    await transaction.RollbackAsync(cancellationToken);
                    return BudgetMutationExecutionResult.Replayed(snapshot, lookup.Record!);
                }
                case BudgetIdempotencyDisposition.Conflict:
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return BudgetMutationExecutionResult.Conflict(lookup.Record!);
                }
                case BudgetIdempotencyDisposition.Miss:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown idempotency disposition '{lookup.Disposition}'.");
            }

            var work = await mutate(connection, transaction, cancellationToken);
            if (!work.IsSuccess)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return BudgetMutationExecutionResult.Rejected(
                    work.ErrorCode ?? BudgetErrors.Unexpected);
            }

            var outcome = work.Outcome
                ?? throw new InvalidOperationException("Successful mutation work must produce an outcome.");

            ValidateOutcome(outcome);

            var revision = await stateStore.GetRevisionAsync(
                connection, transaction, outcome.ResultRevisionId, cancellationToken)
                ?? throw new InvalidOperationException("Mutation outcome revision was not found in the transaction.");

            var resultHash = BudgetMutationCanonicalizer.HashResult(
                outcome.PlanId,
                outcome.ResultRevisionId,
                outcome.PriorActiveRevisionId,
                outcome.LifecycleEventIds,
                revision.PayloadHash);

            var completedAtUtc = outcome.CompletedAtUtc;
            var record = new BudgetIdempotencyRow(
                keyDigest,
                identity.ContractVersion,
                identity.OperationId,
                identity.RequestHash,
                BudgetIdempotencyStore.CompletedState,
                outcome.PlanId,
                outcome.ResultRevisionId,
                outcome.PriorActiveRevisionId,
                BudgetRowMapper.FormatLifecycleEventIds(outcome.LifecycleEventIds),
                resultHash,
                outcome.CreatedAtUtc,
                completedAtUtc);

            await idempotencyStore.CommitAsync(connection, transaction, record, cancellationToken);

            if (FaultPoint == BudgetMutationFaultPoint.BeforeCommit)
            {
                throw new BudgetMutationFaultException(BudgetMutationFaultPoint.BeforeCommit);
            }

            await transaction.CommitAsync(cancellationToken);
            stateStore.RequireOwnerOnlyArtifacts();

            if (FaultPoint == BudgetMutationFaultPoint.AfterCommit)
            {
                throw new BudgetMutationFaultException(BudgetMutationFaultPoint.AfterCommit);
            }

            // Rehydrate from durable rows so committed and replay paths share one shape
            // (event-time revision/entries/lifecycle only — no live enrichment).
            await using var readConnection = await stateStore.OpenMigratedAsync(cancellationToken);
            await using var readTx = stateStore.BeginImmediate(readConnection);
            var committed = await RehydrateAsync(readConnection, readTx, record, cancellationToken);
            await readTx.RollbackAsync(cancellationToken);
            return BudgetMutationExecutionResult.Committed(committed, record);
        }
        catch (BudgetMutationFaultException)
        {
            // BeforeCommit: ensure rollback if the exception was thrown pre-commit.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Transaction may already be completed (AfterCommit).
            }

            throw;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Best-effort rollback.
            }

            throw;
        }
    }

    private async Task<BudgetMutationSnapshot> RehydrateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BudgetIdempotencyRow record,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.PlanId) || string.IsNullOrWhiteSpace(record.ResultRevisionId))
        {
            throw new InvalidOperationException("Idempotency outcome is missing plan or revision references.");
        }

        var revision = await stateStore.GetRevisionAsync(
            connection, transaction, record.ResultRevisionId, cancellationToken)
            ?? throw new InvalidOperationException("Referenced result revision was not found.");

        var entries = await stateStore.GetEntriesAsync(
            connection, transaction, record.ResultRevisionId, cancellationToken);

        var planEvents = await stateStore.GetLifecycleEventsAsync(
            connection, transaction, record.PlanId, cancellationToken);
        var byId = planEvents.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        var eventIds = BudgetRowMapper.ParseLifecycleEventIds(record.LifecycleEventIds);
        var lifecycleEvents = new List<BudgetLifecycleEventRow>(eventIds.Count);
        foreach (var eventId in eventIds)
        {
            if (!byId.TryGetValue(eventId, out var lifecycleEvent))
            {
                throw new InvalidOperationException($"Referenced lifecycle event '{eventId}' was not found.");
            }

            lifecycleEvents.Add(lifecycleEvent);
        }

        // Reconstruct event-time revision view from cited lifecycle events so later
        // status transitions (Active/Superseded) never leak into mutation replay results.
        var eventTimeRevision = ProjectEventTimeRevision(revision, lifecycleEvents, record.ResultRevisionId);

        return new BudgetMutationSnapshot(
            record.PlanId,
            record.ResultRevisionId,
            record.PriorActiveRevisionId,
            eventTimeRevision,
            entries,
            lifecycleEvents,
            record.ResultHash,
            record.RequestHash,
            record.KeyDigest,
            record.LifecycleEventIds);
    }

    private static BudgetPlanRevisionRow ProjectEventTimeRevision(
        BudgetPlanRevisionRow live,
        IReadOnlyList<BudgetLifecycleEventRow> lifecycleEvents,
        string resultRevisionId)
    {
        // Prefer the last cited event that targets the result revision (DraftCreated / RevisionActivated).
        var terminal = lifecycleEvents.LastOrDefault(e =>
                string.Equals(e.RevisionId, resultRevisionId, StringComparison.Ordinal)
                && e.ResultingStatus is not null)
            ?? lifecycleEvents.LastOrDefault(e => e.ResultingStatus is not null);

        if (terminal?.ResultingStatus is null)
        {
            // Fall back to live row only when no attributable status is present (should not happen for v1 ops).
            return live with
            {
                // Still strip later supersession enrichment if we cannot prove event-time status.
                SupersededAtUtc = null,
                SupersededByRevisionId = null
            };
        }

        var status = BudgetRowMapper.ParseStatus(terminal.ResultingStatus);
        string? activatedAt = status == BudgetRevisionStatus.Active
            ? terminal.OccurredAtUtc
            : null;

        return live with
        {
            Status = status,
            ActivatedAtUtc = activatedAt,
            // Result revision is never superseded at the instant of its own create/activate mutation.
            SupersededAtUtc = null,
            SupersededByRevisionId = null
        };
    }

    private static void ValidateOutcome(BudgetMutationWorkOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.PlanId)
            || string.IsNullOrWhiteSpace(outcome.ResultRevisionId)
            || string.IsNullOrWhiteSpace(outcome.CreatedAtUtc)
            || string.IsNullOrWhiteSpace(outcome.CompletedAtUtc)
            || outcome.LifecycleEventIds is null
            || outcome.LifecycleEventIds.Count == 0
            || outcome.LifecycleEventIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Mutation outcome references are incomplete.");
        }
    }
}

/// <summary>Caller identity + precomputed canonical request hash for a BUDGET mutation.</summary>
public sealed record BudgetMutationIdentity(
    string IdempotencyKey,
    string ContractVersion,
    string OperationId,
    string RequestHash);

/// <summary>Success payload the mutation callback returns inside the open writer transaction.</summary>
public sealed record BudgetMutationWorkOutcome(
    string PlanId,
    string ResultRevisionId,
    string? PriorActiveRevisionId,
    IReadOnlyList<string> LifecycleEventIds,
    string CreatedAtUtc,
    string CompletedAtUtc);

public sealed record BudgetMutationWorkResult(bool IsSuccess, string? ErrorCode, BudgetMutationWorkOutcome? Outcome)
{
    public static BudgetMutationWorkResult Success(BudgetMutationWorkOutcome outcome) =>
        new(true, null, outcome);

    public static BudgetMutationWorkResult Failure(string errorCode) =>
        new(false, errorCode, null);
}

/// <summary>
/// Event-time mutation snapshot rehydrated from immutable revision, entry, and lifecycle rows.
/// Intentionally excludes live period state, later revision status enrichment, and category names.
/// </summary>
public sealed record BudgetMutationSnapshot(
    string PlanId,
    string ResultRevisionId,
    string? PriorActiveRevisionId,
    BudgetPlanRevisionRow Revision,
    IReadOnlyList<BudgetPlanEntryRow> Entries,
    IReadOnlyList<BudgetLifecycleEventRow> LifecycleEvents,
    string ResultHash,
    string RequestHash,
    string KeyDigest,
    string LifecycleEventIdsEncoded);

public enum BudgetMutationDisposition
{
    Committed,
    Replayed,
    Conflict,
    Rejected
}

public sealed record BudgetMutationExecutionResult(
    BudgetMutationDisposition Disposition,
    BudgetMutationSnapshot? Snapshot,
    BudgetIdempotencyRow? Record,
    string? ErrorCode)
{
    public bool IsSuccess =>
        Disposition is BudgetMutationDisposition.Committed or BudgetMutationDisposition.Replayed;

    public static BudgetMutationExecutionResult Committed(BudgetMutationSnapshot snapshot, BudgetIdempotencyRow record) =>
        new(BudgetMutationDisposition.Committed, snapshot, record, null);

    public static BudgetMutationExecutionResult Replayed(BudgetMutationSnapshot snapshot, BudgetIdempotencyRow record) =>
        new(BudgetMutationDisposition.Replayed, snapshot, record, null);

    public static BudgetMutationExecutionResult Conflict(BudgetIdempotencyRow record) =>
        new(BudgetMutationDisposition.Conflict, null, record, BudgetErrors.IdempotencyConflict);

    public static BudgetMutationExecutionResult Rejected(string errorCode) =>
        new(BudgetMutationDisposition.Rejected, null, null, errorCode);
}

public enum BudgetMutationFaultPoint
{
    None = 0,
    BeforeCommit = 1,
    AfterCommit = 2
}

/// <summary>Injected interruption used by crash-cutpoint tests.</summary>
public sealed class BudgetMutationFaultException : Exception
{
    public BudgetMutationFaultException(BudgetMutationFaultPoint point)
        : base($"Injected BUDGET mutation fault at {point}.")
    {
        Point = point;
    }

    public BudgetMutationFaultPoint Point { get; }
}
