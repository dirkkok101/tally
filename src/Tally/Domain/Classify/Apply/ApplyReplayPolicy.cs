using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Domain.Classify.Apply;

/// <summary>
/// Pure frontier and frozen-replay policy for classify.apply.run
/// (FR-CLASSIFY-APPLY-EXECUTION / NFR-CLASSIFY-APPLY-RECOVERY / DD-CLASSIFY-APPLY-SAGA).
/// Never regenerates frozen request fields; never authorizes Ledger calls for terminal items.
/// </summary>
public static class ApplyReplayPolicy
{
    public const string ItemStatePlanned = "planned";
    public const string ItemStateApplied = "applied";
    public const string ItemStateAlreadyApplied = "already_applied";
    public const string ItemStateRejected = "rejected";
    public const string ItemStateFailed = "failed";
    public const string ItemStateUnresolved = "unresolved";

    public const string RunLifecycleRunning = "running";
    public const string RunLifecycleCompleted = "completed";
    public const string RunLifecycleFailed = "failed";
    public const string RunLifecycleAbandoned = "abandoned";

    public const string LedgerOperationAssign = "ledger.transaction.category.assign";
    public const string LedgerOperationCorrect = "ledger.transaction.category.correct";

    public static readonly IReadOnlySet<string> TerminalItemStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ItemStateApplied,
            ItemStateAlreadyApplied,
            ItemStateRejected,
            ItemStateFailed
        };

    public static readonly IReadOnlySet<string> ReplayableItemStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ItemStatePlanned,
            ItemStateUnresolved
        };

    public static bool IsTerminalItemState(string itemState) =>
        TerminalItemStates.Contains(itemState);

    public static bool IsReplayableItemState(string itemState) =>
        ReplayableItemStates.Contains(itemState);

    public static bool MayCallLedger(string itemState) =>
        IsReplayableItemState(itemState);

    /// <summary>
    /// Count of non-terminal items remaining (unresolved frontier size).
    /// Zero when every item has a durable terminal result.
    /// </summary>
    public static int ComputeUnresolvedFrontier(IEnumerable<string> itemStates)
    {
        ArgumentNullException.ThrowIfNull(itemStates);
        return itemStates.Count(state => !IsTerminalItemState(state));
    }

    /// <summary>
    /// Ordered replay frontier: only planned/unresolved items, in deterministic ordinal then transaction order.
    /// </summary>
    public static IReadOnlyList<TItem> SelectReplayFrontier<TItem>(
        IEnumerable<TItem> items,
        Func<TItem, int> ordinal,
        Func<TItem, string> transactionId,
        Func<TItem, string> itemState)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ordinal);
        ArgumentNullException.ThrowIfNull(transactionId);
        ArgumentNullException.ThrowIfNull(itemState);

        return items
            .Where(item => MayCallLedger(itemState(item)))
            .OrderBy(ordinal)
            .ThenBy(item => transactionId(item), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Terminal run lifecycle after processing: completed when frontier is empty; remains running otherwise.
    /// </summary>
    public static string ResolveRunLifecycleAfterItems(int unresolvedFrontier) =>
        unresolvedFrontier == 0 ? RunLifecycleCompleted : RunLifecycleRunning;

    /// <summary>
    /// Map a Ledger success or known rejection into a CLASSIFY item terminal state.
    /// Does not invent success — unknown codes fail closed as failed.
    /// </summary>
    public static (string ItemState, ClassifyApplyItemResultKind Kind) MapLedgerOutcome(
        bool success,
        string? ledgerErrorCode,
        bool categoryAlreadyMatchesTarget)
    {
        if (success)
        {
            return (ItemStateApplied, ClassifyApplyItemResultKind.Applied);
        }

        if (categoryAlreadyMatchesTarget && IsAlreadyAppliedError(ledgerErrorCode))
        {
            return (ItemStateAlreadyApplied, ClassifyApplyItemResultKind.AlreadyApplied);
        }

        if (IsRejectedError(ledgerErrorCode))
        {
            // Cardinality with matching target category is already_applied, not rejected.
            if (categoryAlreadyMatchesTarget && IsAlreadyAppliedError(ledgerErrorCode))
            {
                return (ItemStateAlreadyApplied, ClassifyApplyItemResultKind.AlreadyApplied);
            }

            return (ItemStateRejected, ClassifyApplyItemResultKind.Rejected);
        }

        // Unknown Ledger error → fail closed as failed (never treat as applied).
        return (ItemStateFailed, ClassifyApplyItemResultKind.Failed);
    }

    /// <summary>
    /// Unchanged correction (target already active) is already_applied.
    /// Cardinality on assign is rejected (another allocation exists) unless callers prove same-target replay.
    /// </summary>
    public static bool IsAlreadyAppliedError(string? code) =>
        string.Equals(code, "LEDGER-CATEGORY-ALLOCATION-UNCHANGED", StringComparison.Ordinal);

    public static bool IsRejectedError(string? code) =>
        code is not null
        && (string.Equals(code, CategoryMutationPreconditionCodes.StalePrecondition, StringComparison.Ordinal)
            || string.Equals(code, CategoryMutationPreconditionCodes.ContractMismatch, StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-CATEGORY-ALLOCATION-CARDINALITY", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-CATEGORY-ALLOCATION-NOT-ASSIGNED", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-CATEGORY-ALLOCATION-UNCHANGED", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-TRANSACTION-INACTIVE", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-TRANSACTION-NOT-FOUND", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-CATEGORY-NOT-FOUND", StringComparison.Ordinal)
            || string.Equals(code, "LEDGER-CATEGORY-ARCHIVED", StringComparison.Ordinal)
            || code.StartsWith("LEDGER-", StringComparison.Ordinal));

    public static ClassifyApplyItemResultKind ToPublicKind(string itemState) => itemState switch
    {
        ItemStateApplied => ClassifyApplyItemResultKind.Applied,
        ItemStateAlreadyApplied => ClassifyApplyItemResultKind.AlreadyApplied,
        ItemStateRejected => ClassifyApplyItemResultKind.Rejected,
        ItemStateFailed => ClassifyApplyItemResultKind.Failed,
        ItemStateUnresolved => ClassifyApplyItemResultKind.Unresolved,
        ItemStatePlanned => ClassifyApplyItemResultKind.Unresolved,
        _ => ClassifyApplyItemResultKind.Failed
    };

    public static bool IsValidItemStateTransition(string from, string to)
    {
        if (IsTerminalItemState(from))
        {
            return false;
        }

        return from switch
        {
            ItemStatePlanned => to is ItemStateApplied or ItemStateAlreadyApplied or ItemStateRejected
                or ItemStateFailed or ItemStateUnresolved,
            ItemStateUnresolved => to is ItemStateApplied or ItemStateAlreadyApplied or ItemStateRejected
                or ItemStateFailed,
            _ => false
        };
    }

    /// <summary>
    /// Mode string from preview item → Ledger operation id. Unknown modes fail closed (null).
    /// </summary>
    public static string? ResolveLedgerOperationId(string mode) => mode switch
    {
        ApplyAuthorizationPolicy.ModeAssign => LedgerOperationAssign,
        ApplyAuthorizationPolicy.ModeCorrect => LedgerOperationCorrect,
        _ => null
    };
}
