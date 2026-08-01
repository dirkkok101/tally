using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage.Evaluation;

namespace Tally.Infrastructure.Classify.Storage.Feedback;

/// <summary>
/// Append-only classification_feedback and rule_proposal persistence
/// (DM-CLASSIFY-FEEDBACK-PROPOSAL / TASK-CLASSIFY-RULEBOOK-FEEDBACK-PROPOSALS).
/// Never mutates prior feedback, proposals, outcomes, or Ledger allocations.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationFeedbackStore
{
    public async Task PersistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyFeedbackRow feedback,
        ClassifyRuleProposalRow? proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        await InsertFeedbackAsync(connection, transaction, feedback, cancellationToken);
        if (proposal is not null)
        {
            if (!string.Equals(proposal.FeedbackId, feedback.FeedbackId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Proposal feedback_id must match the feedback row.");
            }

            await InsertProposalAsync(connection, transaction, proposal, cancellationToken);
        }
    }

    public async Task InsertFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyFeedbackRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.Reason) || row.Reason.Length > 1024)
        {
            throw new InvalidOperationException("Feedback reason must be 1..1024 characters.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_feedback (
                feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                rule_set_version_id, decision_type, prior_ledger_allocation_id, resulting_ledger_allocation_id,
                reason, actor, occurred_at
            ) VALUES (
                $feedback_id, $outcome_id, $transaction_id, $evaluation_id, $normalization_version,
                $rule_set_version_id, $decision_type, $prior_ledger_allocation_id, $resulting_ledger_allocation_id,
                $reason, $actor, $occurred_at
            );
            """;
        command.Parameters.AddWithValue("$feedback_id", row.FeedbackId);
        command.Parameters.AddWithValue("$outcome_id", row.OutcomeId);
        command.Parameters.AddWithValue("$transaction_id", row.TransactionId);
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$normalization_version", row.NormalizationVersion);
        command.Parameters.AddWithValue("$rule_set_version_id", row.RuleSetVersionId);
        command.Parameters.AddWithValue("$decision_type", row.DecisionType);
        command.Parameters.AddWithValue("$prior_ledger_allocation_id", (object?)row.PriorLedgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resulting_ledger_allocation_id", (object?)row.ResultingLedgerAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", row.Reason);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$occurred_at", row.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertProposalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyRuleProposalRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ProposedScopeFingerprint.Length != 64)
        {
            throw new InvalidOperationException("proposed_scope_fingerprint must be 64 hex chars.");
        }

        if (!string.Equals(row.LifecycleState, "draft", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Feedback proposals must remain draft (never active).");
        }

        if (!string.Equals(row.RuleOrigin, "feedback_derived", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Feedback proposals must be feedback_derived origin.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rule_proposal (
                proposal_id, feedback_id, rule_origin, proposal_type, source_rule_version_id,
                proposed_scope_fingerprint, proposed_category_id, lifecycle_state, created_at
            ) VALUES (
                $proposal_id, $feedback_id, $rule_origin, $proposal_type, $source_rule_version_id,
                $proposed_scope_fingerprint, $proposed_category_id, $lifecycle_state, $created_at
            );
            """;
        command.Parameters.AddWithValue("$proposal_id", row.ProposalId);
        command.Parameters.AddWithValue("$feedback_id", row.FeedbackId);
        command.Parameters.AddWithValue("$rule_origin", row.RuleOrigin);
        command.Parameters.AddWithValue("$proposal_type", row.ProposalType);
        command.Parameters.AddWithValue("$source_rule_version_id", (object?)row.SourceRuleVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$proposed_scope_fingerprint", row.ProposedScopeFingerprint);
        command.Parameters.AddWithValue("$proposed_category_id", (object?)row.ProposedCategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lifecycle_state", row.LifecycleState);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ClassifyOutcomeRow?> GetOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string outcomeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                   category_id, item_lifecycle_fingerprint, safe_reason
            FROM classification_outcome
            WHERE outcome_id = $id;
            """;
        command.Parameters.AddWithValue("$id", outcomeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapOutcome(reader) : null;
    }

    /// <summary>
    /// Latest durable apply_item allocation pair for a transaction (prior expected + resulting).
    /// Does not rewrite or invent allocations.
    /// </summary>
    public async Task<(string? PriorAllocationId, string? ResultingAllocationId, string? CategoryId)?>
        FindLatestAppliedAllocationAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string transactionId,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT expected_active_allocation_id, ledger_allocation_id, category_id
            FROM apply_item
            WHERE transaction_id = $tx
              AND item_state IN ('applied', 'already_applied')
              AND ledger_allocation_id IS NOT NULL
            ORDER BY apply_id DESC, ordinal DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tx", transactionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<ClassifyFeedbackRow?> GetFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT feedback_id, outcome_id, transaction_id, evaluation_id, normalization_version,
                   rule_set_version_id, decision_type, prior_ledger_allocation_id, resulting_ledger_allocation_id,
                   reason, actor, occurred_at
            FROM classification_feedback
            WHERE feedback_id = $id;
            """;
        command.Parameters.AddWithValue("$id", feedbackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassifyFeedbackRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11));
    }

    public async Task<ClassifyRuleProposalRow?> GetProposalByFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT proposal_id, feedback_id, rule_origin, proposal_type, source_rule_version_id,
                   proposed_scope_fingerprint, proposed_category_id, lifecycle_state, created_at
            FROM rule_proposal
            WHERE feedback_id = $id;
            """;
        command.Parameters.AddWithValue("$id", feedbackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassifyRuleProposalRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8));
    }

    public async Task<long> CountFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM classification_feedback;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountProposalsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM rule_proposal;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}
