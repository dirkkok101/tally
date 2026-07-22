using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Infrastructure.Storage.Reconciliation;

public sealed class ReconciliationCoverageStore(
    LedgerDb database,
    LedgerConnectionFactory connectionFactory,
    TransactionStore transactionStore)
{
    public async Task<StatementCoveragePreparation> PrepareAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NormalizedStatementCoverage input,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScope(connection, transaction, input.ScopeId, cancellationToken);
        if (scope is null) return StatementCoveragePreparation.Failure(ReconciliationCoverageErrors.ScopeNotFound);
        var evidenceIds = await ReadEvidenceIds(connection, transaction, input.ScopeId, cancellationToken);
        if (StatementCoveragePolicy.ValidateScope(input, scope, evidenceIds) is { } scopeError)
            return StatementCoveragePreparation.Failure(scopeError);
        if (!await EvidenceMembersAreCompatible(connection, transaction, scope, cancellationToken))
            return StatementCoveragePreparation.Failure(ReconciliationCoverageErrors.ScopeConflict);
        if (await HasCoverage(connection, transaction, input.ScopeId, cancellationToken))
            return StatementCoveragePreparation.Failure(ReconciliationCoverageErrors.AlreadyCompleted);

        var decisions = await ReadCurrentDecisions(connection, transaction, input.ScopeId, cancellationToken);
        var eligible = await ReadEligibleTransactions(connection, transaction, scope, cancellationToken);
        var eligibleSet = eligible.ToHashSet(StringComparer.Ordinal);
        if (StatementCoveragePolicy.ValidateRows(input.ScopeId, decisions, eligibleSet) is { } outcomeError)
            return StatementCoveragePreparation.Failure(outcomeError);
        return StatementCoveragePreparation.Success(scope, evidenceIds, decisions, eligible);
    }

    public async Task InsertCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCoveragePreparation preparation,
        string actor,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        var scope = preparation.Scope!;
        foreach (var decision in preparation.Decisions)
        {
            await InsertEntry(
                connection,
                transaction,
                LedgerId.New().ToString(),
                scope.ScopeId,
                decision.EvidenceId,
                decision.ActiveTransactionId,
                StoredOutcome(decision.Outcome),
                decision.Reason,
                decision.DecisionId,
                actor,
                recordedAt,
                cancellationToken);
        }

        var covered = preparation.Decisions
            .SelectMany(StatementCoveragePolicy.CoveredTransactionIds)
            .ToHashSet(StringComparer.Ordinal);
        var anchorEvidenceId = preparation.EvidenceIds[0];
        foreach (var transactionId in preparation.EligibleTransactionIds.Where(transactionId => !covered.Contains(transactionId)))
        {
            await InsertEntry(
                connection,
                transaction,
                LedgerId.New().ToString(),
                scope.ScopeId,
                anchorEvidenceId,
                transactionId,
                "recorded_absent_from_statement",
                StatementCoveragePolicy.RecordedAbsentReason,
                null,
                actor,
                recordedAt,
                cancellationToken);
        }
    }

    public async Task<StatementCoverageSummary?> GetAsync(string scopeId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var summary = await GetAsync(connection, transaction, scopeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }

    public async Task<StatementCoverageSummary?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScope(connection, transaction, scopeId, cancellationToken);
        if (scope is null) return null;
        var history = await ReadHistory(connection, transaction, scope, cancellationToken);
        if (history.Count == 0) return null;

        var currentRows = (await ReadCurrentDecisions(connection, transaction, scopeId, cancellationToken))
            .Select(ToCurrentRow)
            .OrderBy(member => member.StableId, StringComparer.Ordinal)
            .ToArray();
        var eligibleIds = history
            .Where(item => item.Kind == StatementCoverageMemberKind.EligibleTransaction)
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in history.Where(item =>
                     item.Kind == StatementCoverageMemberKind.StatementRow
                     && item.Outcome is StatementCoverageOutcome.ConfirmedExisting or StatementCoverageOutcome.OwnerConfirmedMatch
                     && item.ActiveTransactionId is not null))
        {
            if (await WasRecordedBeforeScope(connection, transaction, row.ActiveTransactionId!, scope, cancellationToken))
                eligibleIds.Add(row.ActiveTransactionId!);
        }

        var orderedEligibleIds = eligibleIds.Order(StringComparer.Ordinal).ToArray();

        var currentTransactions = new List<StatementCoverageMember>(orderedEligibleIds.Length);
        foreach (var transactionId in orderedEligibleIds)
        {
            var coveringRow = currentRows.FirstOrDefault(row =>
                string.Equals(row.ActiveTransactionId, transactionId, StringComparison.Ordinal)
                || row.Outcome == StatementCoverageOutcome.CorrectedFromStatement
                    && string.Equals(row.PriorTransactionId, transactionId, StringComparison.Ordinal));
            currentTransactions.Add(coveringRow is null
                ? new(
                    StatementCoverageMemberKind.EligibleTransaction,
                    transactionId,
                    null,
                    null,
                    transactionId,
                    StatementCoverageOutcome.RecordedAbsentFromStatement,
                    StatementCoveragePolicy.RecordedAbsentReason,
                    null)
                : new(
                    StatementCoverageMemberKind.EligibleTransaction,
                    transactionId,
                    coveringRow.EvidenceId,
                    coveringRow.PriorTransactionId,
                    coveringRow.ActiveTransactionId,
                    StatementCoverageOutcome.StatementReconciled,
                    "confirmed_by_statement_scope",
                    coveringRow.DecisionId));
        }

        var current = currentRows
            .Concat(currentTransactions)
            .OrderBy(member => member.Kind)
            .ThenBy(member => member.StableId, StringComparer.Ordinal)
            .ToArray();
        var counts = current
            .GroupBy(member => (member.Kind, member.Outcome))
            .OrderBy(group => group.Key.Kind)
            .ThenBy(group => group.Key.Outcome)
            .Select(group => new StatementCoverageCount(group.Key.Kind, group.Key.Outcome, group.Count()))
            .ToArray();
        return new(
            scope.ScopeId,
            scope.AccountId,
            scope.PeriodStart,
            scope.PeriodEnd,
            scope.ManifestOpaqueReference,
            StatementCoveragePolicy.PolicyId,
            StatementCoveragePolicy.PolicyVersion,
            currentRows.Length,
            orderedEligibleIds.Length,
            current,
            counts,
            history,
            history.Select(item => item.RecordedAt).Max(StringComparer.Ordinal)!);
    }

    private async Task<IReadOnlyList<string>> ReadEligibleTransactions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCoverageScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT fact.transaction_id
            FROM transaction_fact AS fact
            WHERE fact.account_id = $accountId
              AND fact.effective_date >= $periodStart
              AND fact.effective_date <= $periodEnd
              AND fact.recorded_at < $scopeCreatedAt
              AND NOT EXISTS (
                  SELECT 1 FROM transaction_lifecycle_event AS lifecycle
                  WHERE lifecycle.transaction_id = fact.transaction_id)
              AND NOT EXISTS (
                  SELECT 1
                  FROM evidence_active_confirming_target AS target
                  WHERE target.transaction_id = fact.transaction_id
                    AND NOT EXISTS (
                        SELECT 1 FROM statement_scope_evidence AS member
                        WHERE member.scope_id = $scopeId AND member.evidence_id = target.evidence_id))
            ORDER BY fact.transaction_id;
            """,
            ("$accountId", scope.AccountId),
            ("$periodStart", scope.PeriodStart),
            ("$periodEnd", scope.PeriodEnd),
            ("$scopeCreatedAt", scope.CreatedAt),
            ("$scopeId", scope.ScopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var active = new List<string>(ids.Count);
        foreach (var transactionId in ids)
        {
            var detail = await transactionStore.GetAsync(connection, transaction, transactionId, includeHistory: false, cancellationToken);
            if (detail?.LifecycleStatus == TransactionLifecycleStatus.Active) active.Add(transactionId);
        }

        return active;
    }

    private static async Task<StatementCoverageScope?> ReadScope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_at
            FROM statement_scope
            WHERE scope_id = $scopeId;
            """, ("$scopeId", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6))
            : null;
    }

    private static async Task<IReadOnlyList<string>> ReadEvidenceIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT evidence_id FROM statement_scope_evidence
            WHERE scope_id = $scopeId
            ORDER BY evidence_id;
            """, ("$scopeId", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<IReadOnlyList<StatementCoverageDecision>> ReadCurrentDecisions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT member.evidence_id,
                   decision.decision_id,
                   decision.prior_transaction_id,
                   decision.active_transaction_id,
                   decision.disposition,
                   decision.reason,
                   decision.decided_at,
                   decision.statement_authority_basis,
                   CASE
                       WHEN decision.active_transaction_id IS NOT NULL AND EXISTS (
                           SELECT 1 FROM transaction_lifecycle_event AS lifecycle
                           WHERE lifecycle.transaction_id = decision.active_transaction_id)
                       THEN 1 ELSE 0
                   END
            FROM statement_scope_evidence AS member
            LEFT JOIN reconciliation_current_v2 AS decision ON decision.evidence_id = member.evidence_id
            WHERE member.scope_id = $scopeId
            ORDER BY member.evidence_id;
            """, ("$scopeId", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var decisions = new List<StatementCoverageDecision>();
        while (await reader.ReadAsync(cancellationToken))
        {
            decisions.Add(reader.IsDBNull(1)
                ? new(reader.GetString(0), null, null, null, StatementCoverageOutcome.Exception, "outcome_missing", null, null)
                : new(
                    reader.GetString(0),
                    reader.GetString(1),
                    Optional(reader, 2),
                    Optional(reader, 3),
                    ParseDecisionOutcome(reader.GetString(4), reader.GetInt64(8) == 1),
                    reader.GetString(5),
                    reader.GetString(6),
                    Optional(reader, 7)));
        }

        return decisions;
    }

    private static async Task<IReadOnlyList<StatementCoverageHistoryItem>> ReadHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCoverageScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT entry.coverage_entry_id,
                   entry.evidence_id,
                   entry.transaction_id,
                   entry.outcome,
                   entry.reason,
                   entry.active_decision_id,
                   entry.recorded_by,
                   entry.recorded_at,
                   authority.prior_transaction_id,
                   authority.active_transaction_id,
                   authority.disposition_detail
            FROM coverage_entry AS entry
            LEFT JOIN reconciliation_decision_authority AS authority ON authority.decision_id = entry.active_decision_id
            WHERE entry.scope_id = $scopeId
            ORDER BY entry.recorded_at, entry.coverage_entry_id;
            """, ("$scopeId", scope.ScopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new List<StatementCoverageHistoryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedOutcome = reader.GetString(3);
            var absent = storedOutcome == "recorded_absent_from_statement";
            var transactionId = Optional(reader, 2);
            var evidenceId = reader.GetString(1);
            var prior = Optional(reader, 8);
            var active = Optional(reader, 9) ?? transactionId;
            var outcome = ParseStoredOutcome(storedOutcome, Optional(reader, 10));
            history.Add(new(
                reader.GetString(0),
                absent ? StatementCoverageMemberKind.EligibleTransaction : StatementCoverageMemberKind.StatementRow,
                absent ? transactionId! : evidenceId,
                absent ? null : evidenceId,
                prior,
                active,
                outcome,
                reader.GetString(4),
                Optional(reader, 5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return history;
    }

    private static async Task<bool> HasCoverage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM coverage_entry WHERE scope_id = $scopeId);",
            ("$scopeId", scopeId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> WasRecordedBeforeScope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        StatementCoverageScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM transaction_fact
                WHERE transaction_id = $transactionId
                  AND account_id = $accountId
                  AND effective_date >= $periodStart
                  AND effective_date <= $periodEnd
                  AND recorded_at < $scopeCreatedAt);
            """,
            ("$transactionId", transactionId),
            ("$accountId", scope.AccountId),
            ("$periodStart", scope.PeriodStart),
            ("$periodEnd", scope.PeriodEnd),
            ("$scopeCreatedAt", scope.CreatedAt));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> EvidenceMembersAreCompatible(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCoverageScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT NOT EXISTS (
                SELECT 1
                FROM statement_scope_evidence AS member
                JOIN evidence_record AS evidence ON evidence.evidence_id = member.evidence_id
                LEFT JOIN evidence_observation AS observation ON observation.evidence_id = evidence.evidence_id
                WHERE member.scope_id = $scopeId
                  AND (evidence.kind <> 'statement_row'
                       OR observation.account_id IS NULL
                       OR observation.account_id <> $accountId
                       OR observation.currency_code <> 'ZAR'
                       OR observation.transaction_date IS NULL
                       OR observation.transaction_date < $periodStart
                       OR observation.transaction_date > $periodEnd));
            """,
            ("$scopeId", scope.ScopeId),
            ("$accountId", scope.AccountId),
            ("$periodStart", scope.PeriodStart),
            ("$periodEnd", scope.PeriodEnd));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task InsertEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string coverageEntryId,
        string scopeId,
        string evidenceId,
        string? transactionId,
        string outcome,
        string reason,
        string? decisionId,
        string actor,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO coverage_entry(
                coverage_entry_id, scope_id, evidence_id, transaction_id, outcome,
                reason, active_decision_id, recorded_by, recorded_at)
            VALUES ($entryId, $scopeId, $evidenceId, $transactionId, $outcome,
                    $reason, $decisionId, $actor, $recordedAt);
            """,
            ("$entryId", coverageEntryId),
            ("$scopeId", scopeId),
            ("$evidenceId", evidenceId),
            ("$transactionId", transactionId),
            ("$outcome", outcome),
            ("$reason", reason),
            ("$decisionId", decisionId),
            ("$actor", actor),
            ("$recordedAt", recordedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StatementCoverageMember ToCurrentRow(StatementCoverageDecision decision) => new(
        StatementCoverageMemberKind.StatementRow,
        decision.EvidenceId,
        decision.EvidenceId,
        decision.PriorTransactionId,
        decision.ActiveTransactionId,
        decision.Outcome,
        decision.Reason,
        decision.DecisionId);

    private static StatementCoverageOutcome ParseDecisionOutcome(string disposition, bool activeTransactionIsInactive)
    {
        var decisionDisposition = disposition switch
        {
            "confirmed_existing" => ReconciliationDecisionDisposition.ConfirmedExisting,
            "corrected_from_statement" => ReconciliationDecisionDisposition.CorrectedFromStatement,
            "statement_only" => ReconciliationDecisionDisposition.StatementOnly,
            "ambiguous" => ReconciliationDecisionDisposition.Ambiguous,
            "exception" => ReconciliationDecisionDisposition.Exception,
            "owner_confirmed_match" => ReconciliationDecisionDisposition.OwnerConfirmedMatch,
            "rejected" => ReconciliationDecisionDisposition.Rejected,
            "revoked" => ReconciliationDecisionDisposition.Revoked,
            "replaced" => ReconciliationDecisionDisposition.Replaced,
            _ => throw new InvalidOperationException("Stored reconciliation disposition is not supported by statement coverage.")
        };
        return ReconciliationStateReducer.CurrentState(decisionDisposition, activeTransactionIsInactive) switch
        {
            ReconciliationDecisionCurrentState.ConfirmedExisting => StatementCoverageOutcome.ConfirmedExisting,
            ReconciliationDecisionCurrentState.CorrectedFromStatement => StatementCoverageOutcome.CorrectedFromStatement,
            ReconciliationDecisionCurrentState.StatementOnly => StatementCoverageOutcome.StatementOnly,
            ReconciliationDecisionCurrentState.Ambiguous => StatementCoverageOutcome.Ambiguous,
            ReconciliationDecisionCurrentState.OwnerConfirmedMatch or ReconciliationDecisionCurrentState.Replaced
                => StatementCoverageOutcome.OwnerConfirmedMatch,
            ReconciliationDecisionCurrentState.Exception
                or ReconciliationDecisionCurrentState.Rejected
                or ReconciliationDecisionCurrentState.Revoked => StatementCoverageOutcome.Exception,
            _ => throw new InvalidOperationException("Reduced reconciliation state is not supported by statement coverage.")
        };
    }

    private static StatementCoverageOutcome ParseStoredOutcome(string outcome, string? dispositionDetail) => outcome switch
    {
        "statement_reconciled" when dispositionDetail == "corrected_from_statement" => StatementCoverageOutcome.CorrectedFromStatement,
        "statement_reconciled" => StatementCoverageOutcome.ConfirmedExisting,
        "statement_only" => StatementCoverageOutcome.StatementOnly,
        "recorded_absent_from_statement" => StatementCoverageOutcome.RecordedAbsentFromStatement,
        "ambiguous_match" => StatementCoverageOutcome.Ambiguous,
        "owner_confirmed_match" => StatementCoverageOutcome.OwnerConfirmedMatch,
        "reconciliation_exception" => StatementCoverageOutcome.Exception,
        _ => throw new InvalidOperationException("Stored coverage outcome is not supported.")
    };

    private static string StoredOutcome(StatementCoverageOutcome outcome) => outcome switch
    {
        StatementCoverageOutcome.ConfirmedExisting or StatementCoverageOutcome.CorrectedFromStatement => "statement_reconciled",
        StatementCoverageOutcome.StatementOnly => "statement_only",
        StatementCoverageOutcome.Ambiguous => "ambiguous_match",
        StatementCoverageOutcome.OwnerConfirmedMatch => "owner_confirmed_match",
        StatementCoverageOutcome.Exception => "reconciliation_exception",
        _ => throw new InvalidOperationException("Statement row outcome cannot be stored in coverage.")
    };

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
}

public sealed record StatementCoveragePreparation(
    StatementCoverageScope? Scope,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<StatementCoverageDecision> Decisions,
    IReadOnlyList<string> EligibleTransactionIds,
    string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;

    public static StatementCoveragePreparation Success(
        StatementCoverageScope scope,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<StatementCoverageDecision> decisions,
        IReadOnlyList<string> eligibleTransactionIds) => new(scope, evidenceIds, decisions, eligibleTransactionIds, null);

    public static StatementCoveragePreparation Failure(string errorCode) => new(null, [], [], [], errorCode);
}
