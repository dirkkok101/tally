using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Infrastructure.Storage.Reconciliation;

public enum ReconciliationWriteBoundary
{
    StatementOnlyTransaction,
    Decision,
    DecisionAuthority,
    ConfirmingLink,
    Exception
}

public sealed class ReconciliationWriteStore(EvidenceStore evidenceStore, TransactionStore transactionStore)
{
    public static IReadOnlyList<ReconciliationWriteBoundary> BaseWriteBoundaries { get; } =
    [
        ReconciliationWriteBoundary.StatementOnlyTransaction,
        ReconciliationWriteBoundary.Decision,
        ReconciliationWriteBoundary.DecisionAuthority,
        ReconciliationWriteBoundary.ConfirmingLink,
        ReconciliationWriteBoundary.Exception
    ];

    public async Task<ReconciliationProjectionReadResult> ReadProjectionSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var evidence = await evidenceStore.GetAsync(connection, transaction, evidenceId, includeHistory: false, cancellationToken);
        if (evidence is null) return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.EvidenceNotFound);
        if (evidence.Kind != EvidenceKind.StatementRow) return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.StatementEvidenceRequired);
        if (evidence.Observation is not { AccountId: not null, SignedAmountMinor: not null, CurrencyCode: not null, TransactionDate: not null } observation)
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.IncompleteObservation);

        var scope = await ReadScope(connection, transaction, scopeId, evidenceId, cancellationToken);
        if (scope is null) return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeNotFound);
        if (string.Equals(scope.Status, "replaced", StringComparison.Ordinal)) return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeInactive);
        if (!string.Equals(scope.AccountId, observation.AccountId, StringComparison.Ordinal)) return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeConflict);

        var evidenceState = await ReadEvidenceState(connection, transaction, evidenceId, cancellationToken);
        var transactions = await ReadTransactions(connection, transaction, cancellationToken);
        return ReconciliationProjectionReadResult.Success(new(
            new(
                evidenceId,
                evidence.ContentFingerprint ?? evidence.LogicalIdentityDigest,
                observation.AccountId,
                observation.SignedAmountMinor.Value,
                observation.CurrencyCode,
                observation.TransactionDate,
                evidenceState.HasActiveConfirmingLink,
                evidenceState.HasCurrentDecision),
            new(scopeId, scope.AccountId, scope.PeriodStart, scope.PeriodEnd),
            transactions));
    }

    public Task<EvidenceRecordDetail?> GetEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken) =>
        evidenceStore.GetAsync(connection, transaction, evidenceId, includeHistory: false, cancellationToken);

    public Task InsertStatementOnlyTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        string attributionEventId,
        string poolAssignmentEventId,
        TransactionFact fact,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken) =>
        transactionStore.InsertFactAndDefaultsAsync(
            connection,
            transaction,
            transactionId,
            attributionEventId,
            assignedAttributionEventId: null,
            poolAssignmentEventId,
            fact,
            occurredAt,
            Environment.UserName,
            actor,
            cancellationToken);

    public async Task InsertDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationDecisionWrite decision,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $transactionId, $disposition, $policyId, $policyVersion,
                    $matchBasis, 0, $reason, $actor, $occurredAt, NULL);
            """,
            ("$decisionId", decision.DecisionId),
            ("$evidenceId", decision.EvidenceId),
            ("$transactionId", decision.TransactionId),
            ("$disposition", decision.BaseDisposition),
            ("$policyId", decision.PolicyId),
            ("$policyVersion", decision.PolicyVersion),
            ("$matchBasis", decision.MatchBasis),
            ("$reason", decision.Reason),
            ("$actor", decision.Actor),
            ("$occurredAt", decision.OccurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertDecisionAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationDecisionAuthorityWrite authority,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, $detail, NULL, $activeTransactionId,
                    'owner', $basis, 'v2', $occurredAt);
            """,
            ("$decisionId", authority.DecisionId),
            ("$detail", authority.DispositionDetail),
            ("$activeTransactionId", authority.ActiveTransactionId),
            ("$basis", authority.StatementAuthorityBasis),
            ("$occurredAt", authority.OccurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertConfirmingLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string linkEventId,
        string evidenceId,
        string transactionId,
        string decisionId,
        string reason,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $transactionId, 'confirming', 'link', $decisionId,
                    $reason, $actor, $occurredAt, NULL);
            """,
            ("$linkId", linkEventId),
            ("$evidenceId", evidenceId),
            ("$transactionId", transactionId),
            ("$decisionId", decisionId),
            ("$reason", reason),
            ("$actor", actor),
            ("$occurredAt", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertExceptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string exceptionId,
        string scopeId,
        string evidenceId,
        string disposition,
        string reason,
        string decisionId,
        string actor,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO reconciliation_exception(
                exception_id, scope_id, evidence_id, transaction_id, disposition,
                reason, active_decision_id, recorded_by, recorded_at)
            VALUES ($exceptionId, $scopeId, $evidenceId, NULL, $disposition,
                    $reason, $decisionId, $actor, $occurredAt);
            """,
            ("$exceptionId", exceptionId),
            ("$scopeId", scopeId),
            ("$evidenceId", evidenceId),
            ("$disposition", disposition),
            ("$reason", reason),
            ("$decisionId", decisionId),
            ("$actor", actor),
            ("$occurredAt", occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StoredScope?> ReadScope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scopeId,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT scope.account_id, scope.period_start, scope.period_end, scope.status
            FROM statement_scope AS scope
            JOIN statement_scope_evidence AS member ON member.scope_id = scope.scope_id
            WHERE scope.scope_id = $scopeId AND member.evidence_id = $evidenceId;
            """, ("$scopeId", scopeId), ("$evidenceId", evidenceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static async Task<EvidenceState> ReadEvidenceState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM evidence_active_confirming_target WHERE evidence_id = $evidenceId),
                   EXISTS(
                       SELECT 1 FROM reconciliation_current
                       WHERE evidence_id = $evidenceId AND disposition NOT IN ('revoked', 'rejected'));
            """, ("$evidenceId", evidenceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetInt64(0) == 1, reader.GetInt64(1) == 1);
    }

    private async Task<IReadOnlyList<ReconciliationProjectionTransaction>> ReadTransactions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT fact.transaction_id,
                   NOT EXISTS(SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = fact.transaction_id),
                   EXISTS(
                       SELECT 1 FROM reconciliation_current
                       WHERE transaction_id = fact.transaction_id AND disposition NOT IN ('revoked', 'rejected')),
                   EXISTS(SELECT 1 FROM evidence_active_confirming_target WHERE transaction_id = fact.transaction_id)
            FROM transaction_fact AS fact;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var states = new List<TransactionState>();
        while (await reader.ReadAsync(cancellationToken))
            states.Add(new(reader.GetString(0), reader.GetInt64(1) == 1, reader.GetInt64(2) == 1, reader.GetInt64(3) == 1));
        await reader.DisposeAsync();

        var result = new List<ReconciliationProjectionTransaction>(states.Count);
        foreach (var state in states)
        {
            var detail = await transactionStore.GetAsync(connection, transaction, state.TransactionId, includeHistory: false, cancellationToken)
                ?? throw new InvalidOperationException("Reconciliation transaction disappeared inside the write snapshot.");
            if (!Money.TryParse(detail.SignedAmount, out var amount, out _))
                throw new InvalidOperationException("Stored transaction amount is not canonical.");
            result.Add(new(
                detail.TransactionId,
                detail.AccountId,
                amount.MinorUnits,
                detail.CurrencyCode,
                detail.EffectiveDate,
                state.IsActive,
                state.HasCurrentReconciliationDecision || detail.ReconciliationState is not TransactionReconciliationState.RecordedUnreconciled
                    and not TransactionReconciliationState.RecordedAbsentFromStatement,
                state.HasActiveStatementConfirmation));
        }

        return result;
    }

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

    private sealed record StoredScope(string AccountId, string PeriodStart, string PeriodEnd, string Status);
    private sealed record EvidenceState(bool HasActiveConfirmingLink, bool HasCurrentDecision);
    private sealed record TransactionState(string TransactionId, bool IsActive, bool HasCurrentReconciliationDecision, bool HasActiveStatementConfirmation);
}

public sealed record ReconciliationDecisionWrite(
    string DecisionId,
    string EvidenceId,
    string? TransactionId,
    string BaseDisposition,
    string PolicyId,
    string PolicyVersion,
    string MatchBasis,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record ReconciliationDecisionAuthorityWrite(
    string DecisionId,
    string DispositionDetail,
    string? ActiveTransactionId,
    string StatementAuthorityBasis,
    string OccurredAt);
