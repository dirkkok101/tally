using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Domain.Ledger.Relationships;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Infrastructure.Storage.Reconciliation;

public static class StatementCorrectionEffectErrors
{
    public const string Conflict = "LEDGER-RECONCILIATION-CORRECTION-CONFLICT";
}

public sealed record StatementCorrectionEffectWrite(
    string CorrectionId,
    string DecisionId,
    string ConfirmingLinkEventId,
    string SupersessionLifecycleEventId,
    string EvidenceId,
    string PriorTransactionId,
    string ReplacementTransactionId,
    TransactionFact StatementFact,
    string? PreviousDecisionId,
    string? PreviousConfirmingLinkEventId,
    string PolicyId,
    string PolicyVersion,
    string MatchBasis,
    string StatementAuthorityBasis,
    string Reason,
    string Actor,
    string OccurredAt,
    IReadOnlyList<string> RelationshipIds);

public sealed record StatementCorrectionEffectResult(
    bool IsSuccess,
    bool ReviewRequired,
    string? ErrorCode,
    string? CorrectionId,
    string? DecisionId,
    string? ReplacementTransactionId,
    string? SupersessionLifecycleEventId,
    string? ConfirmingLinkEventId,
    string? CategoryAllocationEventId,
    string? PoolAssignmentEventId,
    string? AttributionEventId,
    PaymentAttributionCarryForwardResolution? PaymentResolution,
    IReadOnlyList<string> RelationshipLifecycleEventIds)
{
    public static StatementCorrectionEffectResult Failure(string errorCode, bool reviewRequired = false) =>
        new(false, reviewRequired, errorCode, null, null, null, null, null, null, null, null, null, []);
}

public sealed class StatementCorrectionEffectWriter(
    ReconciliationWriteStore writeStore,
    ReconciliationDecisionStore decisionStore,
    TransactionStore transactionStore,
    CategoryAllocationStore categoryStore,
    PaymentAttributionStore paymentStore,
    PaymentIdentityStore paymentIdentityStore,
    PoolAssignmentStore poolStore,
    RelationshipStore relationshipStore)
{
    public async Task<StatementCorrectionEffectResult> AppendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCorrectionEffectWrite write,
        CancellationToken cancellationToken)
    {
        if (await FindExistingAsync(connection, transaction, write, cancellationToken) is { } existing)
        {
            return existing;
        }

        const string savepoint = "statement_correction_effect";
        await ExecuteAsync(connection, transaction, $"SAVEPOINT {savepoint};", cancellationToken);
        try
        {
            var supersession = await transactionStore.AppendStatementSupersessionAsync(
                connection,
                transaction,
                write.SupersessionLifecycleEventId,
                write.PriorTransactionId,
                write.ReplacementTransactionId,
                write.StatementFact,
                write.Reason,
                write.Actor,
                write.OccurredAt,
                async (replacementId, token) =>
                {
                    if (!string.Equals(replacementId, write.ReplacementTransactionId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    await decisionStore.InsertTransitionAsync(
                        connection,
                        transaction,
                        new(
                            write.DecisionId,
                            write.EvidenceId,
                            write.PreviousDecisionId,
                            write.PriorTransactionId,
                            write.ReplacementTransactionId,
                            "replaced",
                            "corrected_from_statement",
                            write.PolicyId,
                            write.PolicyVersion,
                            write.MatchBasis,
                            write.StatementAuthorityBasis,
                            write.Reason,
                            write.Actor,
                            write.OccurredAt),
                        token);
                    return write.DecisionId;
                },
                cancellationToken);
            if (!supersession.IsSuccess)
            {
                await RollbackAsync(connection, transaction, savepoint);
                return StatementCorrectionEffectResult.Failure(supersession.ErrorCode!);
            }

            await AppendConfirmingLinkAsync(connection, transaction, write, cancellationToken);
            var categoryEventId = await categoryStore.CarryForwardAsync(
                connection,
                transaction,
                write.PriorTransactionId,
                write.ReplacementTransactionId,
                write.DecisionId,
                write.Reason,
                write.Actor,
                write.OccurredAt,
                cancellationToken);
            var pool = await poolStore.CarryForwardAsync(
                connection,
                transaction,
                write.PriorTransactionId,
                write.ReplacementTransactionId,
                write.DecisionId,
                write.Reason,
                write.Actor,
                write.OccurredAt,
                cancellationToken);
            var payment = await paymentStore.CarryForwardOrUnknownAsync(
                connection,
                transaction,
                paymentIdentityStore,
                write.PriorTransactionId,
                write.ReplacementTransactionId,
                write.DecisionId,
                write.Reason,
                write.Actor,
                write.OccurredAt,
                cancellationToken);

            var relationshipLifecycleIds = new List<string>(write.RelationshipIds.Count);
            foreach (var relationshipId in write.RelationshipIds)
            {
                var relationship = await relationshipStore.ReplaceForStatementCorrectionAsync(
                    connection,
                    transaction,
                    relationshipId,
                    write.PriorTransactionId,
                    write.ReplacementTransactionId,
                    write.DecisionId,
                    write.Reason,
                    write.Actor,
                    write.OccurredAt,
                    cancellationToken);
                if (relationship.ReviewRequired)
                {
                    await RollbackAsync(connection, transaction, savepoint);
                    return StatementCorrectionEffectResult.Failure(
                        relationship.ErrorCode ?? RelationshipLifecycleErrors.ReviewRequired,
                        reviewRequired: true);
                }

                relationshipLifecycleIds.Add(relationship.LifecycleEventId!);
            }

            await InsertCorrectionAsync(
                connection,
                transaction,
                write,
                categoryEventId,
                pool.PoolAssignmentEventId,
                payment.AttributionEventId,
                payment.Resolution,
                relationshipLifecycleIds,
                cancellationToken);
            await ExecuteAsync(connection, transaction, $"RELEASE SAVEPOINT {savepoint};", cancellationToken);
            return Success(
                write,
                categoryEventId,
                pool.PoolAssignmentEventId,
                payment.AttributionEventId,
                payment.Resolution,
                relationshipLifecycleIds);
        }
        catch
        {
            await RollbackAsync(connection, transaction, savepoint);
            throw;
        }
    }

    private async Task AppendConfirmingLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCorrectionEffectWrite write,
        CancellationToken cancellationToken)
    {
        if (write.PreviousConfirmingLinkEventId is null)
        {
            await writeStore.InsertConfirmingLinkAsync(
                connection,
                transaction,
                write.ConfirmingLinkEventId,
                write.EvidenceId,
                write.ReplacementTransactionId,
                write.DecisionId,
                write.Reason,
                write.Actor,
                write.OccurredAt,
                cancellationToken);
            return;
        }

        await decisionStore.InsertLinkTransitionAsync(
            connection,
            transaction,
            new(
                write.ConfirmingLinkEventId,
                write.EvidenceId,
                write.ReplacementTransactionId,
                write.DecisionId,
                "replace",
                write.PreviousConfirmingLinkEventId,
                write.Reason,
                write.Actor,
                write.OccurredAt),
            cancellationToken);
    }

    private static async Task InsertCorrectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCorrectionEffectWrite write,
        string? categoryEventId,
        string poolEventId,
        string attributionEventId,
        PaymentAttributionCarryForwardResolution paymentResolution,
        IReadOnlyList<string> relationshipLifecycleIds,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO statement_correction(
                correction_id, decision_id, prior_transaction_id, active_transaction_id,
                supersession_lifecycle_event_id, category_resolution, category_allocation_event_id,
                pool_assignment_event_id, payment_resolution, attribution_event_id,
                authority_basis, previous_decision_id, reason, actor_context, occurred_at)
            VALUES ($correctionId, $decisionId, $priorId, $activeId,
                    $lifecycleId, $categoryResolution, $categoryEventId,
                    $poolEventId, $paymentResolution, $attributionEventId,
                    $authorityBasis, $previousDecisionId, $reason, $actor, $occurredAt);
            """,
            ("$correctionId", write.CorrectionId),
            ("$decisionId", write.DecisionId),
            ("$priorId", write.PriorTransactionId),
            ("$activeId", write.ReplacementTransactionId),
            ("$lifecycleId", write.SupersessionLifecycleEventId),
            ("$categoryResolution", categoryEventId is null ? "uncategorized" : "carry_forward"),
            ("$categoryEventId", categoryEventId),
            ("$poolEventId", poolEventId),
            ("$paymentResolution", PaymentResolutionValue(paymentResolution)),
            ("$attributionEventId", attributionEventId),
            ("$authorityBasis", write.StatementAuthorityBasis),
            ("$previousDecisionId", write.PreviousDecisionId),
            ("$reason", write.Reason),
            ("$actor", write.Actor),
            ("$occurredAt", write.OccurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);

        for (var ordinal = 0; ordinal < relationshipLifecycleIds.Count; ordinal++)
        {
            await using var relationship = Command(connection, transaction, """
                INSERT INTO statement_correction_relationship_event(
                    correction_id, ordinal, relationship_lifecycle_event_id)
                VALUES ($correctionId, $ordinal, $lifecycleId);
                """,
                ("$correctionId", write.CorrectionId),
                ("$ordinal", ordinal),
                ("$lifecycleId", relationshipLifecycleIds[ordinal]));
            await relationship.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<StatementCorrectionEffectResult?> FindExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StatementCorrectionEffectWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT correction.correction_id, correction.decision_id, decision.evidence_id,
                   correction.prior_transaction_id, correction.active_transaction_id,
                   correction.supersession_lifecycle_event_id, link.link_event_id,
                   correction.category_allocation_event_id, correction.pool_assignment_event_id,
                   correction.attribution_event_id, correction.payment_resolution,
                   correction.authority_basis, correction.previous_decision_id,
                   correction.reason, correction.actor_context, correction.occurred_at,
                   decision.policy_id, decision.policy_version, decision.match_basis,
                   fact.account_id, fact.signed_amount_minor, fact.currency_code,
                   fact.transaction_date, fact.posting_date, fact.original_description,
                   link.previous_link_event_id
            FROM statement_correction AS correction
            JOIN reconciliation_decision AS decision ON decision.decision_id = correction.decision_id
            JOIN evidence_link_event AS link
              ON link.decision_id = correction.decision_id AND link.role = 'confirming'
            JOIN transaction_fact AS fact ON fact.transaction_id = correction.active_transaction_id
            WHERE correction.correction_id = $correctionId OR correction.decision_id = $decisionId;
            """,
            ("$correctionId", write.CorrectionId),
            ("$decisionId", write.DecisionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var paymentResolution = ParsePaymentResolution(reader.GetString(10));
        var exact = reader.GetString(0) == write.CorrectionId
            && reader.GetString(1) == write.DecisionId
            && reader.GetString(2) == write.EvidenceId
            && reader.GetString(3) == write.PriorTransactionId
            && reader.GetString(4) == write.ReplacementTransactionId
            && reader.GetString(5) == write.SupersessionLifecycleEventId
            && reader.GetString(6) == write.ConfirmingLinkEventId
            && reader.GetString(11) == write.StatementAuthorityBasis
            && Optional(reader, 12) == write.PreviousDecisionId
            && reader.GetString(13) == write.Reason
            && reader.GetString(14) == write.Actor
            && reader.GetString(15) == write.OccurredAt
            && Optional(reader, 16) == write.PolicyId
            && Optional(reader, 17) == write.PolicyVersion
            && reader.GetString(18) == write.MatchBasis
            && reader.GetString(19) == write.StatementFact.AccountId
            && reader.GetInt64(20) == write.StatementFact.SignedAmount.MinorUnits
            && reader.GetString(21) == write.StatementFact.Currency.Code
            && reader.GetString(22) == write.StatementFact.TransactionDate.ToString()
            && Optional(reader, 23) == write.StatementFact.PostingDate?.ToString()
            && reader.GetString(24) == write.StatementFact.OriginalDescription
            && Optional(reader, 25) == write.PreviousConfirmingLinkEventId;
        if (!exact) return StatementCorrectionEffectResult.Failure(StatementCorrectionEffectErrors.Conflict);
        var categoryId = Optional(reader, 7);
        var poolId = reader.GetString(8);
        var attributionId = reader.GetString(9);
        await reader.DisposeAsync();

        var relationshipIds = new List<string>();
        var relationshipLifecycleIds = new List<string>();
        await using var relationships = Command(connection, transaction, """
            SELECT lifecycle.relationship_id, member.relationship_lifecycle_event_id
            FROM statement_correction_relationship_event AS member
            JOIN relationship_lifecycle_event AS lifecycle
              ON lifecycle.lifecycle_event_id = member.relationship_lifecycle_event_id
            WHERE member.correction_id = $correctionId ORDER BY member.ordinal;
            """, ("$correctionId", write.CorrectionId));
        await using var relationshipReader = await relationships.ExecuteReaderAsync(cancellationToken);
        while (await relationshipReader.ReadAsync(cancellationToken))
        {
            relationshipIds.Add(relationshipReader.GetString(0));
            relationshipLifecycleIds.Add(relationshipReader.GetString(1));
        }
        if (!relationshipIds.SequenceEqual(write.RelationshipIds, StringComparer.Ordinal))
            return StatementCorrectionEffectResult.Failure(StatementCorrectionEffectErrors.Conflict);
        return Success(write, categoryId, poolId, attributionId, paymentResolution, relationshipLifecycleIds);
    }

    private static StatementCorrectionEffectResult Success(
        StatementCorrectionEffectWrite write,
        string? categoryEventId,
        string poolEventId,
        string attributionEventId,
        PaymentAttributionCarryForwardResolution paymentResolution,
        IReadOnlyList<string> relationshipLifecycleIds) => new(
            true,
            false,
            null,
            write.CorrectionId,
            write.DecisionId,
            write.ReplacementTransactionId,
            write.SupersessionLifecycleEventId,
            write.ConfirmingLinkEventId,
            categoryEventId,
            poolEventId,
            attributionEventId,
            paymentResolution,
            relationshipLifecycleIds);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task RollbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string savepoint) => ExecuteAsync(
            connection,
            transaction,
            $"ROLLBACK TO SAVEPOINT {savepoint}; RELEASE SAVEPOINT {savepoint};",
            CancellationToken.None);

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

    private static string? Optional(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string PaymentResolutionValue(PaymentAttributionCarryForwardResolution resolution) => resolution switch
    {
        PaymentAttributionCarryForwardResolution.CarryForward => "carry_forward",
        PaymentAttributionCarryForwardResolution.UnknownInitialization => "unknown_initialization",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution))
    };

    private static PaymentAttributionCarryForwardResolution ParsePaymentResolution(string value) => value switch
    {
        "carry_forward" => PaymentAttributionCarryForwardResolution.CarryForward,
        "unknown_initialization" => PaymentAttributionCarryForwardResolution.UnknownInitialization,
        _ => throw new InvalidOperationException("Stored statement-correction payment resolution is invalid.")
    };
}
