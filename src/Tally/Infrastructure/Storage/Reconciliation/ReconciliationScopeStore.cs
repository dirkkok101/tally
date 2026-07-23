using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger.Reconciliation;

namespace Tally.Infrastructure.Storage.Reconciliation;

public sealed class ReconciliationScopeStore
{
    public async Task<ReconciliationScopeDetail?> GetAsync(SqliteConnection connection, SqliteTransaction transaction, string scopeId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at FROM statement_scope WHERE scope_id = $id;", ("$id", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new ReconciliationScopeDetail(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), [], reader.GetString(6), reader.GetString(7));
        await reader.DisposeAsync();
        return result with { EvidenceIds = await EvidenceIdsAsync(connection, transaction, scopeId, cancellationToken) };
    }

    public async Task<string?> ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, NormalizedStatementScopeRegistration input, CancellationToken cancellationToken)
    {
        await using (var account = Command(connection, transaction, "SELECT status FROM catalogue_current WHERE catalogue_kind = 'account' AND entity_id = $id;", ("$id", input.AccountId)))
        await using (var reader = await account.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return ReconciliationScopeErrors.AccountNotFound;
            if (reader.GetString(0) != "active") return ReconciliationScopeErrors.AccountInactive;
        }
        await using (var conflict = Command(connection, transaction, "SELECT 1 FROM statement_scope WHERE account_id = $account AND period_start = $start AND period_end = $end;", ("$account", input.AccountId), ("$start", input.PeriodStart), ("$end", input.PeriodEnd)))
            if (await conflict.ExecuteScalarAsync(cancellationToken) is not null) return ReconciliationScopeErrors.AccountPeriodConflict;

        foreach (var id in input.EvidenceIds)
        {
            await using var evidence = Command(connection, transaction, """
                SELECT record.kind, observation.account_id, observation.signed_amount_minor, observation.currency_code, observation.transaction_date,
                       EXISTS(SELECT 1 FROM statement_scope_evidence WHERE evidence_id = $id)
                FROM evidence_record AS record LEFT JOIN evidence_observation AS observation ON observation.evidence_id = record.evidence_id
                WHERE record.evidence_id = $id;
                """, ("$id", id));
            await using var reader = await evidence.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return ReconciliationScopeErrors.EvidenceNotFound;
            if (reader.GetString(0) != "statement_row") return ReconciliationScopeErrors.StatementEvidenceRequired;
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)) return ReconciliationScopeErrors.IncompleteObservation;
            if (reader.GetString(1) != input.AccountId || reader.GetString(4).CompareTo(input.PeriodStart) < 0 || reader.GetString(4).CompareTo(input.PeriodEnd) > 0) return ReconciliationScopeErrors.AccountDateConflict;
            if (reader.GetInt64(5) == 1) return ReconciliationScopeErrors.EvidenceAlreadyScoped;
        }
        return null;
    }

    public async Task<ReconciliationScopeDetail> InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string scopeId, NormalizedStatementScopeRegistration input, string actor, string now, CancellationToken cancellationToken)
    {
        await using (var scope = Command(connection, transaction, "INSERT INTO statement_scope (scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at) VALUES ($id, $account, $start, $end, $manifest, 'completed', $actor, $now);", ("$id", scopeId), ("$account", input.AccountId), ("$start", input.PeriodStart), ("$end", input.PeriodEnd), ("$manifest", input.ManifestOpaqueReference), ("$actor", actor), ("$now", now)))
            await scope.ExecuteNonQueryAsync(cancellationToken);
        foreach (var evidenceId in input.EvidenceIds)
        {
            await using var member = Command(connection, transaction, "INSERT INTO statement_scope_evidence (scope_id, evidence_id) VALUES ($scope, $evidence);", ("$scope", scopeId), ("$evidence", evidenceId));
            await member.ExecuteNonQueryAsync(cancellationToken);
        }
        return new(scopeId, input.AccountId, input.PeriodStart, input.PeriodEnd, input.ManifestOpaqueReference, "completed", input.EvidenceIds, actor, now);
    }

    private static async Task<IReadOnlyList<string>> EvidenceIdsAsync(SqliteConnection connection, SqliteTransaction transaction, string scopeId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT evidence_id FROM statement_scope_evidence WHERE scope_id = $id ORDER BY evidence_id;", ("$id", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>(); while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0)); return ids;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); return command; }
}
