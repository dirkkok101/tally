using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Features.Classify.Contract;

namespace Tally.Infrastructure.Classify.Storage.Apply;

/// <summary>
/// Atomic apply_preview / apply_preview_item persistence
/// (DM-CLASSIFY-APPLY-RUN / TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW).
/// One transaction publishes a complete expiry-bound preview or nothing.
/// Never stores raw source descriptions, amounts, or private paths.
/// Never mutates Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationApplyPreviewStore
{
    /// <summary>
    /// Persist a complete preview with ordered items. Caller owns the SQLite transaction.
    /// </summary>
    public async Task PersistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyApplyPreviewRow preview,
        IReadOnlyList<ClassifyApplyPreviewItemRow> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        if (items.Count != preview.SelectedCount)
        {
            throw new InvalidOperationException(
                "Persisted preview item count must equal selected_count.");
        }

        if (preview.EvaluationFingerprint.Length != 64
            || preview.SelectionHash.Length != 64
            || preview.StoreGenerationFingerprint.Length != 64
            || preview.CategoryLifecycleFingerprint.Length != 64
            || preview.TargetCategoryFingerprint.Length != 64
            || preview.RuleAuthorityFingerprint.Length != 64)
        {
            throw new InvalidOperationException(
                "Preview fingerprints must be 64-character hex digests.");
        }

        await InsertPreviewAsync(connection, transaction, preview, cancellationToken);

        var ordered = items
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordered[i].Ordinal != i)
            {
                throw new InvalidOperationException(
                    "Preview item ordinals must be contiguous from zero before persistence.");
            }

            if (!string.Equals(ordered[i].PreviewId, preview.PreviewId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Preview item preview_id must match the header.");
            }

            // Privacy: never accept description-like free text outside correction_reason bounds.
            if (ordered[i].CorrectionReason is { Length: > 1024 })
            {
                throw new InvalidOperationException(
                    "Correction reason exceeds bound.");
            }

            await InsertItemAsync(connection, transaction, ordered[i], cancellationToken);
        }
    }

    public async Task InsertPreviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyApplyPreviewRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO apply_preview (
                preview_id, operation_idempotency_key, evaluation_id, evaluation_fingerprint, selection_mode,
                selection_hash, ledger_contract_version, projection_version, store_generation_fingerprint,
                preflight_snapshot_id, preflight_expires_at, category_lifecycle_fingerprint,
                target_category_fingerprint, rule_authority_fingerprint, expires_at,
                selected_count, exclusion_count, no_suggestion_count, conflict_count, actor, created_at
            ) VALUES (
                $preview_id, $operation_idempotency_key, $evaluation_id, $evaluation_fingerprint, $selection_mode,
                $selection_hash, $ledger_contract_version, $projection_version, $store_generation_fingerprint,
                $preflight_snapshot_id, $preflight_expires_at, $category_lifecycle_fingerprint,
                $target_category_fingerprint, $rule_authority_fingerprint, $expires_at,
                $selected_count, $exclusion_count, $no_suggestion_count, $conflict_count, $actor, $created_at
            );
            """;
        command.Parameters.AddWithValue("$preview_id", row.PreviewId);
        command.Parameters.AddWithValue("$operation_idempotency_key", (object?)row.OperationIdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$evaluation_fingerprint", row.EvaluationFingerprint);
        command.Parameters.AddWithValue("$selection_mode", row.SelectionMode);
        command.Parameters.AddWithValue("$selection_hash", row.SelectionHash);
        command.Parameters.AddWithValue("$ledger_contract_version", row.LedgerContractVersion);
        command.Parameters.AddWithValue("$projection_version", row.ProjectionVersion);
        command.Parameters.AddWithValue("$store_generation_fingerprint", row.StoreGenerationFingerprint);
        command.Parameters.AddWithValue("$preflight_snapshot_id", row.PreflightSnapshotId);
        command.Parameters.AddWithValue("$preflight_expires_at", row.PreflightExpiresAt);
        command.Parameters.AddWithValue("$category_lifecycle_fingerprint", row.CategoryLifecycleFingerprint);
        command.Parameters.AddWithValue("$target_category_fingerprint", row.TargetCategoryFingerprint);
        command.Parameters.AddWithValue("$rule_authority_fingerprint", row.RuleAuthorityFingerprint);
        command.Parameters.AddWithValue("$expires_at", row.ExpiresAt);
        command.Parameters.AddWithValue("$selected_count", row.SelectedCount);
        command.Parameters.AddWithValue("$exclusion_count", row.ExclusionCount);
        command.Parameters.AddWithValue("$no_suggestion_count", row.NoSuggestionCount);
        command.Parameters.AddWithValue("$conflict_count", row.ConflictCount);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyApplyPreviewItemRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO apply_preview_item (
                preview_id, ordinal, outcome_id, transaction_id, mode, category_id, rule_version_id,
                expected_current_category_id, expected_active_allocation_id,
                expected_transaction_revision, expected_relationship_revision, expected_allocation_revision,
                correction_reason
            ) VALUES (
                $preview_id, $ordinal, $outcome_id, $transaction_id, $mode, $category_id, $rule_version_id,
                $expected_current_category_id, $expected_active_allocation_id,
                $expected_transaction_revision, $expected_relationship_revision, $expected_allocation_revision,
                $correction_reason
            );
            """;
        command.Parameters.AddWithValue("$preview_id", row.PreviewId);
        command.Parameters.AddWithValue("$ordinal", row.Ordinal);
        command.Parameters.AddWithValue("$outcome_id", row.OutcomeId);
        command.Parameters.AddWithValue("$transaction_id", row.TransactionId);
        command.Parameters.AddWithValue("$mode", row.Mode);
        command.Parameters.AddWithValue("$category_id", row.CategoryId);
        command.Parameters.AddWithValue("$rule_version_id", (object?)row.RuleVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expected_current_category_id", (object?)row.ExpectedCurrentCategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expected_active_allocation_id", (object?)row.ExpectedActiveAllocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expected_transaction_revision", row.ExpectedTransactionRevision);
        command.Parameters.AddWithValue("$expected_relationship_revision", row.ExpectedRelationshipRevision);
        command.Parameters.AddWithValue("$expected_allocation_revision", row.ExpectedAllocationRevision);
        command.Parameters.AddWithValue("$correction_reason", (object?)row.CorrectionReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ClassifyApplyPreviewRow?> GetPreviewAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string previewId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT preview_id, operation_idempotency_key, evaluation_id, evaluation_fingerprint, selection_mode,
                   selection_hash, ledger_contract_version, projection_version, store_generation_fingerprint,
                   preflight_snapshot_id, preflight_expires_at, category_lifecycle_fingerprint,
                   target_category_fingerprint, rule_authority_fingerprint, expires_at,
                   selected_count, exclusion_count, no_suggestion_count, conflict_count, actor, created_at
            FROM apply_preview
            WHERE preview_id = $id;
            """;
        command.Parameters.AddWithValue("$id", previewId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClassifyApplyPreviewRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetString(19),
            reader.GetString(20));
    }

    public async Task<IReadOnlyList<ClassifyApplyPreviewItemRow>> ListItemsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string previewId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT preview_id, ordinal, outcome_id, transaction_id, mode, category_id, rule_version_id,
                   expected_current_category_id, expected_active_allocation_id,
                   expected_transaction_revision, expected_relationship_revision, expected_allocation_revision,
                   correction_reason
            FROM apply_preview_item
            WHERE preview_id = $id
            ORDER BY ordinal ASC, transaction_id ASC;
            """;
        command.Parameters.AddWithValue("$id", previewId);
        var rows = new List<ClassifyApplyPreviewItemRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClassifyApplyPreviewItemRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return rows;
    }

    public async Task<long> CountPreviewsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM apply_preview;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<long> CountItemsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM apply_preview_item;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}
