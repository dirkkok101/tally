using Tally.Contracts.Ingest;

namespace Tally.Domain.Ingest.Commit;

/// <summary>
/// Closed transitions for per-candidate commit durability (DD-INGEST-COMMIT-RECOVERY).
/// Storage integer values match <see cref="CandidateReceiptState"/>.
/// </summary>
public static class CandidateCommitStates
{
    public static bool IsTerminal(CandidateReceiptState state) =>
        state is CandidateReceiptState.Accepted
            or CandidateReceiptState.ExactDuplicate
            or CandidateReceiptState.Conflicted
            or CandidateReceiptState.Rejected;

    public static bool IsReferenceBearing(CandidateReceiptState state) =>
        state is CandidateReceiptState.Accepted or CandidateReceiptState.ExactDuplicate;

    public static bool MayAttemptLedgerWrite(CandidateReceiptState state) =>
        state is CandidateReceiptState.Pending
            or CandidateReceiptState.Attempting
            or CandidateReceiptState.Unresolved;

    public static bool IsReferenceFreeTerminal(CandidateReceiptState state) =>
        state is CandidateReceiptState.Conflicted or CandidateReceiptState.Rejected;

    public static CandidateReceiptState FromStorage(int value) =>
        Enum.IsDefined(typeof(CandidateReceiptState), value)
            ? (CandidateReceiptState)value
            : throw new InvalidOperationException("Unknown candidate commit state.");

    public static int ToStorage(CandidateReceiptState state) => (int)state;
}
