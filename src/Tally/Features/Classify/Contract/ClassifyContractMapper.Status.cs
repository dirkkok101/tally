using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure classify.status mapping (FR-CLASSIFY-STATUS-HISTORY / TASK-CLASSIFY-RULEBOOK-STATUS-WORKFLOW).
/// Projects durable metadata only — never private corpus rows, Ledger payloads, paths, amounts, or tokens.
/// </summary>
public static partial class ClassifyContractMapper
{
    public static ClassifyStatusResult ToStatusResult(
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        SafeNextActionPolicy.Decision decision) =>
        new(
            ClassifyOperationIds.ContractVersion,
            subjectType,
            subjectId.Trim(),
            decision.LifecycleState,
            decision.MutationMayHaveOccurred,
            decision.NextSafeOperationId);

    /// <summary>
    /// Terminal apply item totals used only to decide mutation possibility and next action.
    /// Never embeds transaction descriptions, amounts, or private error payloads.
    /// </summary>
    public static ApplyStatusTotals ToApplyStatusTotals(IReadOnlyList<ClassifyApplyItemRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var applied = 0;
        var alreadyApplied = 0;
        var rejected = 0;
        var failed = 0;
        var unresolved = 0;
        foreach (var item in items)
        {
            switch (ApplyReplayPolicy.ToPublicKind(item.ItemState))
            {
                case ClassifyApplyItemResultKind.Applied:
                    applied++;
                    break;
                case ClassifyApplyItemResultKind.AlreadyApplied:
                    alreadyApplied++;
                    break;
                case ClassifyApplyItemResultKind.Rejected:
                    rejected++;
                    break;
                case ClassifyApplyItemResultKind.Failed:
                    failed++;
                    break;
                default:
                    unresolved++;
                    break;
            }
        }

        return new ApplyStatusTotals(applied, alreadyApplied, rejected, failed, unresolved);
    }

    public static string DerivePreviewLifecycle(bool isTombstoned, bool isExpired) =>
        isTombstoned
            ? SafeNextActionPolicy.LifecycleAbandoned
            : isExpired
                ? SafeNextActionPolicy.LifecycleExpired
                : SafeNextActionPolicy.LifecycleRetained;

    public sealed record ApplyStatusTotals(
        int AppliedCount,
        int AlreadyAppliedCount,
        int RejectedCount,
        int FailedCount,
        int UnresolvedCount);
}
