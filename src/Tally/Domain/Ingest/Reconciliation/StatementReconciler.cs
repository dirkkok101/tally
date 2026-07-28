namespace Tally.Domain.Ingest.Reconciliation;

public enum ReconciliationControlState { Satisfied, Mismatched, Unavailable }

public sealed record ReconciliationRecord(string SourceRecordId, long? MovementMinor, long? RunningBalanceMinor, long? SourceControlMinor);

public sealed record ReconciliationControlResult(string Name, ReconciliationControlState State);

public sealed record StatementReconciliationResult(bool FullyReconciled, IReadOnlyList<ReconciliationControlResult> Controls);

public static class StatementReconciler
{
    public static StatementReconciliationResult Reconcile(long? openingBalanceMinor, long? closingBalanceMinor, IReadOnlyList<ReconciliationRecord> records)
    {
        var controls = new List<ReconciliationControlResult>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // FR-INGEST-SOURCE-RECONCILIATION: every source record is accounted exactly once when it has a
        // stable unique id. Explicit outcomes without a movement (excluded-non-transaction, blocked,
        // exact-duplicate) still count — only missing/blank or duplicate ids fail this control.
        // Opening/closing and running-balance controls use MovementMinor separately and already skip nulls.
        var accounted = records.All(record => !string.IsNullOrWhiteSpace(record.SourceRecordId) && ids.Add(record.SourceRecordId));
        controls.Add(new("record_accounting", accounted ? ReconciliationControlState.Satisfied : ReconciliationControlState.Mismatched));
        if (openingBalanceMinor is null || closingBalanceMinor is null)
            controls.Add(new("opening_to_closing", ReconciliationControlState.Unavailable));
        else
        {
            var movement = records.Where(record => record.MovementMinor is not null).Aggregate(0L, (sum, record) => checked(sum + record.MovementMinor!.Value));
            controls.Add(new("opening_to_closing", checked(openingBalanceMinor.Value + movement) == closingBalanceMinor.Value ? ReconciliationControlState.Satisfied : ReconciliationControlState.Mismatched));
        }

        long? running = openingBalanceMinor;
        foreach (var record in records)
        {
            if (running is not null && record.MovementMinor is not null) running = checked(running.Value + record.MovementMinor.Value);
            if (record.RunningBalanceMinor is null)
                controls.Add(new($"running:{record.SourceRecordId}", ReconciliationControlState.Unavailable));
            else
            {
                controls.Add(new($"running:{record.SourceRecordId}", running is null
                    ? ReconciliationControlState.Unavailable
                    : running == record.RunningBalanceMinor
                        ? ReconciliationControlState.Satisfied
                        : ReconciliationControlState.Mismatched));
                running = record.RunningBalanceMinor;
            }

            controls.Add(new($"source:{record.SourceRecordId}", record.SourceControlMinor is null
                ? ReconciliationControlState.Unavailable
                : record.MovementMinor == record.SourceControlMinor
                    ? ReconciliationControlState.Satisfied
                    : ReconciliationControlState.Mismatched));
        }

        return new(controls.All(control => control.State != ReconciliationControlState.Mismatched), controls);
    }
}
