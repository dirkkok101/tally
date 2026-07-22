using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Infrastructure.Storage.Reconciliation;

public sealed class ReconciliationProjectionStore(
    LedgerDb database,
    LedgerConnectionFactory connectionFactory,
    EvidenceStore evidenceStore,
    TransactionStore transactionStore)
{
    public async Task<ReconciliationProjectionReadResult> ReadAsync(
        string evidenceId,
        string scopeId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await SetQueryOnly(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var evidence = await evidenceStore.GetAsync(connection, transaction, evidenceId, includeHistory: false, cancellationToken);
        if (evidence is null)
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.EvidenceNotFound);
        }

        if (evidence.Kind != EvidenceKind.StatementRow)
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.StatementEvidenceRequired);
        }

        if (evidence.Observation is not { AccountId: not null, SignedAmountMinor: not null, CurrencyCode: not null, TransactionDate: not null } observation)
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.IncompleteObservation);
        }

        var scope = await ReadScope(connection, transaction, scopeId, evidenceId, cancellationToken);
        if (scope is null)
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeNotFound);
        }

        if (string.Equals(scope.Status, "replaced", StringComparison.Ordinal))
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeInactive);
        }

        if (!string.Equals(scope.AccountId, observation.AccountId, StringComparison.Ordinal))
        {
            return ReconciliationProjectionReadResult.Failure(ReconciliationProjectionErrors.ScopeConflict);
        }

        var evidenceState = await ReadEvidenceState(connection, transaction, evidenceId, cancellationToken);
        var transactions = await ReadTransactions(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    private static async Task SetQueryOnly(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<EvidenceState> ReadEvidenceState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM evidence_active_confirming_target AS target WHERE target.evidence_id = $evidenceId),
                   EXISTS(
                       SELECT 1 FROM reconciliation_current AS decision
                       WHERE decision.evidence_id = $evidenceId
                         AND decision.disposition NOT IN ('revoked', 'rejected'))
            """, ("$evidenceId", evidenceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetInt64(0) == 1, reader.GetInt64(1) == 1);
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

    private async Task<IReadOnlyList<ReconciliationProjectionTransaction>> ReadTransactions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT fact.transaction_id,
                   NOT EXISTS(
                       SELECT 1 FROM transaction_lifecycle_event AS lifecycle
                       WHERE lifecycle.transaction_id = fact.transaction_id),
                   EXISTS(
                       SELECT 1 FROM reconciliation_current AS decision
                       WHERE decision.transaction_id = fact.transaction_id
                         AND decision.disposition NOT IN ('revoked', 'rejected')),
                   EXISTS(
                       SELECT 1 FROM evidence_active_confirming_target AS target
                       WHERE target.transaction_id = fact.transaction_id)
            FROM transaction_fact AS fact;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var states = new List<TransactionState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new(reader.GetString(0), reader.GetInt64(1) == 1, reader.GetInt64(2) == 1, reader.GetInt64(3) == 1));
        }

        await reader.DisposeAsync();
        var transactions = new List<ReconciliationProjectionTransaction>(states.Count);
        foreach (var state in states)
        {
            var detail = await transactionStore.GetAsync(connection, transaction, state.TransactionId, includeHistory: false, cancellationToken)
                ?? throw new InvalidOperationException("Projection transaction disappeared inside a read snapshot.");
            if (!Money.TryParse(detail.SignedAmount, out var amount, out _))
            {
                throw new InvalidOperationException("Stored transaction amount is not canonical.");
            }

            transactions.Add(new(
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

        return transactions;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private sealed record StoredScope(string AccountId, string PeriodStart, string PeriodEnd, string Status);
    private sealed record EvidenceState(bool HasActiveConfirmingLink, bool HasCurrentDecision);
    private sealed record TransactionState(
        string TransactionId,
        bool IsActive,
        bool HasCurrentReconciliationDecision,
        bool HasActiveStatementConfirmation);
}

public sealed record ReconciliationProjectionReadResult(ReconciliationProjectionSource? Source, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
    public static ReconciliationProjectionReadResult Success(ReconciliationProjectionSource source) => new(source, null);
    public static ReconciliationProjectionReadResult Failure(string errorCode) => new(null, errorCode);
}
