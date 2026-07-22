using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Features.Ledger.Recovery;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Features.Ledger.Recovery;

[SupportedOSPlatform("linux")]
public sealed class BackupTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-backup-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private readonly SafeActor actor = new("human", "backup-test");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private BackupService service = null!;
    private BackupOperationModule module = null!;
    private string backupRoot = null!;

    [Fact]
    public void DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_exposes_create_descriptor()
    {
        var descriptor = Assert.Single(module.Descriptors, item => item.OperationId == BackupOperationModule.CreateOperationId);

        Assert.Equal("tally ledger backup create", descriptor.CliPath);
        Assert.Equal("mutation", descriptor.Kind);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(CreateBackupInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(BackupReceipt), descriptor.ResultTypeInfo.Type);
        Assert.Equal("BackupOperationModule.Create", descriptor.HandlerTarget);
    }

    [Fact]
    public void DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_exposes_verify_descriptor()
    {
        var descriptor = Assert.Single(module.Descriptors, item => item.OperationId == BackupOperationModule.VerifyOperationId);

        Assert.Equal("tally ledger backup verify", descriptor.CliPath);
        Assert.Equal("query", descriptor.Kind);
        Assert.False(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(VerifyBackupInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(BackupReceipt), descriptor.ResultTypeInfo.Type);
        Assert.Equal("BackupOperationModule.Verify", descriptor.HandlerTarget);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_create_publishes_one_owner_only_artifact()
    {
        var target = Target();

        var receipt = await Create(target);

        Assert.True(File.Exists(target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        Assert.Equal(Path.GetFileName(target), receipt.ArtifactName);
        Assert.Equal(await Checksum(target), receipt.ArtifactChecksum);
        Assert.Equal(new FileInfo(target).Length, receipt.ArtifactLength);
        Assert.Equal(["ledger.db", "manifest.json"], ArchiveEntries(target));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_create_keeps_live_financial_state_unchanged()
    {
        var account = await SeedAccount();
        await SeedTransaction(account, -12345, "PRIVATE-DESCRIPTION");
        var beforeTransactions = await Count(database, "transaction_fact");

        await Create(Target());

        Assert.Equal(beforeTransactions, await Count(database, "transaction_fact"));
        Assert.Equal(1, await Count(database, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_manifest_contains_complete_counts_totals_and_fingerprints()
    {
        var account = await SeedAccount();
        await SeedTransaction(account, -12345, "Transaction");

        var receipt = await Create(Target());

        Assert.Equal(BackupManifest.CurrentFormatVersion, receipt.Manifest.FormatVersion);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, receipt.Manifest.SchemaVersion);
        Assert.Equal(DurableLedgerVerifier.StorageContractVersion, receipt.Manifest.StorageContractVersion);
        Assert.Equal(31, receipt.Manifest.Types.Count);
        Assert.Equal(5, receipt.Manifest.Actuals.Count);
        Assert.Equal(12345, Assert.Single(receipt.Manifest.Actuals, item => item.Grouping == "none").BudgetActualMinor);
        Assert.NotEmpty(receipt.Manifest.ReconciliationPolicyVersions);
        Assert.All(
            [receipt.Manifest.CategoryHierarchyFingerprint, receipt.Manifest.TransactionReplacementFingerprint,
                receipt.Manifest.RelationshipFingerprint, receipt.Manifest.ReconciliationFingerprint,
                receipt.Manifest.IdempotencyFingerprint, receipt.Manifest.NormalizedFingerprint],
            Assert.NotEmpty);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_verify_recomputes_the_same_complete_report()
    {
        var target = Target();
        var created = await Create(target);

        var verified = await Verify(target, created.ArtifactChecksum);

        AssertSameReceipt(created, verified);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_identical_replay_is_stable_and_does_not_duplicate()
    {
        var target = Target();
        var first = await Create(target, "replay");

        var replay = await Create(target, "replay");

        AssertSameReceipt(first, replay);
        Assert.Single(Directory.EnumerateFiles(backupRoot));
        Assert.Equal(1, await Count(database, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_replay_returns_the_same_artifact_effect()
    {
        var target = Target();
        var first = await Create(target, "first-key");

        var replay = await Create(target, "second-key");

        AssertSameReceipt(first, replay);
        Assert.Equal(1, await Count(database, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
        Assert.Equal(1, await Count(database, "logical_effect", "effect_type = 'backup_artifact'"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_uncommitted_exact_artifact_is_reconciled_after_restart()
    {
        var target = Target();
        var first = await Create(target, "before-crash");
        var restarted = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "restarted"), CancellationToken.None);
        var restartedService = ServiceFor(restarted);

        var result = await restartedService.CreateAsync(new(target), actor, "after-crash", CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        AssertSameReceipt(first, result.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)!);
        Assert.Equal(1, await Count(restarted, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_existing_artifact_from_different_state_is_not_reconciled()
    {
        var target = Target();
        await Create(target, "original");
        var other = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "different"), CancellationToken.None);
        await SeedRawAccountIn(other, "9999");
        var otherService = ServiceFor(other);

        var result = await otherService.CreateAsync(new(target), actor, "different", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.TargetExists, result.ErrorCode);
        Assert.Equal(0, await Count(other, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_replay_conflicts_before_creating_another_artifact()
    {
        await Create(Target("first"), "changed");
        var second = Target("second");

        var result = await service.CreateAsync(new(second), actor, "changed", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, result.ErrorCode);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_existing_unrelated_target_is_rejected_without_overwrite()
    {
        var target = Target();
        await File.WriteAllTextAsync(target, "existing-content");
        protection.ProtectArtifact(target);
        var before = await Checksum(target);

        var result = await service.CreateAsync(new(target), actor, "existing", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.TargetExists, result.ErrorCode);
        Assert.Equal(before, await Checksum(target));
        Assert.Equal(0, await Count(database, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_expected_checksum_mismatch_is_rejected_read_only()
    {
        var target = Target();
        await Create(target);
        var before = await Checksum(target);

        var result = await service.VerifyAsync(new(target, new string('0', 64)), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.ChecksumMismatch, result.ErrorCode);
        Assert.Equal(before, await Checksum(target));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_corrupt_archive_is_rejected_read_only()
    {
        var target = Target();
        await Create(target);
        await using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0xff);
            stream.Flush(true);
        }
        var before = await Checksum(target);

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
        Assert.Equal(before, await Checksum(target));
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_unsafe_artifact_permissions_are_rejected()
    {
        var target = Target();
        await Create(target);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.HostProtection, result.ErrorCode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_unsupported_embedded_schema_is_rejected()
    {
        var target = Target();
        await Create(target);
        await RewriteDatabase(target, connection => Execute(connection, $"PRAGMA user_version = {CompleteLedgerSchema.CurrentVersion + 1};"));

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Incompatible, result.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_cyclic_category_artifact_is_rejected()
    {
        var target = Target();
        await Create(target);
        await RewriteDatabase(target, async connection =>
        {
            var first = LedgerId.New().ToString();
            var second = LedgerId.New().ToString();
            await Execute(connection, "DROP TRIGGER category_parent_cycle_before_insert; DROP TRIGGER category_parent_requires_active_nodes_before_insert;");
            await Execute(connection, "INSERT INTO spend_category VALUES ($first, $at), ($second, $at);", ("$first", first), ("$second", second), ("$at", At));
            await Execute(connection, "INSERT INTO category_parent_event VALUES ($event1, $first, $second, 'initialize', 'bad', 'test', $at, NULL), ($event2, $second, $first, 'initialize', 'bad', 'test', $at, NULL);", ("$event1", LedgerId.New().ToString()), ("$event2", LedgerId.New().ToString()), ("$first", first), ("$second", second), ("$at", At));
        });

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_query_snapshots_are_absent_from_the_backup()
    {
        await Execute(database, "INSERT INTO query_snapshot VALUES ('snapshot', '1.0', 'filter', 'generation', 'hierarchy', 'ephemeral', $at, $expires, 0, 0, 0);", ("$at", At), ("$expires", "2026-07-22T00:15:00Z"));
        var target = Target();

        var receipt = await Create(target);
        var snapshotCounts = await ReadExtractedDatabase(target, async connection => new[]
        {
            await Scalar(connection, "SELECT COUNT(*) FROM query_snapshot;"),
            await Scalar(connection, "SELECT COUNT(*) FROM query_snapshot_group;"),
            await Scalar(connection, "SELECT COUNT(*) FROM query_snapshot_item;"),
            await Scalar(connection, "SELECT COUNT(*) FROM query_snapshot_payload;")
        });

        Assert.All(snapshotCounts, count => Assert.Equal(0, count));
        Assert.DoesNotContain(receipt.Manifest.Types, type => type.Name.StartsWith("query_snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_invalid_source_is_not_published_and_diagnostics_are_private()
    {
        var account = await SeedAccount();
        await SeedTransaction(account, -1000, "PRIVATE-DESCRIPTION-CANARY");
        await Execute(database, "DROP TRIGGER pool_assignment_is_immutable_before_delete; DELETE FROM pool_assignment_event;");
        var target = Target();

        var result = await service.CreateAsync(new(target), actor, "invalid-source", CancellationToken.None);
        var diagnostic = result.ToString();

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
        Assert.False(File.Exists(target));
        Assert.DoesNotContain("PRIVATE-DESCRIPTION-CANARY", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("-1000", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_incomplete_statement_correction_artifact_is_rejected()
    {
        var target = Target();
        await Create(target);
        await RewriteDatabase(target, async connection =>
        {
            var account = await SeedRawAccount(connection, "1111");
            var prior = await SeedRawTransaction(connection, account, -1000);
            var active = await SeedRawTransaction(connection, account, -1000);
            var evidence = LedgerId.New().ToString();
            var decision = LedgerId.New().ToString();
            await Execute(connection, "INSERT INTO evidence_record VALUES ($evidence, 'statement_row', $digest, NULL, NULL, 'test', $at); INSERT INTO reconciliation_decision VALUES ($decision, $evidence, $active, 'owner_confirmed', $policy, $version, 'basis', 0, 'reason', 'test', $at, NULL); INSERT INTO reconciliation_decision_authority VALUES ($decision, 'corrected_from_statement', $prior, $active, 'owner', 'scope:test', 'v2', $at);", ("$evidence", evidence), ("$digest", "digest-" + evidence), ("$decision", decision), ("$active", active), ("$prior", prior), ("$policy", ManualReviewProjectionV1.PolicyId), ("$version", ManualReviewProjectionV1.PolicyVersion), ("$at", At));
        });

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_nonconserving_relationship_artifact_is_rejected()
    {
        var target = Target();
        await Create(target);
        await RewriteDatabase(target, async connection =>
        {
            var sourceAccount = await SeedRawAccount(connection, "1111");
            var targetAccount = await SeedRawAccount(connection, "2222");
            var outflow = await SeedRawTransaction(connection, sourceAccount, -1000);
            var inflow = await SeedRawTransaction(connection, targetAccount, 900);
            await Execute(connection, "INSERT INTO financial_relationship VALUES ($relationship, 'transfer', $outflow, 'transfer_outflow', $inflow, 'transfer_inflow', 1000, 'active', $at, 'test', NULL);", ("$relationship", LedgerId.New().ToString()), ("$outflow", outflow), ("$inflow", inflow), ("$at", At));
        });

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_provider_payload_schema_is_rejected()
    {
        var target = Target();
        await Create(target);
        await RewriteDatabase(target, connection => Execute(connection, "CREATE TABLE mailbox_payload (payload TEXT NOT NULL);"));

        var result = await service.VerifyAsync(new(target), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Integrity, result.ErrorCode);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_a_later_backup_verifies_prior_backup_receipts()
    {
        var first = await Create(Target("first"), "first");

        var second = await Create(Target("second"), "second");
        var verified = await Verify(Target("second"), second.ArtifactChecksum);

        Assert.NotEqual(first.ArtifactChecksum, second.ArtifactChecksum);
        Assert.Equal(1, Assert.Single(verified.Manifest.Types, item => item.Name == "idempotency_record").RowCount);
        Assert.Equal(1, Assert.Single(verified.Manifest.Types, item => item.Name == "logical_effect").RowCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.tally-backup")]
    public async Task DM_LEDGER_RECOVERY_STORAGE_CONTRACTS_invalid_paths_do_not_consume_idempotency(string path)
    {
        var result = await service.CreateAsync(new(path), actor, "invalid", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupErrors.Invalid, result.ErrorCode);
        Assert.Equal(0, await Count(database, "idempotency_record", "operation_id LIKE '%ledger.backup.create'"));
    }

    public async Task InitializeAsync()
    {
        protection.EnsureDataRoot(root);
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "live"), CancellationToken.None);
        backupRoot = Path.Combine(root, "backups");
        protection.EnsureDataRoot(backupRoot);
        factory = new(protection);
        service = ServiceFor(database);
        module = new(service);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private string Target(string name = "ledger") => Path.Combine(backupRoot, name + ".tally-backup");

    private async Task<BackupReceipt> Create(string target, string key = "create")
    {
        var result = await service.CreateAsync(new(target), actor, key, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)!;
    }

    private async Task<BackupReceipt> Verify(string target, string? expectedChecksum = null)
    {
        var result = await service.VerifyAsync(new(target, expectedChecksum), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Deserialize(BackupJsonContext.Default.BackupReceipt)!;
    }

    private async Task<string> SeedAccount()
    {
        var account = LedgerId.New().ToString();
        await Execute(database, "INSERT INTO account VALUES ($id, 'Bank', 'cheque', 'asset', '1111', 'ZAR', $at); INSERT INTO catalogue_lifecycle_event VALUES ($event, 'account', $id, 'create', NULL, 'Account', 'account', NULL, 'test', $at, NULL);", ("$id", account), ("$event", LedgerId.New().ToString()), ("$at", At));
        return account;
    }

    private async Task SeedTransaction(string account, long amount, string description)
    {
        var transaction = LedgerId.New().ToString();
        await Execute(database, "INSERT INTO transaction_fact VALUES ($transaction, $account, $amount, 'ZAR', '2026-07-22', NULL, $description, $at, 'test'); INSERT INTO transaction_attribution_event VALUES ($attribution, $transaction, 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at); INSERT INTO pool_assignment_event VALUES ($pool, $transaction, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);", ("$transaction", transaction), ("$account", account), ("$amount", amount), ("$description", description), ("$attribution", LedgerId.New().ToString()), ("$pool", LedgerId.New().ToString()), ("$at", At));
    }

    private async Task SeedRawAccountIn(LedgerDb target, string maskedSuffix)
    {
        await using var connection = await factory.OpenAsync(target, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await SeedRawAccount(connection, maskedSuffix);
    }

    private BackupService ServiceFor(LedgerDb target) => new(
        new LedgerMutationExecutor(target, factory, new IdempotencyStore()),
        new DurableLedgerVerifier(protection),
        new ArtifactReconciler(),
        protection);

    private static async Task<string> SeedRawAccount(SqliteConnection connection, string maskedSuffix)
    {
        var account = LedgerId.New().ToString();
        var label = "Account " + maskedSuffix;
        await Execute(connection, "INSERT INTO account VALUES ($id, 'Bank', 'cheque', 'asset', $masked, 'ZAR', $at); INSERT INTO catalogue_lifecycle_event VALUES ($event, 'account', $id, 'create', NULL, $label, $normalized, NULL, 'test', $at, NULL);", ("$id", account), ("$masked", maskedSuffix), ("$event", LedgerId.New().ToString()), ("$label", label), ("$normalized", label.ToLowerInvariant()), ("$at", At));
        return account;
    }

    private static async Task<string> SeedRawTransaction(SqliteConnection connection, string account, long amount)
    {
        var transaction = LedgerId.New().ToString();
        await Execute(connection, "INSERT INTO transaction_fact VALUES ($transaction, $account, $amount, 'ZAR', '2026-07-22', NULL, 'Transaction', $at, 'test'); INSERT INTO transaction_attribution_event VALUES ($attribution, $transaction, 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at); INSERT INTO pool_assignment_event VALUES ($pool, $transaction, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);", ("$transaction", transaction), ("$account", account), ("$amount", amount), ("$attribution", LedgerId.New().ToString()), ("$pool", LedgerId.New().ToString()), ("$at", At));
        return transaction;
    }

    private async Task Execute(LedgerDb target, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenAsync(target, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await Execute(connection, sql, parameters);
    }

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(LedgerDb target, string table, string? where = null)
    {
        await using var connection = await factory.OpenAsync(target, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        return await Scalar(connection, $"SELECT COUNT(*) FROM {table}" + (where is null ? ";" : " WHERE " + where + ";"));
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] ArchiveEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task RewriteDatabase(string artifactPath, Func<SqliteConnection, Task> rewrite)
    {
        var directory = Path.Combine(root, "rewrite-" + Guid.NewGuid().ToString("N"));
        protection.EnsureDataRoot(directory);
        var databasePath = Path.Combine(directory, "ledger.db");
        var manifestPath = Path.Combine(directory, "manifest.json");
        using (var archive = ZipFile.OpenRead(artifactPath))
        {
            archive.GetEntry("ledger.db")!.ExtractToFile(databasePath);
            archive.GetEntry("manifest.json")!.ExtractToFile(manifestPath);
        }
        protection.ProtectArtifact(databasePath);
        protection.ProtectArtifact(manifestPath);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await rewrite(connection);
        }
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["databaseChecksum"] = await Checksum(databasePath);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        File.Delete(artifactPath);
        using (var archive = ZipFile.Open(artifactPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(databasePath, "ledger.db", CompressionLevel.NoCompression);
            archive.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.NoCompression);
        }
        protection.ProtectArtifact(artifactPath);
        Directory.Delete(directory, true);
    }

    private async Task<T> ReadExtractedDatabase<T>(string artifactPath, Func<SqliteConnection, Task<T>> read)
    {
        var path = Path.Combine(root, "read-" + Guid.NewGuid().ToString("N") + ".db");
        using (var archive = ZipFile.OpenRead(artifactPath)) archive.GetEntry("ledger.db")!.ExtractToFile(path);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        var result = await read(connection);
        File.Delete(path);
        return result;
    }

    private static async Task<string> Checksum(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static void AssertSameReceipt(BackupReceipt expected, BackupReceipt actual) => Assert.Equal(
        JsonSerializer.Serialize(expected, BackupJsonContext.Default.BackupReceipt),
        JsonSerializer.Serialize(actual, BackupJsonContext.Default.BackupReceipt));
}
