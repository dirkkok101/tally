using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Recovery;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Infrastructure.Recovery;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-ATTRIBUTABLE-HISTORY-INVARIANTS and TC-LEDGER-VERIFIED-RECOVERY-DRILL.
public sealed class DurableLedgerVerifierTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-durable-verifier-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();

    [Fact]
    public async Task Empty_current_schema_candidate_verifies_every_durable_type()
    {
        var database = await Candidate();

        var result = await Verify(database);

        Assert.True(result.IsVerified, result.ErrorCode);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, result.Report!.SchemaVersion);
        Assert.Equal(31, result.Report.Types.Count);
        Assert.DoesNotContain(result.Report.Types, type => type.Name.StartsWith("query_snapshot", StringComparison.Ordinal));
        Assert.All(result.Report.Types, type => Assert.NotEmpty(type.Fingerprint));
        Assert.NotEmpty(result.Report.NormalizedFingerprint);
    }

    [Fact]
    public async Task Verification_is_read_only_and_preserves_artifact_checksum()
    {
        var database = await Candidate();
        await SeedTransaction(database, await SeedAccount(database), -1000);
        var before = await Checksum(database.DatabasePath);

        var result = await Verify(database);

        Assert.True(result.IsVerified, result.ErrorCode);
        Assert.Equal(before, await Checksum(database.DatabasePath));
        Assert.Equal(before, result.Report!.Artifacts.Single(item => item.Name == "ledger.db").Checksum);
    }

    [Fact]
    public async Task Exact_actuals_are_reconciled_from_one_current_projection()
    {
        var database = await Candidate();
        await SeedTransaction(database, await SeedAccount(database), -12345);

        var result = await Verify(database);

        Assert.True(result.IsVerified, result.ErrorCode);
        var all = Assert.Single(result.Report!.Actuals, item => item.Grouping == "none");
        Assert.Equal(1, all.MemberCount);
        Assert.Equal(-12345, all.NetAccountMovementMinor);
        Assert.Equal(12345, all.ExternalSpendMinor);
        Assert.Equal(12345, all.BudgetActualMinor);
        Assert.Equal(5, result.Report.Actuals.Count);
    }

    [Fact]
    public async Task Ephemeral_query_snapshots_do_not_change_normalized_durable_state()
    {
        var database = await Candidate();
        var before = (await Verify(database)).Report!;
        await Execute(database, """
            INSERT INTO query_snapshot VALUES (
                'snapshot', '1.0', 'filter', 'generation', 'hierarchy', 'ephemeral',
                '2026-07-22T00:00:00Z', '2026-07-22T00:15:00Z', 0, 0, 0);
            """);

        var after = (await Verify(database)).Report!;

        Assert.Equal(before.NormalizedFingerprint, after.NormalizedFingerprint);
        Assert.Equal(before.Types, after.Types);
        Assert.DoesNotContain(after.Types, type => type.Name.StartsWith("query_snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logical_clone_has_the_same_normalized_report_apart_from_artifact_metadata()
    {
        var source = await Candidate("source");
        await SeedTransaction(source, await SeedAccount(source), -2500);
        var target = await Candidate("target");
        await CopyDatabase(source, target);

        var sourceReport = (await Verify(source)).Report!;
        var targetReport = (await Verify(target)).Report!;

        Assert.Equal(sourceReport.NormalizedFingerprint, targetReport.NormalizedFingerprint);
        Assert.Equal(sourceReport.Types, targetReport.Types);
        Assert.Equal(sourceReport.Actuals, targetReport.Actuals);
    }

    [Fact]
    public async Task Live_current_generation_is_rejected_before_inspection()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(Path.Combine(root, "live"), CancellationToken.None);

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.LiveStore, result.ErrorCode);
        Assert.Null(result.Report);
    }

    [Fact]
    public async Task Expected_checksum_mismatch_is_rejected()
    {
        var database = await Candidate();

        var result = await Verifier().VerifyAsync(database, "wrong-checksum", CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.ChecksumMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task Unsafe_database_permissions_are_rejected()
    {
        var database = await Candidate();
        File.SetUnixFileMode(database.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.HostProtection, result.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_schema_version_is_rejected()
    {
        var database = await Candidate();
        await Execute(database, "PRAGMA user_version = 1;");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.SchemaIncompatible, result.ErrorCode);
    }

    [Fact]
    public async Task Missing_migration_fragment_is_rejected()
    {
        var database = await Candidate();
        await Execute(database, "DELETE FROM migration_metadata WHERE fragment_name = 'v001_transaction';");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.SchemaIncompatible, result.ErrorCode);
        Assert.Equal("migration_metadata", result.SafeType);
    }

    [Fact]
    public async Task Missing_durable_table_is_rejected()
    {
        var database = await Candidate();
        await Execute(database, "DROP TABLE artifact_manifest;");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.SchemaIncompatible, result.ErrorCode);
    }

    [Fact]
    public async Task Provider_payload_table_is_rejected_as_a_schema_privacy_violation()
    {
        var database = await Candidate();
        await Execute(database, "CREATE TABLE mailbox_payload (payload TEXT NOT NULL);");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.PrivacyViolation, result.ErrorCode);
        Assert.Equal("schema", result.SafeType);
    }

    [Fact]
    public async Task Category_cycle_is_rejected_even_when_write_guards_were_bypassed()
    {
        var database = await Candidate();
        var account = await SeedAccount(database);
        var rootCategory = LedgerId.New().ToString();
        var childCategory = LedgerId.New().ToString();
        var rootParentEvent = await SeedCategory(database, rootCategory, null, "Root");
        _ = await SeedCategory(database, childCategory, rootCategory, "Child");
        await Execute(database, "DROP TRIGGER category_parent_cycle_before_insert;");
        await Execute(database, """
            INSERT INTO category_parent_event VALUES ($event, $category, $parent, 'reparent', 'corruption', 'test', $at, $previous);
            """, ("$event", LedgerId.New().ToString()), ("$category", rootCategory), ("$parent", childCategory), ("$at", At), ("$previous", rootParentEvent));
        _ = account;

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("category_hierarchy", result.SafeType);
    }

    [Fact]
    public async Task Catalogue_lifecycle_for_a_missing_entity_is_rejected()
    {
        var database = await Candidate();
        await Execute(database, "DROP TRIGGER catalogue_lifecycle_entity_exists_before_insert;");
        await Execute(database, """
            INSERT INTO catalogue_lifecycle_event VALUES (
                $event, 'account', $missing, 'create', NULL, 'Missing', 'missing', NULL, 'test', $at, NULL);
            """, ("$event", LedgerId.New().ToString()), ("$missing", LedgerId.New().ToString()), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("catalogue_lifecycle", result.SafeType);
    }

    [Fact]
    public async Task Transaction_without_current_pool_assignment_is_rejected()
    {
        var database = await Candidate();
        await SeedTransaction(database, await SeedAccount(database), -1000);
        await Execute(database, "DROP TRIGGER pool_assignment_is_immutable_before_delete; DELETE FROM pool_assignment_event;");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("pool_assignment", result.SafeType);
    }

    [Fact]
    public async Task Transaction_without_current_payment_attribution_is_rejected()
    {
        var database = await Candidate();
        await SeedTransaction(database, await SeedAccount(database), -1000);
        await Execute(database, "DROP TRIGGER transaction_attribution_is_immutable_before_delete; DELETE FROM transaction_attribution_event;");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("payment_attribution", result.SafeType);
    }

    [Fact]
    public async Task Transaction_replacement_cycle_is_rejected()
    {
        var database = await Candidate();
        var account = await SeedAccount(database);
        var first = await SeedTransaction(database, account, -1000);
        var second = await SeedTransaction(database, account, -1000);
        await Execute(database, "DROP TRIGGER transaction_lifecycle_replacement_must_be_active_before_insert;");
        await Execute(database, """
            INSERT INTO transaction_lifecycle_event VALUES ($firstEvent, $first, 'superseded', $second, NULL, 'cycle', 'test', $at);
            INSERT INTO transaction_lifecycle_event VALUES ($secondEvent, $second, 'superseded', $first, NULL, 'cycle', 'test', $at);
            """, ("$firstEvent", LedgerId.New().ToString()), ("$first", first), ("$second", second), ("$secondEvent", LedgerId.New().ToString()), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("transaction_replacement", result.SafeType);
    }

    [Fact]
    public async Task Transfer_with_nonconserving_principal_is_rejected()
    {
        var database = await Candidate();
        var outflow = await SeedTransaction(database, await SeedAccount(database), -1000);
        var inflow = await SeedTransaction(database, await SeedAccount(database), 900);
        await SeedRelationship(database, "transfer", outflow, inflow, 1000);

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("financial_relationship", result.SafeType);
    }

    [Fact]
    public async Task Duplicate_active_relationship_role_is_rejected()
    {
        var database = await Candidate();
        var outflow = await SeedTransaction(database, await SeedAccount(database), -1000);
        var firstInflow = await SeedTransaction(database, await SeedAccount(database), 1000);
        var secondInflow = await SeedTransaction(database, await SeedAccount(database), 1000);
        await SeedRelationship(database, "transfer", outflow, firstInflow, 1000);
        await Execute(database, "DROP TRIGGER financial_relationship_roles_are_exclusive_before_insert;");
        await SeedRelationship(database, "transfer", outflow, secondInflow, 1000);

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("relationship_cardinality", result.SafeType);
    }

    [Fact]
    public async Task Relationship_replacement_cycle_is_rejected()
    {
        var database = await Candidate();
        var first = await SeedRelationship(
            database,
            "transfer",
            await SeedTransaction(database, await SeedAccount(database), -1000),
            await SeedTransaction(database, await SeedAccount(database), 1000),
            1000);
        var second = await SeedRelationship(
            database,
            "transfer",
            await SeedTransaction(database, await SeedAccount(database), -1000),
            await SeedTransaction(database, await SeedAccount(database), 1000),
            1000);
        await Execute(database, """
            INSERT INTO relationship_lifecycle_event VALUES ($firstEvent, $first, 'replaced', $second, NULL, 'cycle', 'test', $at);
            INSERT INTO relationship_lifecycle_event VALUES ($secondEvent, $second, 'replaced', $first, NULL, 'cycle', 'test', $at);
            """, ("$firstEvent", LedgerId.New().ToString()), ("$first", first), ("$second", second), ("$secondEvent", LedgerId.New().ToString()), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("relationship_replacement", result.SafeType);
    }

    [Fact]
    public async Task Unsupported_reconciliation_policy_is_rejected()
    {
        var database = await Candidate();
        var transaction = await SeedTransaction(database, await SeedAccount(database), -1000);
        await SeedDecision(database, transaction, "statement_row", "unsupported", "9.0", true, "confirmed_existing");

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.PolicyIncompatible, result.ErrorCode);
        Assert.Equal("reconciliation_policy", result.SafeType);
    }

    [Fact]
    public async Task Confirming_link_from_non_statement_evidence_is_rejected()
    {
        var database = await Candidate();
        var transaction = await SeedTransaction(database, await SeedAccount(database), -1000);
        var decision = await SeedDecision(database, transaction, "owner_assertion", ManualReviewProjectionV1.PolicyId, ManualReviewProjectionV1.PolicyVersion, false, "owner_confirmed_match");
        await Execute(database, "DROP TRIGGER confirming_link_requires_statement_evidence;");
        await Execute(database, """
            INSERT INTO evidence_link_event VALUES ($link, $evidence, $transaction, 'confirming', 'link', $decision, 'bad link', 'test', $at, NULL);
            """, ("$link", LedgerId.New().ToString()), ("$evidence", decision.EvidenceId), ("$transaction", transaction), ("$decision", decision.DecisionId), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("evidence_link", result.SafeType);
    }

    [Fact]
    public async Task Duplicate_active_confirming_links_are_rejected()
    {
        var database = await Candidate();
        var transaction = await SeedTransaction(database, await SeedAccount(database), -1000);
        var decision = await SeedDecision(
            database,
            transaction,
            "statement_row",
            ReconciliationPolicyV1.PolicyId,
            ReconciliationPolicyV1.PolicyVersion,
            true,
            "confirmed_existing");
        await Execute(database, "DROP INDEX ux_confirming_link_root_per_evidence;");
        await Execute(database, """
            INSERT INTO evidence_link_event VALUES ($first, $evidence, $transaction, 'confirming', 'link', $decision, 'first', 'test', $at, NULL);
            INSERT INTO evidence_link_event VALUES ($second, $evidence, $transaction, 'confirming', 'link', $decision, 'second', 'test', $at, NULL);
            """, ("$first", LedgerId.New().ToString()), ("$second", LedgerId.New().ToString()),
            ("$evidence", decision.EvidenceId), ("$transaction", transaction), ("$decision", decision.DecisionId), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("evidence_link", result.SafeType);
    }

    [Fact]
    public async Task Corrected_statement_authority_without_complete_correction_is_rejected()
    {
        var database = await Candidate();
        var account = await SeedAccount(database);
        var prior = await SeedTransaction(database, account, -1000);
        var active = await SeedTransaction(database, account, -1000);
        var decisionId = LedgerId.New().ToString();
        var evidenceId = await SeedEvidence(database, "statement_row");
        await Execute(database, """
            INSERT INTO reconciliation_decision VALUES ($decision, $evidence, $active, 'owner_confirmed', $policy, $version, 'owner correction', 0, 'correction', 'test', $at, NULL);
            INSERT INTO reconciliation_decision_authority VALUES ($decision, 'corrected_from_statement', $prior, $active, 'owner', 'scope:test', 'v2', $at);
            """, ("$decision", decisionId), ("$evidence", evidenceId), ("$active", active), ("$prior", prior), ("$policy", ManualReviewProjectionV1.PolicyId), ("$version", ManualReviewProjectionV1.PolicyVersion), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("statement_correction", result.SafeType);
    }

    [Fact]
    public async Task Logical_effect_operation_mismatch_is_rejected()
    {
        var database = await Candidate();
        await Execute(database, """
            INSERT INTO idempotency_record VALUES ('key', '1.0' || char(10) || 'ledger.transfer.confirm', $hash, 'test', 'committed', '{"value":{},"errorCode":null}', $at);
            INSERT INTO logical_effect VALUES ('refund:one:two', 'refund_confirmation', 'key', $at);
            """, ("$hash", new string('a', 64)), ("$at", At));

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("idempotency", result.SafeType);
    }

    [Fact]
    public async Task Valid_committed_logical_effect_replay_state_is_accepted()
    {
        var database = await Candidate();
        var account = await SeedAccount(database);
        var original = await SeedTransaction(database, account, -1000);
        var refund = await SeedTransaction(database, account, 1000);
        await SeedRelationship(database, "refund", original, refund, 1000);
        await SeedIdempotency(database, "ledger.refund.confirm", "refund_confirmation", $"refund:{original}:{refund}");

        var result = await Verify(database);

        Assert.True(result.IsVerified, result.ErrorCode);
    }

    [Fact]
    public async Task Required_logical_effect_missing_from_committed_replay_state_is_rejected()
    {
        var database = await Candidate();
        await SeedIdempotency(database, "ledger.refund.confirm", null, null);

        var result = await Verify(database);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("idempotency", result.SafeType);
    }

    [Fact]
    public async Task Failure_diagnostics_never_echo_financial_or_evidence_payloads()
    {
        var database = await Candidate();
        await SeedTransaction(database, await SeedAccount(database), -1000, "PRIVATE-DESCRIPTION-CANARY");
        await Execute(database, "DROP TRIGGER pool_assignment_is_immutable_before_delete; DELETE FROM pool_assignment_event;");

        var result = await Verify(database);
        var diagnostic = result.ToString();

        Assert.DoesNotContain("PRIVATE-DESCRIPTION-CANARY", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("-1000", diagnostic, StringComparison.Ordinal);
        Assert.Equal("pool_assignment", result.SafeType);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private DurableLedgerVerifier Verifier() => new(protection);

    private Task<DurableLedgerVerificationResult> Verify(LedgerDb database) =>
        Verifier().VerifyAsync(database, expectedDatabaseChecksum: null, CancellationToken.None);

    private async Task<LedgerDb> Candidate(string name = "candidate")
    {
        var database = new LedgerDb(Path.Combine(root, name), Guid.NewGuid().ToString("N"));
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);
        return database;
    }

    private async Task CopyDatabase(LedgerDb source, LedgerDb target)
    {
        await using var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using var targetConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await sourceConnection.OpenAsync();
        await targetConnection.OpenAsync();
        sourceConnection.BackupDatabase(targetConnection);
        protection.ProtectArtifact(target.DatabasePath);
    }

    private async Task<string> SeedAccount(LedgerDb database)
    {
        var accountId = LedgerId.New().ToString();
        await Execute(database, """
            INSERT INTO account VALUES ($id, 'Bank', 'cheque', 'asset', $masked, 'ZAR', $at);
            INSERT INTO catalogue_lifecycle_event VALUES ($event, 'account', $id, 'create', NULL, 'Account', 'account', NULL, 'test', $at, NULL);
            """, ("$id", accountId), ("$masked", accountId[^4..]), ("$event", LedgerId.New().ToString()), ("$at", At));
        return accountId;
    }

    private async Task<string> SeedTransaction(LedgerDb database, string accountId, long amount, string description = "Transaction")
    {
        var transactionId = LedgerId.New().ToString();
        await Execute(database, """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transaction, $account, $amount, 'ZAR', '2026-07-22', NULL, $description, $at, 'test');
            INSERT INTO transaction_attribution_event VALUES ($attribution, $transaction, 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);
            INSERT INTO pool_assignment_event VALUES ($pool, $transaction, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', $at);
            """, ("$transaction", transactionId), ("$account", accountId), ("$amount", amount), ("$description", description), ("$attribution", LedgerId.New().ToString()), ("$pool", LedgerId.New().ToString()), ("$at", At));
        return transactionId;
    }

    private async Task<string> SeedCategory(LedgerDb database, string categoryId, string? parentId, string name)
    {
        var parentEvent = LedgerId.New().ToString();
        await Execute(database, """
            INSERT INTO spend_category VALUES ($category, $at);
            INSERT INTO category_parent_event VALUES ($parentEvent, $category, $parent, 'initialize', 'initial', 'test', $at, NULL);
            INSERT INTO catalogue_lifecycle_event VALUES ($lifecycle, 'category', $category, 'create', NULL, $name, $normalized, NULL, 'test', $at, NULL);
            """, ("$category", categoryId), ("$parent", parentId), ("$parentEvent", parentEvent), ("$lifecycle", LedgerId.New().ToString()), ("$name", name), ("$normalized", name.ToLowerInvariant()), ("$at", At));
        return parentEvent;
    }

    private async Task<string> SeedRelationship(LedgerDb database, string type, string source, string target, long amount)
    {
        var relationshipId = LedgerId.New().ToString();
        var sourceRole = type == "transfer" ? "transfer_outflow" : "refund_original";
        var targetRole = type == "transfer" ? "transfer_inflow" : "refund_credit";
        await Execute(database, """
            INSERT INTO financial_relationship VALUES ($id, $type, $source, $sourceRole, $target, $targetRole, $amount, 'active', $at, 'test', NULL);
            """, ("$id", relationshipId), ("$type", type), ("$source", source), ("$sourceRole", sourceRole), ("$target", target), ("$targetRole", targetRole), ("$amount", amount), ("$at", At));
        return relationshipId;
    }

    private Task SeedIdempotency(LedgerDb database, string operationId, string? effectType, string? logicalIdentity)
    {
        var key = LedgerId.New().ToString();
        var effect = effectType is null
            ? string.Empty
            : "INSERT INTO logical_effect VALUES ($identity, $effectType, $key, $at);";
        return Execute(database, $$"""
            INSERT INTO idempotency_record VALUES ($key, '1.0' || char(10) || $operation, $hash, 'test', 'committed', '{"value":{},"errorCode":null}', $at);
            {{effect}}
            """, ("$key", key), ("$operation", operationId), ("$hash", new string('a', 64)), ("$identity", logicalIdentity), ("$effectType", effectType), ("$at", At));
    }

    private async Task<(string DecisionId, string EvidenceId)> SeedDecision(
        LedgerDb database,
        string transactionId,
        string evidenceKind,
        string policyId,
        string policyVersion,
        bool deterministic,
        string disposition)
    {
        var evidenceId = await SeedEvidence(database, evidenceKind);
        var decisionId = LedgerId.New().ToString();
        var baseDisposition = deterministic ? "deterministic_match" : "owner_confirmed";
        await Execute(database, """
            INSERT INTO reconciliation_decision VALUES ($decision, $evidence, $transaction, $baseDisposition, $policy, $version, 'basis', $deterministic, 'reason', 'test', $at, NULL);
            INSERT INTO reconciliation_decision_authority VALUES ($decision, $disposition, NULL, $transaction, $authority, NULL, 'v2', $at);
            """, ("$decision", decisionId), ("$evidence", evidenceId), ("$transaction", transactionId), ("$baseDisposition", baseDisposition), ("$policy", policyId), ("$version", policyVersion), ("$deterministic", deterministic ? 1 : 0), ("$disposition", disposition), ("$authority", deterministic ? "deterministic_policy" : "owner"), ("$at", At));
        return (decisionId, evidenceId);
    }

    private async Task<string> SeedEvidence(LedgerDb database, string kind)
    {
        var evidenceId = LedgerId.New().ToString();
        await Execute(database, """
            INSERT INTO evidence_record VALUES ($id, $kind, $digest, NULL, NULL, 'test', $at);
            """, ("$id", evidenceId), ("$kind", kind), ("$digest", "digest-" + evidenceId), ("$at", At));
        return evidenceId;
    }

    private async Task Execute(LedgerDb database, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Checksum(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }
}
