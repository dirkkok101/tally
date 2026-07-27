using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Manifests;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class ReviewStateStore(IngestDatabase database)
{
    public sealed record StoredManifest(
        string BatchId,
        string ManifestRevisionId,
        string CanonicalDigest,
        bool Committable,
        string SelectedAccountId,
        string AdapterIdentity,
        string LedgerContractVersion,
        string ManifestSchemaVersion,
        BatchStatus BatchStatus,
        IReadOnlyList<SourceRecordOutcome> Outcomes,
        IReadOnlyList<ImportCandidate> Candidates,
        IReadOnlyList<ReconciliationControl> Controls,
        ManifestApprovalState Approval);

    private async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenAsync(cancellationToken);
        await new IngestSchemaMigrator().ApplyAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<StoredManifest?> LoadAsync(
        string batchId,
        string manifestRevisionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        const string batchSql = """
            SELECT batch_id, selected_account_id, adapter_identity, ledger_contract_version,
                   manifest_schema_version, status
            FROM ingest_batch
            WHERE batch_id = $batchId;
            """;
        await using var batchCommand = connection.CreateCommand();
        batchCommand.CommandText = batchSql;
        batchCommand.Parameters.AddWithValue("$batchId", batchId);
        await using var batchReader = await batchCommand.ExecuteReaderAsync(cancellationToken);
        if (!await batchReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var selectedAccountId = batchReader.GetString(1);
        var adapterIdentity = batchReader.GetString(2);
        var ledgerVersion = batchReader.GetString(3);
        var schemaVersion = batchReader.GetString(4);
        var status = (BatchStatus)batchReader.GetInt32(5);
        await batchReader.DisposeAsync();

        const string revisionSql = """
            SELECT manifest_revision_id, canonical_digest, committable
            FROM manifest_revision
            WHERE batch_id = $batchId AND manifest_revision_id = $revisionId;
            """;
        await using var revisionCommand = connection.CreateCommand();
        revisionCommand.CommandText = revisionSql;
        revisionCommand.Parameters.AddWithValue("$batchId", batchId);
        revisionCommand.Parameters.AddWithValue("$revisionId", manifestRevisionId);
        await using var revisionReader = await revisionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await revisionReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var digest = revisionReader.GetString(1);
        var committable = revisionReader.GetInt32(2) == 1;
        await revisionReader.DisposeAsync();

        var outcomes = await LoadOutcomesAsync(connection, manifestRevisionId, cancellationToken);
        var candidates = await LoadCandidatesAsync(connection, manifestRevisionId, cancellationToken);
        var controls = await LoadControlsAsync(connection, manifestRevisionId, cancellationToken);
        var approval = await LoadApprovalAsync(connection, manifestRevisionId, cancellationToken);

        return new StoredManifest(
            batchId,
            manifestRevisionId,
            digest,
            committable,
            selectedAccountId,
            adapterIdentity,
            ledgerVersion,
            schemaVersion,
            status,
            outcomes,
            candidates,
            controls,
            approval);
    }

    public async Task<string?> ApproveAsync(
        string batchId,
        string manifestRevisionId,
        string expectedDigest,
        SafeActor actor,
        string approvedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string loadSql = """
                SELECT m.canonical_digest, m.committable, b.status
                FROM manifest_revision m
                JOIN ingest_batch b ON b.batch_id = m.batch_id
                WHERE m.batch_id = $batchId AND m.manifest_revision_id = $revisionId;
                """;
            await using var load = connection.CreateCommand();
            load.Transaction = transaction;
            load.CommandText = loadSql;
            load.Parameters.AddWithValue("$batchId", batchId);
            load.Parameters.AddWithValue("$revisionId", manifestRevisionId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var digest = reader.GetString(0);
            var committable = reader.GetInt32(1) == 1;
            var status = (BatchStatus)reader.GetInt32(2);
            await reader.DisposeAsync();

            if (!committable ||
                status is not (BatchStatus.Previewed or BatchStatus.Approved) ||
                !string.Equals(digest, expectedDigest, StringComparison.Ordinal))
            {
                return "reject";
            }

            // Deactivate prior approvals for this revision.
            const string deactivateSql = """
                UPDATE manifest_approval
                SET active = 0
                WHERE manifest_revision_id = $revisionId AND active = 1;
                """;
            await using (var deactivate = connection.CreateCommand())
            {
                deactivate.Transaction = transaction;
                deactivate.CommandText = deactivateSql;
                deactivate.Parameters.AddWithValue("$revisionId", manifestRevisionId);
                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }

            var approvalId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            const string insertSql = """
                INSERT INTO manifest_approval (
                    approval_id, manifest_revision_id, manifest_digest, actor, trusted_os_identity, approved_at, active)
                VALUES ($id, $revisionId, $digest, $actor, $osIdentity, $approvedAt, 1);
                """;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = insertSql;
                insert.Parameters.AddWithValue("$id", approvalId);
                insert.Parameters.AddWithValue("$revisionId", manifestRevisionId);
                insert.Parameters.AddWithValue("$digest", expectedDigest);
                insert.Parameters.AddWithValue("$actor", actor.Label);
                insert.Parameters.AddWithValue("$osIdentity", actor.Kind);
                insert.Parameters.AddWithValue("$approvedAt", approvedAt);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            const string statusSql = """
                UPDATE ingest_batch
                SET status = $status, updated_at = $updatedAt
                WHERE batch_id = $batchId;
                """;
            await using (var statusCommand = connection.CreateCommand())
            {
                statusCommand.Transaction = transaction;
                statusCommand.CommandText = statusSql;
                statusCommand.Parameters.AddWithValue("$status", (int)BatchStatus.Approved);
                statusCommand.Parameters.AddWithValue("$updatedAt", approvedAt);
                statusCommand.Parameters.AddWithValue("$batchId", batchId);
                await statusCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return approvalId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<IReadOnlyList<SourceRecordOutcome>> LoadOutcomesAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_record_id, record_order, disposition, reason_code, candidate_id, prior_canonical_ref
            FROM source_record_outcome
            WHERE manifest_revision_id = $id
            ORDER BY record_order;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SourceRecordOutcome>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SourceRecordOutcome(
                revisionId,
                reader.GetString(0),
                reader.GetInt32(1),
                (SourceRecordDisposition)reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ImportCandidate>> LoadCandidatesAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT immutable_facts_json
            FROM import_candidate
            WHERE manifest_revision_id = $id
            ORDER BY candidate_id;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ImportCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(0);
            var candidate = JsonSerializer.Deserialize(json, IngestJsonContext.Default.ImportCandidate);
            if (candidate is not null)
            {
                rows.Add(candidate);
            }
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ReconciliationControl>> LoadControlsAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT evidence_json
            FROM reconciliation_control
            WHERE manifest_revision_id = $id
            ORDER BY control_order;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ReconciliationControl>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var control = JsonSerializer.Deserialize(reader.GetString(0), IngestJsonContext.Default.ReconciliationControl);
            if (control is not null)
            {
                rows.Add(control);
            }
        }

        return rows;
    }

    private static async Task<ManifestApprovalState> LoadApprovalAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT approval_id, approved_at
            FROM manifest_approval
            WHERE manifest_revision_id = $id AND active = 1
            ORDER BY approved_at DESC
            LIMIT 1;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ManifestApprovalState(false, null, null);
        }

        return new ManifestApprovalState(true, reader.GetString(0), reader.GetString(1));
    }
}
