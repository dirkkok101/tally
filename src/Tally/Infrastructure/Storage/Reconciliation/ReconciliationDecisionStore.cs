using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Infrastructure.Storage.Reconciliation;

public sealed class ReconciliationDecisionStore(
    LedgerDb database,
    LedgerConnectionFactory connectionFactory,
    EvidenceStore evidenceStore,
    TransactionStore transactionStore)
{
    public async Task<ReconciliationDecisionDetail?> GetAsync(string evidenceId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var detail = await GetAsync(connection, transaction, evidenceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return detail;
    }

    public async Task<ReconciliationDecisionDetail?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        var currentDecisionId = await CurrentDecisionId(connection, transaction, evidenceId, cancellationToken);
        if (currentDecisionId is null) return null;

        var history = await History(connection, transaction, evidenceId, cancellationToken);
        var current = history.Single(item => item.DecisionId == currentDecisionId);
        var activeLink = history
            .SelectMany(item => item.Links)
            .SingleOrDefault(link => link.IsActive && link.Role == EvidenceLinkRole.Confirming);
        var inactive = false;
        if (current.ActiveTransactionId is not null)
        {
            var transactionDetail = await transactionStore.GetAsync(connection, transaction, current.ActiveTransactionId, includeHistory: false, cancellationToken);
            inactive = transactionDetail?.LifecycleStatus is not TransactionLifecycleStatus.Active;
        }

        var linkConflict = activeLink is not null
            && !string.Equals(activeLink.TransactionId, current.ActiveTransactionId, StringComparison.Ordinal);
        var state = ReconciliationStateReducer.CurrentState(current.Disposition, inactive || linkConflict);
        return new(
            evidenceId,
            current.DecisionId,
            state,
            current.ActiveTransactionId,
            activeLink?.LinkEventId,
            state is ReconciliationDecisionCurrentState.Ambiguous
                or ReconciliationDecisionCurrentState.Exception
                or ReconciliationDecisionCurrentState.Rejected
                or ReconciliationDecisionCurrentState.Revoked,
            history);
    }

    public async Task<CandidateValidationResult> ValidateCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        string scopeId,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var statement = await ValidateStatementScopeAsync(connection, transaction, evidenceId, scopeId, cancellationToken);
        if (!statement.IsSuccess) return CandidateValidationResult.Failure(statement.ErrorCode!);

        var candidate = await transactionStore.GetAsync(connection, transaction, transactionId, includeHistory: false, cancellationToken);
        if (candidate is null) return CandidateValidationResult.Failure(ReconciliationDecisionErrors.CandidateNotFound);
        if (candidate.LifecycleStatus != TransactionLifecycleStatus.Active)
            return CandidateValidationResult.Failure(ReconciliationDecisionErrors.CandidateInactive);
        if (!string.Equals(candidate.AccountId, statement.AccountId, StringComparison.Ordinal)
            || string.CompareOrdinal(candidate.EffectiveDate, statement.PeriodStart) < 0
            || string.CompareOrdinal(candidate.EffectiveDate, statement.PeriodEnd) > 0
            || !string.Equals(candidate.CurrencyCode, statement.CurrencyCode, StringComparison.Ordinal))
            return CandidateValidationResult.Failure(ReconciliationDecisionErrors.CandidateIncompatible);

        if (await TargetHasActiveReconciliation(connection, transaction, transactionId, cancellationToken))
            return CandidateValidationResult.Failure(ReconciliationDecisionErrors.CandidateAlreadyReconciled);
        return CandidateValidationResult.Success(candidate, statement.StatementAuthorityBasis!);
    }

    public async Task<StatementScopeValidationResult> ValidateStatementScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var evidence = await evidenceStore.GetAsync(connection, transaction, evidenceId, includeHistory: false, cancellationToken);
        if (evidence is null) return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.EvidenceNotFound);
        if (evidence.Kind != EvidenceKind.StatementRow)
            return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.StatementEvidenceRequired);
        if (evidence.Observation is not { AccountId: not null, SignedAmountMinor: not null, CurrencyCode: not null, TransactionDate: not null } observation)
            return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.IncompleteObservation);

        var scope = await Scope(connection, transaction, evidenceId, scopeId, cancellationToken);
        if (scope is null) return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.ScopeNotFound);
        if (scope.Status == "replaced") return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.ScopeInactive);
        if (!string.Equals(scope.AccountId, observation.AccountId, StringComparison.Ordinal))
            return StatementScopeValidationResult.Failure(ReconciliationProjectionErrors.ScopeConflict);
        var fingerprint = evidence.ContentFingerprint ?? evidence.LogicalIdentityDigest;
        return StatementScopeValidationResult.Success(
            scope.AccountId,
            scope.PeriodStart,
            scope.PeriodEnd,
            observation.CurrencyCode,
            $"scope:{scopeId}|evidence:{fingerprint}");
    }

    public async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationDecisionTransitionWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $activeTransactionId, $baseDisposition, $policyId, $policyVersion,
                    $matchBasis, 0, $reason, $actor, $occurredAt, $previousDecisionId);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, $detailDisposition, $priorTransactionId, $activeTransactionId,
                    'owner', $statementAuthorityBasis, 'v2', $occurredAt);
            """,
            ("$decisionId", write.DecisionId),
            ("$evidenceId", write.EvidenceId),
            ("$activeTransactionId", write.ActiveTransactionId),
            ("$baseDisposition", write.BaseDisposition),
            ("$detailDisposition", write.DetailDisposition),
            ("$priorTransactionId", write.PriorTransactionId),
            ("$policyId", write.PolicyId),
            ("$policyVersion", write.PolicyVersion),
            ("$matchBasis", write.MatchBasis),
            ("$statementAuthorityBasis", write.StatementAuthorityBasis),
            ("$reason", write.Reason),
            ("$actor", write.Actor),
            ("$occurredAt", write.OccurredAt),
            ("$previousDecisionId", write.PreviousDecisionId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertLinkTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationLinkTransitionWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $transactionId, 'confirming', $action, $decisionId,
                    $reason, $actor, $occurredAt, $previousLinkId);
            """,
            ("$linkId", write.LinkEventId),
            ("$evidenceId", write.EvidenceId),
            ("$transactionId", write.TransactionId),
            ("$action", write.Action),
            ("$decisionId", write.DecisionId),
            ("$reason", write.Reason),
            ("$actor", write.Actor),
            ("$occurredAt", write.OccurredAt),
            ("$previousLinkId", write.PreviousLinkEventId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ReconciliationDecisionHistoryItem>> History(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT decision_id, evidence_id, previous_decision_id, prior_transaction_id, active_transaction_id,
                   disposition, authority_kind, statement_authority_basis, match_basis, reason, actor_context,
                   decided_at, policy_id, policy_version
            FROM reconciliation_decision_v2
            WHERE evidence_id = $evidenceId
            ORDER BY decided_at, decision_id;
            """, ("$evidenceId", evidenceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<HistoryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(
                reader.GetString(0), reader.GetString(1), Optional(reader, 2), Optional(reader, 3), Optional(reader, 4),
                ParseDisposition(reader.GetString(5)), ParseAuthority(reader.GetString(6)), Optional(reader, 7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                Optional(reader, 12), Optional(reader, 13)));
        }
        await reader.DisposeAsync();

        var history = new List<ReconciliationDecisionHistoryItem>(rows.Count);
        foreach (var row in rows)
        {
            history.Add(new(
                row.DecisionId,
                row.EvidenceId,
                row.PreviousDecisionId,
                row.PriorTransactionId,
                row.ActiveTransactionId,
                row.Disposition,
                row.AuthorityKind,
                row.StatementAuthorityBasis,
                row.MatchBasis,
                row.Reason,
                row.Actor,
                row.DecidedAt,
                row.PolicyId,
                row.PolicyVersion,
                await Links(connection, transaction, row.DecisionId, cancellationToken),
                await CarryForward(connection, transaction, row.DecisionId, cancellationToken)));
        }

        return history;
    }

    private static async Task<IReadOnlyList<ReconciliationDecisionLink>> Links(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string decisionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT link_event_id, transaction_id, role, action, decision_id, reason,
                   actor_context, recorded_at, previous_link_event_id, is_active
            FROM evidence_link_history_v2
            WHERE decision_id = $decisionId
            ORDER BY recorded_at, link_event_id;
            """, ("$decisionId", decisionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var links = new List<ReconciliationDecisionLink>();
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new(
                reader.GetString(0), reader.GetString(1), ParseRole(reader.GetString(2)), ParseAction(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Optional(reader, 8), reader.GetInt64(9) == 1));
        }
        return links;
    }

    private static async Task<ReconciliationDecisionCarryForward?> CarryForward(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string decisionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT correction_id, category_allocation_event_id, pool_assignment_event_id, attribution_event_id
            FROM statement_correction WHERE decision_id = $decisionId;
            """, ("$decisionId", decisionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var correctionId = reader.GetString(0);
        var categoryId = Optional(reader, 1);
        var poolId = reader.GetString(2);
        var attributionId = reader.GetString(3);
        await reader.DisposeAsync();

        await using var relationships = Command(connection, transaction, """
            SELECT relationship_lifecycle_event_id
            FROM statement_correction_relationship_event
            WHERE correction_id = $correctionId
            ORDER BY ordinal;
            """, ("$correctionId", correctionId));
        await using var relationshipReader = await relationships.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await relationshipReader.ReadAsync(cancellationToken)) ids.Add(relationshipReader.GetString(0));
        return new(correctionId, categoryId, poolId, attributionId, ids);
    }

    private static async Task<string?> CurrentDecisionId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT decision_id FROM reconciliation_current_v2 WHERE evidence_id = $evidenceId;",
            ("$evidenceId", evidenceId));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<ScopeRow?> Scope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT scope.account_id, scope.period_start, scope.period_end, scope.status
            FROM statement_scope AS scope
            JOIN statement_scope_evidence AS member ON member.scope_id = scope.scope_id
            WHERE member.evidence_id = $evidenceId AND scope.scope_id = $scopeId;
            """, ("$evidenceId", evidenceId), ("$scopeId", scopeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static async Task<bool> TargetHasActiveReconciliation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM reconciliation_current_v2 WHERE active_transaction_id = $transactionId),
                   EXISTS(SELECT 1 FROM evidence_active_confirming_target WHERE transaction_id = $transactionId);
            """, ("$transactionId", transactionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetInt64(0) == 1 || reader.GetInt64(1) == 1;
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

    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static EvidenceLinkRole ParseRole(string value) => value switch
    {
        "confirming" => EvidenceLinkRole.Confirming,
        "supporting" => EvidenceLinkRole.Supporting,
        _ => throw new InvalidOperationException("Stored evidence-link role is invalid.")
    };
    private static EvidenceLinkAction ParseAction(string value) => value switch
    {
        "link" => EvidenceLinkAction.Link,
        "revoke" => EvidenceLinkAction.Revoke,
        "replace" => EvidenceLinkAction.Replace,
        _ => throw new InvalidOperationException("Stored evidence-link action is invalid.")
    };
    private static ReconciliationAuthorityKind ParseAuthority(string value) => value switch
    {
        "owner" => ReconciliationAuthorityKind.Owner,
        "deterministic_policy" => ReconciliationAuthorityKind.DeterministicPolicy,
        _ => throw new InvalidOperationException("Stored reconciliation authority is invalid.")
    };
    private static ReconciliationDecisionDisposition ParseDisposition(string value) => value switch
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
        _ => throw new InvalidOperationException("Stored reconciliation disposition is invalid.")
    };

    private sealed record ScopeRow(string AccountId, string PeriodStart, string PeriodEnd, string Status);
    private sealed record HistoryRow(
        string DecisionId,
        string EvidenceId,
        string? PreviousDecisionId,
        string? PriorTransactionId,
        string? ActiveTransactionId,
        ReconciliationDecisionDisposition Disposition,
        ReconciliationAuthorityKind AuthorityKind,
        string? StatementAuthorityBasis,
        string MatchBasis,
        string Reason,
        string Actor,
        string DecidedAt,
        string? PolicyId,
        string? PolicyVersion);
}

public sealed record CandidateValidationResult(TransactionDetail? Candidate, string? StatementAuthorityBasis, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
    public static CandidateValidationResult Success(TransactionDetail candidate, string statementAuthorityBasis) => new(candidate, statementAuthorityBasis, null);
    public static CandidateValidationResult Failure(string errorCode) => new(null, null, errorCode);
}

public sealed record StatementScopeValidationResult(
    string? AccountId,
    string? PeriodStart,
    string? PeriodEnd,
    string? CurrencyCode,
    string? StatementAuthorityBasis,
    string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
    public static StatementScopeValidationResult Success(
        string accountId,
        string periodStart,
        string periodEnd,
        string currencyCode,
        string basis) => new(accountId, periodStart, periodEnd, currencyCode, basis, null);
    public static StatementScopeValidationResult Failure(string errorCode) => new(null, null, null, null, null, errorCode);
}

public sealed record ReconciliationDecisionTransitionWrite(
    string DecisionId,
    string EvidenceId,
    string? PreviousDecisionId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    string BaseDisposition,
    string DetailDisposition,
    string? PolicyId,
    string? PolicyVersion,
    string MatchBasis,
    string? StatementAuthorityBasis,
    string Reason,
    string Actor,
    string OccurredAt);

public sealed record ReconciliationLinkTransitionWrite(
    string LinkEventId,
    string EvidenceId,
    string TransactionId,
    string DecisionId,
    string Action,
    string? PreviousLinkEventId,
    string Reason,
    string Actor,
    string OccurredAt);
