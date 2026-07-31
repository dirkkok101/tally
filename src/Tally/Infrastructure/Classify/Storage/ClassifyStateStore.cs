using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// CLASSIFY-owned raw-SQLite durability boundary: connections, migrations, BEGIN IMMEDIATE writers,
/// and owner-only artifact protection (DD-CLASSIFY-STATE-STORE). Separate from ledger.db.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyStateStore
{
    private readonly HostArtifactProtection artifactProtection;

    public ClassifyStateStore(string dataRoot, HostArtifactProtection? artifactProtection = null)
    {
        Paths = new ClassifyStorePaths(dataRoot);
        this.artifactProtection = artifactProtection ?? new HostArtifactProtection();
    }

    public ClassifyStateStore(ClassifyStorePaths paths, HostArtifactProtection? artifactProtection = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
        this.artifactProtection = artifactProtection ?? new HostArtifactProtection();
    }

    public ClassifyStorePaths Paths { get; }

    public HostArtifactProtection ArtifactProtection => artifactProtection;

    [SupportedOSPlatform("linux")]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ClassifySchema.ApplyAsync(connection, cancellationToken);
        await EnsureStoreMetaAsync(connection, cancellationToken);
        ProtectPersistedArtifacts();
    }

    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        artifactProtection.EnsureDataRoot(Paths.DataRoot);
        artifactProtection.EnsureDataRoot(Paths.ClassifyDirectory);
        artifactProtection.EnsureDataRoot(Paths.TemporaryDirectory);
        artifactProtection.EnsureDataRoot(Paths.ReportsDirectory);
        RejectUnsafeDatabasePath(Paths.DatabasePath);
        if (File.Exists(Paths.DatabasePath))
        {
            artifactProtection.RequireOwnerOnlyArtifact(Paths.DatabasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        try
        {
            await ClassifySchema.ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await ClassifySchema.ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
            await ClassifySchema.ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ClassifySchema.ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
            ProtectPersistedArtifacts();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    public async Task<SqliteConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            await ClassifySchema.ApplyAsync(connection, cancellationToken);
            await EnsureStoreMetaAsync(connection, cancellationToken);
            ProtectPersistedArtifacts();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Begins an exclusive writer transaction (BEGIN IMMEDIATE).</summary>
    public SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction(deferred: false);
    }

    public async Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await using var transaction = BeginImmediate(connection);
        try
        {
            var result = await work(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ProtectPersistedArtifacts();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Guarded lifecycle transition: updates only when the current state matches expectedPriorState.
    /// </summary>
    public async Task<bool> TryTransitionEvaluationLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evaluationId,
        string expectedPriorState,
        string nextState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE evaluation_run
            SET lifecycle_state = $next
            WHERE evaluation_id = $id AND lifecycle_state = $expected;
            """;
        command.Parameters.AddWithValue("$next", nextState);
        command.Parameters.AddWithValue("$id", evaluationId);
        command.Parameters.AddWithValue("$expected", expectedPriorState);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryTransitionApplyRunLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string applyId,
        string expectedPriorState,
        string nextState,
        string? completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE apply_run
            SET lifecycle_state = $next,
                completed_at = $completed_at
            WHERE apply_id = $id AND lifecycle_state = $expected;
            """;
        command.Parameters.AddWithValue("$next", nextState);
        command.Parameters.AddWithValue("$completed_at", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", applyId);
        command.Parameters.AddWithValue("$expected", expectedPriorState);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 1;
    }

    public async Task InsertEvaluationRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyEvaluationRunRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                $evaluation_id, $operation_idempotency_key, $rule_set_version_id, $normalization_version,
                $ledger_contract_version, $projection_version, $store_generation_fingerprint, $snapshot_id,
                $snapshot_expires_at, $category_lifecycle_fingerprint, $ordered_items_fingerprint,
                $input_count, $suggestion_count, $no_suggestion_count, $conflict_count, $stale_count,
                $lifecycle_state, $actor, $created_at
            );
            """;
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$operation_idempotency_key", (object?)row.OperationIdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$rule_set_version_id", row.RuleSetVersionId);
        command.Parameters.AddWithValue("$normalization_version", row.NormalizationVersion);
        command.Parameters.AddWithValue("$ledger_contract_version", row.LedgerContractVersion);
        command.Parameters.AddWithValue("$projection_version", row.ProjectionVersion);
        command.Parameters.AddWithValue("$store_generation_fingerprint", row.StoreGenerationFingerprint);
        command.Parameters.AddWithValue("$snapshot_id", row.SnapshotId);
        command.Parameters.AddWithValue("$snapshot_expires_at", row.SnapshotExpiresAt);
        command.Parameters.AddWithValue("$category_lifecycle_fingerprint", row.CategoryLifecycleFingerprint);
        command.Parameters.AddWithValue("$ordered_items_fingerprint", row.OrderedItemsFingerprint);
        command.Parameters.AddWithValue("$input_count", row.InputCount);
        command.Parameters.AddWithValue("$suggestion_count", row.SuggestionCount);
        command.Parameters.AddWithValue("$no_suggestion_count", row.NoSuggestionCount);
        command.Parameters.AddWithValue("$conflict_count", row.ConflictCount);
        command.Parameters.AddWithValue("$stale_count", row.StaleCount);
        command.Parameters.AddWithValue("$lifecycle_state", row.LifecycleState);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassifyOutcomeRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_outcome (
                outcome_id, evaluation_id, ordinal, transaction_id, outcome_type,
                category_id, item_lifecycle_fingerprint, safe_reason
            ) VALUES (
                $outcome_id, $evaluation_id, $ordinal, $transaction_id, $outcome_type,
                $category_id, $item_lifecycle_fingerprint, $safe_reason
            );
            """;
        command.Parameters.AddWithValue("$outcome_id", row.OutcomeId);
        command.Parameters.AddWithValue("$evaluation_id", row.EvaluationId);
        command.Parameters.AddWithValue("$ordinal", row.Ordinal);
        command.Parameters.AddWithValue("$transaction_id", row.TransactionId);
        command.Parameters.AddWithValue("$outcome_type", row.OutcomeType);
        command.Parameters.AddWithValue("$category_id", (object?)row.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$item_lifecycle_fingerprint", row.ItemLifecycleFingerprint);
        command.Parameters.AddWithValue("$safe_reason", row.SafeReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ClassifyEvaluationRunRow?> GetEvaluationRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evaluationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                   ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                   snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                   input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                   lifecycle_state, actor, created_at
            FROM evaluation_run
            WHERE evaluation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", evaluationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ClassifyRowMapper.MapEvaluationRun(reader) : null;
    }

    public void RequireOwnerOnlyArtifacts()
    {
        artifactProtection.RequireOwnerOnlyDirectory(Paths.DataRoot);
        artifactProtection.RequireOwnerOnlyDirectory(Paths.ClassifyDirectory);
        foreach (var artifact in Paths.RecognizedArtifactPaths().Where(File.Exists))
        {
            artifactProtection.RequireOwnerOnlyArtifact(artifact);
        }
    }

    private async Task EnsureStoreMetaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM classify_store_meta;";
        var count = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count > 0)
        {
            return;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO classify_store_meta (schema_version, store_id, created_at)
            VALUES ($schema_version, $store_id, $created_at);
            """;
        insert.Parameters.AddWithValue("$schema_version", ClassifySchema.CurrentVersion);
        insert.Parameters.AddWithValue("$store_id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue(
            "$created_at",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void RejectUnsafeDatabasePath(string databasePath)
    {
        if (File.Exists(databasePath) || Directory.Exists(databasePath))
        {
            try
            {
                if (File.ResolveLinkTarget(databasePath, returnFinalTarget: false) is not null)
                {
                    throw new InvalidOperationException(
                        "The classify database path must not be a symbolic link.");
                }
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "The classify database path is not a safe regular file.", ex);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private void ProtectPersistedArtifacts()
    {
        if (File.Exists(Paths.DatabasePath))
        {
            artifactProtection.ProtectArtifact(Paths.DatabasePath);
        }

        foreach (var artifact in Paths.RecognizedArtifactPaths().Where(path => path != Paths.DatabasePath && File.Exists(path)))
        {
            artifactProtection.ProtectArtifact(artifact);
        }
    }
}
