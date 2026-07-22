using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Domain.Ledger.Reconciliation;

public static class ReconciliationStateReducer
{
    public static bool RequiresActiveLinkPredecessor(ReconciliationDecisionAction action) => action is
        ReconciliationDecisionAction.Revoke or ReconciliationDecisionAction.Replace;

    public static bool ContainsReviewedCandidate(ReconciliationDecisionHistoryItem decision, string transactionId)
    {
        const string marker = "candidates=";
        var start = decision.MatchBasis.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return false;
        start += marker.Length;
        var end = decision.MatchBasis.IndexOf(';', start);
        var value = end < 0 ? decision.MatchBasis[start..] : decision.MatchBasis[start..end];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(transactionId, StringComparer.Ordinal);
    }

    public static string? ValidateTransition(
        ReconciliationDecisionAction action,
        ReconciliationDecisionHistoryItem? current,
        ReconciliationDecisionLink? activeLink)
    {
        return action switch
        {
            ReconciliationDecisionAction.Confirm when current is null
                || current.Disposition is not (ReconciliationDecisionDisposition.Ambiguous
                    or ReconciliationDecisionDisposition.Exception
                    or ReconciliationDecisionDisposition.Rejected)
                || activeLink is not null => ReconciliationDecisionErrors.TransitionIncompatible,
            ReconciliationDecisionAction.Reject when activeLink is not null => ReconciliationDecisionErrors.TransitionIncompatible,
            ReconciliationDecisionAction.Revoke when current is null || activeLink is null
                || current.ActiveTransactionId != activeLink.TransactionId => ReconciliationDecisionErrors.TransitionIncompatible,
            ReconciliationDecisionAction.Replace when current is null || activeLink is null
                || current.ActiveTransactionId != activeLink.TransactionId => ReconciliationDecisionErrors.TransitionIncompatible,
            _ => null
        };
    }

    public static ReconciliationDecisionCurrentState CurrentState(
        ReconciliationDecisionDisposition disposition,
        bool activeTransactionIsInactive) => activeTransactionIsInactive
        ? ReconciliationDecisionCurrentState.Exception
        : disposition switch
        {
            ReconciliationDecisionDisposition.ConfirmedExisting => ReconciliationDecisionCurrentState.ConfirmedExisting,
            ReconciliationDecisionDisposition.CorrectedFromStatement => ReconciliationDecisionCurrentState.CorrectedFromStatement,
            ReconciliationDecisionDisposition.StatementOnly => ReconciliationDecisionCurrentState.StatementOnly,
            ReconciliationDecisionDisposition.Ambiguous => ReconciliationDecisionCurrentState.Ambiguous,
            ReconciliationDecisionDisposition.Exception => ReconciliationDecisionCurrentState.Exception,
            ReconciliationDecisionDisposition.OwnerConfirmedMatch => ReconciliationDecisionCurrentState.OwnerConfirmedMatch,
            ReconciliationDecisionDisposition.Rejected => ReconciliationDecisionCurrentState.Rejected,
            ReconciliationDecisionDisposition.Revoked => ReconciliationDecisionCurrentState.Revoked,
            ReconciliationDecisionDisposition.Replaced => ReconciliationDecisionCurrentState.Replaced,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };
}
